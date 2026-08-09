using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace TriffView.TriffSkills;

internal static class TriffSkillsPaths
{
    private static string? _rootOverride;

    // Test-only seam. Production code never calls this, so the real root is
    // always %APPDATA%\TriffHud\TriffSkills\ unless a harness redirects it.
    public static void OverrideRoot(string root) => _rootOverride = root;

    public static string Root => _rootOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TriffHud",
        "TriffSkills"
    );

    public static string StatePath => Path.Combine(Root, "state.json");
    public static string SkillIdsPath => Path.Combine(Root, "skill-ids.json");
    public static string PlansDir => Path.Combine(Root, "plans");
}

internal static class TriffSkillsJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };
}

internal sealed class TriffSkillsCharacter
{
    public long CharacterId { get; set; }
    public string CharacterName { get; set; } = "";
    public List<string> Scopes { get; set; } = new();
    public DateTimeOffset AuthenticatedUtc { get; set; }
    public DateTimeOffset? FetchedUtc { get; set; }
    public Dictionary<int, int> TrainedLevels { get; set; } = new();   // typeID -> level
    public List<QueueEntry> Queue { get; set; } = new();
    public string Error { get; set; } = "";
    public bool NeedsReauth { get; set; }
}

internal sealed class TriffSkillsState
{
    public List<TriffSkillsCharacter> Characters { get; set; } = new();
    public long SelectedCharacterId { get; set; }

    public static TriffSkillsState Load()
    {
        try
        {
            if (!File.Exists(TriffSkillsPaths.StatePath)) return new TriffSkillsState();
            var state = JsonSerializer.Deserialize<TriffSkillsState>(
                File.ReadAllText(TriffSkillsPaths.StatePath),
                TriffSkillsJson.Options
            );
            return state?.Normalize() ?? new TriffSkillsState();
        }
        catch
        {
            return new TriffSkillsState();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(TriffSkillsPaths.Root);
        var json = JsonSerializer.Serialize(Normalize(), TriffSkillsJson.Options);

        // Diverges from EveSettingsLocalState.Save (EveSettingsController.cs:1136-1140),
        // which calls File.WriteAllText directly. state.json holds every character's
        // last-good skills and queue, so a crash mid-write would cost real data that
        // only a fresh ESI round-trip can rebuild. Writing a sibling temp file and
        // calling File.Replace makes the swap atomic: a crash leaves the previous
        // file intact rather than a truncated one.
        // A fixed ".tmp" name is a shared resource: two saves overlapping - a refresh pass
        // saving per character while the user forgets one - would write the same path and
        // one File.Replace would fail or consume the other's bytes. A unique name per save
        // keeps concurrent saves from touching each other; the last Replace wins, which is
        // the intended semantics for a full-state snapshot.
        var tempPath = $"{TriffSkillsPaths.StatePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(tempPath, json, new UTF8Encoding(false));
            if (File.Exists(TriffSkillsPaths.StatePath))
            {
                File.Replace(tempPath, TriffSkillsPaths.StatePath, null);
            }
            else
            {
                File.Move(tempPath, TriffSkillsPaths.StatePath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A transient failure here - antivirus holding the file, a locked profile
            // directory - must not abort the refresh pass that called Save(). The
            // in-memory state is still correct and the next Save will retry; losing the
            // write is recoverable, losing the pass is not.
            Debug.WriteLine($"TriffSkills: state save failed: {ex.Message}");
            TryDelete(tempPath);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Leaving a stray temp file behind is strictly better than throwing out of
            // a cleanup path that is itself handling a failure.
        }
    }

    public TriffSkillsState Normalize()
    {
        var deduped = new Dictionary<long, TriffSkillsCharacter>();
        foreach (var character in Characters ?? new List<TriffSkillsCharacter>())
        {
            if (character == null || character.CharacterId <= 0) continue;
            character.CharacterName = character.CharacterName?.Trim() ?? "";
            character.Scopes ??= new List<string>();
            character.TrainedLevels ??= new Dictionary<int, int>();
            character.Queue ??= new List<QueueEntry>();
            character.Error = character.Error?.Trim() ?? "";
            deduped[character.CharacterId] = character;   // last wins
        }

        Characters = deduped.Values.ToList();
        if (!deduped.ContainsKey(SelectedCharacterId))
        {
            SelectedCharacterId = Characters.FirstOrDefault()?.CharacterId ?? 0;
        }

        return this;
    }

    public TriffSkillsCharacter Upsert(long characterId)
    {
        var existing = Characters.FirstOrDefault(character => character.CharacterId == characterId);
        if (existing != null) return existing;

        var added = new TriffSkillsCharacter { CharacterId = characterId };
        Characters.Add(added);
        return added;
    }

    // Both Apply* methods below look the character up instead of upserting it, and do
    // nothing when it is gone. A refresh pass awaits ESI per character, and Forget
    // character can complete during that await - upserting here would then re-add a
    // character the user just deleted, with its credential already destroyed, leaving a
    // permanently broken row that only another Forget can clear. Adding characters is the
    // authorization path's job; a fetch result may only update one that still exists.
    private TriffSkillsCharacter? Find(long characterId)
    {
        return characterId <= 0
            ? null
            : Characters.FirstOrDefault(character => character.CharacterId == characterId);
    }

    public void ApplyFetchSuccess(long characterId, Dictionary<int, int>? trainedLevels, List<QueueEntry>? queue)
    {
        var character = Find(characterId);
        if (character == null) return;

        // Copied rather than aliased: the caller's collections are its own locals and
        // nothing stops it mutating them after this returns, which would silently edit
        // persisted state behind Save()'s back.
        character.TrainedLevels = trainedLevels == null
            ? new Dictionary<int, int>()
            : new Dictionary<int, int>(trainedLevels);
        character.Queue = queue == null ? new List<QueueEntry>() : new List<QueueEntry>(queue);
        character.FetchedUtc = DateTimeOffset.UtcNow;
        character.Error = "";
        character.NeedsReauth = false;
    }

    public void ApplyFetchFailure(long characterId, string error, bool needsReauth)
    {
        var character = Find(characterId);
        if (character == null) return;

        // Deliberately leaves TrainedLevels, Queue and FetchedUtc untouched, so the
        // last-good record stays visible and the UI can label it stale by FetchedUtc.
        character.Error = string.IsNullOrWhiteSpace(error) ? "ESI request failed." : error.Trim();
        character.NeedsReauth = needsReauth;
    }
}
