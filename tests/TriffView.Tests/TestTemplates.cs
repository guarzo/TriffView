using System;
using System.Collections.Generic;
using System.IO;
using TriffView.Audio;

/// <summary>
/// Loads the 11 committed splash templates shipped under
/// native/TriffAudio/Assets/splash-templates/. Tests execute from the build output directory,
/// which has no path back to the source tree, so the csproj copies the assets alongside the
/// test binaries (see the "templates" ItemGroup); this helper resolves them from there.
/// </summary>
public static class TestTemplates
{
    private const int Count = 11;

    public static IReadOnlyList<SplashTemplate> LoadShipped()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "templates");
        var templates = new List<SplashTemplate>();

        for (var i = 1; i <= Count; i++)
        {
            var id = $"splash-{i:D2}";
            var wav = File.ReadAllBytes(Path.Combine(dir, id + ".wav"));
            var statsJson = File.ReadAllBytes(Path.Combine(dir, id + ".json"));

            var template = SplashTemplate.FromWav(id, id, builtIn: true, wav, statsJson);
            if (template is null)
                throw new InvalidOperationException($"Shipped template '{id}' failed to load; a silently-empty list would make every downstream test meaningless.");

            templates.Add(template);
        }

        if (templates.Count != Count)
            throw new InvalidOperationException($"Expected {Count} shipped templates, found {templates.Count}.");

        return templates;
    }

    /// <summary>
    /// Returns the raw samples of the shipped template at <paramref name="index"/> (0-based),
    /// with the write gain already undone so they sit in the same amplitude space the
    /// template's patch was built from.
    /// </summary>
    public static float[] LoadShippedSamples(int index)
    {
        var id = $"splash-{index + 1:D2}";
        var dir = Path.Combine(AppContext.BaseDirectory, "templates");

        var wav = File.ReadAllBytes(Path.Combine(dir, id + ".wav"));
        var statsJson = File.ReadAllBytes(Path.Combine(dir, id + ".json"));

        var samples = SplashTemplate.ReadWavMono16(wav);

        using var doc = System.Text.Json.JsonDocument.Parse(statsJson);
        var gain = doc.RootElement.GetProperty("gain").GetDouble();

        for (var i = 0; i < samples.Length; i++)
            samples[i] = (float)(samples[i] / gain);

        return samples;
    }

    /// <summary>
    /// Returns the (median, mad) context statistics stored in the shipped template's sidecar
    /// JSON at <paramref name="index"/> (0-based) — the same statistics
    /// <see cref="SplashTemplate.FromWav"/> used to build that template's patch, for tests that
    /// need a true identity check rather than context derived from a short buffer.
    /// </summary>
    public static (float[] Median, float[] Mad) LoadShippedStats(int index)
    {
        var id = $"splash-{index + 1:D2}";
        var dir = Path.Combine(AppContext.BaseDirectory, "templates");
        var statsJson = File.ReadAllBytes(Path.Combine(dir, id + ".json"));

        using var doc = System.Text.Json.JsonDocument.Parse(statsJson);
        var root = doc.RootElement;

        var median = ReadFloatArray(root.GetProperty("median"));
        var mad = ReadFloatArray(root.GetProperty("mad"));
        return (median, mad);
    }

    private static float[] ReadFloatArray(System.Text.Json.JsonElement arrayEl)
    {
        var result = new float[arrayEl.GetArrayLength()];
        var i = 0;
        foreach (var el in arrayEl.EnumerateArray())
            result[i++] = (float)el.GetDouble();
        return result;
    }
}
