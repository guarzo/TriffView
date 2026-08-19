using System.Text.Json;
using TriffView.Audio;
using Xunit;

public class SplashTemplateStoreTests
{
    [Fact]
    public void LoadsElevenBuiltInTemplates()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tv-tmpl-" + Guid.NewGuid().ToString("N"));
        var store = new SplashTemplateStore(dir);
        Assert.Equal(11, store.Templates.Count(t => t.BuiltIn));
    }

    [Fact]
    public void SavedTemplateSurvivesReloadAndCanBeDeleted()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tv-tmpl-" + Guid.NewGuid().ToString("N"));
        var store = new SplashTemplateStore(dir);
        var samples = new float[SplashFeatures.SampleRate * 2];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = (float)Math.Sin(2 * Math.PI * 300.0 * i / SplashFeatures.SampleRate);
        var bands = SplashFeatures.ComputeBands(samples);
        SplashFeatures.ComputeContextStats(bands, bands.GetLength(1), out var med, out var mad);

        var id = store.Save("mine", samples, med, mad);
        store.Reload();
        Assert.Contains(store.Templates, t => t.Id == id && !t.BuiltIn);

        Assert.True(store.Delete(id));
        store.Reload();
        Assert.DoesNotContain(store.Templates, t => t.Id == id);
    }

    [Fact]
    public void DeleteRefusesBuiltInTemplates()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tv-tmpl-" + Guid.NewGuid().ToString("N"));
        var store = new SplashTemplateStore(dir);
        var builtIn = store.Templates.First(t => t.BuiltIn);
        Assert.False(store.Delete(builtIn.Id));
    }

    [Fact]
    public void CorruptUserTemplateIsSkippedNotFatal()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tv-tmpl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "broken.wav"), new byte[] { 1, 2, 3 });
        File.WriteAllText(Path.Combine(dir, "broken.json"), "not json");
        var store = new SplashTemplateStore(dir);          // must not throw
        Assert.Equal(11, store.Templates.Count);
    }

    [Fact]
    public void OrphanWavWithNoJsonIsSkippedNotFatal()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tv-tmpl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "orphan.wav"), new byte[] { 1, 2, 3 });
        var store = new SplashTemplateStore(dir);          // must not throw
        Assert.Equal(11, store.Templates.Count);
    }

    [Fact]
    public void OrphanJsonWithNoWavIsSkippedNotFatal()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tv-tmpl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "orphan.json"), "{\"version\":1}");
        var store = new SplashTemplateStore(dir);          // must not throw
        Assert.Equal(11, store.Templates.Count);
    }

    [Fact]
    public void ZeroByteUserFilesAreSkippedNotFatal()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tv-tmpl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "empty.wav"), Array.Empty<byte>());
        File.WriteAllBytes(Path.Combine(dir, "empty.json"), Array.Empty<byte>());
        var store = new SplashTemplateStore(dir);          // must not throw
        Assert.Equal(11, store.Templates.Count);
    }

    [Fact]
    public void SaveAppliesNonTrivialGainAndPatchRoundTrips()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tv-tmpl-" + Guid.NewGuid().ToString("N"));
        var store = new SplashTemplateStore(dir);

        // Peak ~0.005 -> WriteWav's gain lands near 1/0.005 = 200. A bug that persisted a
        // hardcoded 1.0 instead of the computed gain would not be caught by a full-scale clip
        // (gain there is already ~1.0) - this is exactly the bug class that cost four rounds of
        // debugging in Task 4.
        var samples = new float[SplashFeatures.SampleRate * 2];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = 0.005f * (float)Math.Sin(2 * Math.PI * 300.0 * i / SplashFeatures.SampleRate);

        var bandsRaw = SplashFeatures.ComputeBands(samples);
        SplashFeatures.ComputeContextStats(bandsRaw, bandsRaw.GetLength(1), out var med, out var mad);
        var expectedPatch = SplashFeatures.BuildPatch(bandsRaw, 0, med, mad);

        var id = store.Save("quiet", samples, med, mad);
        store.Reload();

        var persistedGain = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(dir, id + ".json")))
            .RootElement.GetProperty("gain").GetDouble();
        Assert.True(persistedGain > 50.0, $"expected a large gain for a quiet clip, got {persistedGain}");

        // Proves the persisted gain round-trips correctly: the loaded template's patch must
        // match one built directly from the pre-normalised samples. A wrong/hardcoded gain would
        // fail to undo the write-time normalisation and produce a mismatched patch here.
        var saved = store.Templates.First(t => t.Id == id);
        for (var i = 0; i < expectedPatch.Length; i++)
            Assert.InRange(saved.Patch[i] - expectedPatch[i], -0.01f, 0.01f);
    }
}
