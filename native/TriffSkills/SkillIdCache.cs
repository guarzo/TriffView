using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace TriffView.TriffSkills;

// Name -> typeID map persisted at %APPDATA%\TriffHud\TriffSkills\skill-ids.json.
//
// This cache is load-bearing, not an optimisation. Skill names are
// immutable once resolved, so the map is written once and only unseen names are ever sent
// to ESI. Without it every plan load re-resolves every name in every plan and the ESI
// dependency is worse than the Fuzzworks invTypes.csv download it replaced. Do not add a
// TTL, do not clear it on refresh, do not bypass it.
internal sealed class SkillIdCache
{
    // POST /universe/ids/ declares maxItems: 500 and uniqueItems: true on its request body,
    // so names are de-duplicated (see Unresolved) and split into chunks of at most 500.
    // The batching exists to stay inside that request-size limit, not to be polite.
    public const int BatchSize = 500;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    // Plan files are hand-written and vary in casing ("Caldari Frigate" vs "caldari frigate"),
    // and ESI resolves names case-insensitively, so the map must too.
    public Dictionary<string, int> Map { get; } = new(StringComparer.OrdinalIgnoreCase);

    // Goes through TriffSkillsPaths rather than rebuilding the path by hand, so this
    // cache lands next to state.json under one root - including when a harness
    // redirects that root via TriffSkillsPaths.OverrideRoot.
    public static string CachePath => TriffSkillsPaths.SkillIdsPath;

    public static SkillIdCache Load()
    {
        try
        {
            return File.Exists(CachePath) ? FromJson(File.ReadAllText(CachePath)) : new SkillIdCache();
        }
        catch
        {
            // A corrupt cache costs one round of re-resolution, not a broken tool.
            return new SkillIdCache();
        }
    }

    public static SkillIdCache FromJson(string json)
    {
        var cache = new SkillIdCache();
        var map = JsonSerializer.Deserialize<Dictionary<string, int>>(json);
        foreach (var pair in map ?? new Dictionary<string, int>())
        {
            // Copied entry by entry rather than assigned: System.Text.Json builds its own
            // dictionary with the default comparer, which would lose case-insensitivity.
            if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value <= 0) continue;
            cache.Map[pair.Key.Trim()] = pair.Value;
        }

        return cache;
    }

    public void Save()
    {
        var path = CachePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // Unique per save for the same reason TriffSkillsState.Save uses one: a fixed
        // ".tmp" is shared state between concurrent saves. This cache is pure derived
        // data - re-resolvable from ESI - so a failed write is logged and dropped rather
        // than propagated into the refresh that triggered it.
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(Map, JsonOptions), new UTF8Encoding(false));

            // Write-temp-then-replace, so a crash mid-write leaves the previous file rather
            // than a truncated one. File.Replace requires the destination to exist.
            if (File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"TriffSkills: skill id cache save failed: {ex.Message}");
            try
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                // Nothing useful to do; a stray temp file is harmless.
            }
        }
    }

    public static List<string> Unresolved(IReadOnlyDictionary<string, int> map, IEnumerable<string> names)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();
        foreach (var name in names)
        {
            var trimmed = (name ?? "").Trim();
            if (trimmed.Length == 0) continue;
            if (map.ContainsKey(trimmed)) continue;
            if (!seen.Add(trimmed)) continue;
            missing.Add(trimmed);
        }

        return missing;
    }

    public static List<List<string>> Batch(IReadOnlyList<string> names, int batchSize)
    {
        var size = Math.Max(1, batchSize);
        var batches = new List<List<string>>();
        for (var start = 0; start < names.Count; start += size)
        {
            batches.Add(names.Skip(start).Take(size).ToList());
        }

        return batches;
    }

    public static int Merge(IDictionary<string, int> map, IEnumerable<SkillsUniverseIdName> resolved)
    {
        var added = 0;
        foreach (var entry in resolved)
        {
            if (string.IsNullOrWhiteSpace(entry.Name) || entry.Id <= 0) continue;
            var name = entry.Name.Trim();
            if (map.ContainsKey(name)) continue;
            map[name] = entry.Id;
            added++;
        }

        return added;
    }

    public async Task<int> ResolveMissingAsync(
        IEnumerable<string> names,
        Func<IReadOnlyList<string>, Task<IReadOnlyList<SkillsUniverseIdName>>> resolver,
        bool persist = true)
    {
        var missing = Unresolved(Map, names);
        if (missing.Count == 0) return 0;

        var added = 0;
        foreach (var batch in Batch(missing, BatchSize))
        {
            // Names ESI does not recognise are simply absent from the response. Merge only
            // adds what came back, so an unrecognised name stays out of the map forever and
            // surfaces downstream as an UnknownSkill rather than a satisfied requirement.
            var resolved = await resolver(batch);
            added += Merge(Map, resolved);
        }

        if (added > 0 && persist) Save();
        return added;
    }
}
