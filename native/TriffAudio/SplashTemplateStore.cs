using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace TriffView.Audio;

/// <summary>
/// Loads the shipped wormhole-splash templates (embedded resources) plus any templates the
/// user has recorded, and lets the settings UI add or remove user templates. Built-in templates
/// can never be deleted.
///
/// User templates live as `{id}.wav` / `{id}.json` pairs in <see cref="_userTemplateDirectory"/>.
/// The directory is not created until the first <see cref="Save"/> - an app that never uses this
/// feature must not leave an empty folder behind in %APPDATA%.
/// </summary>
internal sealed class SplashTemplateStore
{
    private readonly string _userTemplateDirectory;

    // A reference swap, not a mutated-in-place list: TriffAudioService's detection tick reads
    // Templates from a thread other than whichever one calls Save/Delete/Reload, and used to hold
    // a live reference into a List<SplashTemplate> that Reload cleared and repopulated under it -
    // "Collection was modified" thrown mid-scan, or worse, a torn read racing the resize. Every
    // read here sees either the old, complete array or the new one, never a partial one.
    private volatile IReadOnlyList<SplashTemplate> _templates = Array.Empty<SplashTemplate>();

    public SplashTemplateStore(string userTemplateDirectory)
    {
        _userTemplateDirectory = userTemplateDirectory;
        Reload();
    }

    public IReadOnlyList<SplashTemplate> Templates => _templates;

    public void Reload()
    {
        var loaded = new List<SplashTemplate>();
        LoadBuiltIns(loaded);
        LoadUserTemplates(loaded);
        _templates = loaded;
    }

    public string Save(string name, ReadOnlySpan<float> samples, float[] median, float[] mad)
    {
        Directory.CreateDirectory(_userTemplateDirectory);

        var id = "user-" + Guid.NewGuid().ToString("N");
        var wav = SplashTemplate.WriteWav(samples, out var gain);
        var json = BuildStatsJson(gain, median, mad, name);

        File.WriteAllBytes(Path.Combine(_userTemplateDirectory, id + ".wav"), wav);
        File.WriteAllBytes(Path.Combine(_userTemplateDirectory, id + ".json"), json);

        Reload();
        return id;
    }

    public bool Delete(string id)
    {
        var current = _templates;
        var existing = current.FirstOrDefault(t => t.Id == id);
        if (existing is null || existing.BuiltIn)
            return false;

        var wavPath = Path.Combine(_userTemplateDirectory, id + ".wav");
        var jsonPath = Path.Combine(_userTemplateDirectory, id + ".json");

        var deletedAny = false;
        if (File.Exists(wavPath)) { File.Delete(wavPath); deletedAny = true; }
        if (File.Exists(jsonPath)) { File.Delete(jsonPath); deletedAny = true; }

        if (deletedAny)
            _templates = current.Where(t => t.Id != id).ToList();

        return deletedAny;
    }

