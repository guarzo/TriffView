using System.Text.Json;
using TriffView.Audio;
using Xunit;

/// <summary>
/// Locks in measured accuracy against the committed splash-accuracy fixtures (eight 3 s clips,
/// four confirmed splashes and four confirmed non-splashes, each user-listened and labelled).
/// A future change to the DSP that silently degrades accuracy fails here.
/// </summary>
public class SplashAccuracyTests
{
    [Theory]
    [InlineData("splash-a.wav", true, 0.6801)]
    [InlineData("splash-b.wav", true, 0.7087)]
    [InlineData("splash-c.wav", true, 0.6242)]
    [InlineData("splash-d.wav", true, 0.6737)]
    [InlineData("warp-a.wav", false, 0.1511)]
    [InlineData("warp-b.wav", false, 0.0703)]
    [InlineData("weapons-a.wav", false, 0.3183)]
    [InlineData("ambient-a.wav", false, 0.1528)]
    public void ClassifiesFixturesCorrectlyAtDefaultThreshold(string file, bool isSplash, double expected)
    {
        var det = new SplashDetector(TestTemplates.LoadShipped());
        var dir = Path.Combine(AppContext.BaseDirectory, "fixtures", "splash-accuracy");

        // A 3 s fixture has no 30 s context of its own. Its stored statistics MUST be
        // used; recomputing them from the clip collapses the separation (measured).
        using var meta = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(dir, Path.ChangeExtension(file, ".json"))));
        var root = meta.RootElement;
        var gain = root.GetProperty("gain").GetDouble();
        var median = root.GetProperty("median").EnumerateArray().Select(e => (float)e.GetDouble()).ToArray();
        var mad = root.GetProperty("mad").EnumerateArray().Select(e => (float)e.GetDouble()).ToArray();

        var samples = SplashTemplate.ReadWavMono16(File.ReadAllBytes(Path.Combine(dir, file)));
        for (var i = 0; i < samples.Length; i++) samples[i] = (float)(samples[i] / gain);

        var score = det.Score(samples, median, mad);
        if (isSplash) Assert.True(score >= 0.35, $"{file} scored {score:F3}, expected >= 0.35");
        else          Assert.True(score <  0.35, $"{file} scored {score:F3}, expected < 0.35");

        // Binary classification alone is too weak a guard: earlier broken rounds still passed
        // 6/8 while scores were far from the reference (e.g. splash-a at 0.395 and 0.360
        // against a reference of 0.694). Pin the actual value so a regression that keeps the
        // same side of the threshold still fails here.
        Assert.InRange(score, expected - 0.02, expected + 0.02);
    }
}
