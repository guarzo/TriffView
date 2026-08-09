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
        var tempPath = TriffSkillsPaths.StatePath + ".tmp";
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

    public void ApplyFetchSuccess(long characterId, Dictionary<int, int> trainedLevels, List<QueueEntry> queue)
    {
        if (characterId <= 0) return;
        var character = Upsert(characterId);
        character.TrainedLevels = trainedLevels;
        character.Queue = queue;
        character.FetchedUtc = DateTimeOffset.UtcNow;
        character.Error = "";
        character.NeedsReauth = false;
    }

    public void ApplyFetchFailure(long characterId, string error, bool needsReauth)
    {
        if (characterId <= 0) return;
        var character = Upsert(characterId);
        // Deliberately leaves TrainedLevels, Queue and FetchedUtc untouched, so the
        // last-good record stays visible and the UI can label it stale by FetchedUtc.
        character.Error = string.IsNullOrWhiteSpace(error) ? "ESI request failed." : error.Trim();
        character.NeedsReauth = needsReauth;
    }
}