    /// <summary>
    /// Reads the 11 shipped templates from embedded resources. Pairs each `.wav` resource with
    /// its `.json` sibling by matching everything before the extension, rather than assuming the
    /// exact namespace-mangled prefix MSBuild produces for the embed - that mangling is easy to
    /// get subtly wrong and this makes the pairing independent of it.
    /// </summary>
    private static void LoadBuiltIns(List<SplashTemplate> target)
    {
        var assembly = typeof(SplashTemplateStore).Assembly;
        var resourceNames = assembly.GetManifestResourceNames();

        foreach (var wavName in resourceNames)
        {
            if (!wavName.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
                continue;

            var stem = wavName[..^".wav".Length];
            var jsonName = stem + ".json";
            if (!resourceNames.Contains(jsonName))
            {
                TriffViewDiagnostics.Log("splash-templates", $"built-in resource '{wavName}' has no matching '{jsonName}'; skipped.");
                continue;
            }

            // The id is the filename stem, e.g. "splash-01" out of "...splash-templates.splash-01.wav".
            var id = stem.Split('.')[^1];

            byte[] wavBytes;
            byte[] jsonBytes;
            try
            {
                wavBytes = ReadResource(assembly, wavName);
                jsonBytes = ReadResource(assembly, jsonName);
            }
            catch (Exception ex) when (ex is IOException or NotSupportedException)
            {
                TriffViewDiagnostics.Log("splash-templates", $"failed to read built-in resource '{wavName}': {ex.Message}");
                continue;
            }

            var template = SplashTemplate.FromWav(id, id, builtIn: true, wavBytes, jsonBytes);
            if (template is null)
            {
                TriffViewDiagnostics.Log("splash-templates", $"built-in template '{id}' failed to parse; skipped.");
                continue;
            }

            target.Add(template);
        }

        // A broken embed (a rename, a glob regression, a packaging change) must never silently
        // leave the detector with nothing to match against - that fails as "no splashes, ever",
        // which is indistinguishable from a quiet evening. Log loudly, but do not throw: a
        // broken embed should degrade to no detection, not stop the app from starting.
        if (target.Count(t => t.BuiltIn) == 0)
        {
            TriffViewDiagnostics.Log(
                "splash-templates-critical",
                "no built-in splash templates loaded; splash detection is disabled until this is fixed.");
        }
    }

    /// <summary>
    /// Reads user-added templates from <see cref="_userTemplateDirectory"/>. Users can put
    /// arbitrary files there, so every failure mode here is a skip-and-log, never a throw:
    /// a `.wav` with no matching `.json`, a `.json` with no `.wav`, an unreadable file, or a
    /// pair that <see cref="SplashTemplate.FromWav"/> rejects as malformed.
    /// </summary>
    private void LoadUserTemplates(List<SplashTemplate> target)
    {
        // The whole body is one try/catch, not just the individual file reads below: Directory.Exists
        // above a Directory.EnumerateFiles call is inherently TOCTOU (the directory can vanish or
        // become unreadable between the two), and a case-only filename collision makes the
        // ToDictionary calls throw ArgumentException. This runs from the constructor, off the
        // calling thread (see TriffAudioService's constructor), so an escape here is an unhandled
        // exception on a thread-pool thread - and, worse, Lazy<T> would cache and rethrow it on
        // every subsequent access. A broken user-template directory must degrade to "no user
        // templates", the same policy LoadBuiltIns already applies to a broken embed, not to a
        // crash loop.
        try
        {
            if (!Directory.Exists(_userTemplateDirectory))
                return;

            var wavPaths = Directory.EnumerateFiles(_userTemplateDirectory, "*.wav")
                .ToDictionary(p => Path.GetFileNameWithoutExtension(p)!, p => p, StringComparer.OrdinalIgnoreCase);
            var jsonPaths = Directory.EnumerateFiles(_userTemplateDirectory, "*.json")
                .ToDictionary(p => Path.GetFileNameWithoutExtension(p)!, p => p, StringComparer.OrdinalIgnoreCase);

            var ids = wavPaths.Keys.Union(jsonPaths.Keys, StringComparer.OrdinalIgnoreCase);

            foreach (var id in ids)
            {
                if (!wavPaths.TryGetValue(id, out var wavPath))
                {
                    TriffViewDiagnostics.Log("splash-templates", $"user template '{id}' has a stats file but no matching .wav; skipped.");
                    continue;
                }

                if (!jsonPaths.TryGetValue(id, out var jsonPath))
                {
                    TriffViewDiagnostics.Log("splash-templates", $"user template '{id}' has a .wav but no matching stats file; skipped.");
                    continue;
                }

                byte[] wavBytes;
                byte[] jsonBytes;
                try
                {
                    wavBytes = File.ReadAllBytes(wavPath);
                    jsonBytes = File.ReadAllBytes(jsonPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    TriffViewDiagnostics.Log("splash-templates", $"failed to read user template '{id}': {ex.Message}");
                    continue;
                }

                var name = TryReadName(jsonBytes) ?? id;
                var template = SplashTemplate.FromWav(id, name, builtIn: false, wavBytes, jsonBytes);
                if (template is null)
                {
                    TriffViewDiagnostics.Log("splash-templates", $"user template '{id}' failed to parse; skipped.");
                    continue;
                }

                target.Add(template);
            }
        }
        catch (Exception ex)
        {
            TriffViewDiagnostics.Log("splash-templates", $"failed to load user templates from '{_userTemplateDirectory}': {ex.Message}");
        }
    }

    private static byte[] ReadResource(Assembly assembly, string name)
    {
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new IOException($"Resource '{name}' not found.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>
    /// Same schema as <see cref="SplashTemplate.WriteStatsJson"/> (version/sampleRate/gain/
    /// median/mad, so <see cref="SplashTemplate.FromWav"/> can read it back) plus a "name" field
    /// FromWav ignores; the store reads it back itself in <see cref="TryReadName"/> since user
    /// templates need a display name and FromWav has no way to hand one back.
    /// </summary>
    private static byte[] BuildStatsJson(double gain, float[] median, float[] mad, string name)
    {
        var payload = new
        {
            version = 1,
            sampleRate = SplashFeatures.SampleRate,
            gain,
            median,
            mad,
            name,
        };
        return JsonSerializer.SerializeToUtf8Bytes(payload);
    }

    private static string? TryReadName(byte[] statsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(statsJson);
            if (doc.RootElement.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
                return nameEl.GetString();
        }
        catch (JsonException)
        {
            // Malformed JSON is handled by FromWav's own parse; here we just fall back to the id.
        }

        return null;
    }
}
