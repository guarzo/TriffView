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
}
