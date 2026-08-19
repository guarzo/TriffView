using System;
using TriffView.Audio;
using Xunit;

public class SplashDetectorTests
{
    [Fact]
    public void ScoresAShippedTemplateAgainstItselfNearOne()
    {
        var t = TestTemplates.LoadShipped();
        var det = new SplashDetector(t);
        Assert.Equal(11, det.TemplateCount);

        // Use the template's own stored context (30 s median/MAD), not context derived from
        // the 2 s clip: Score(samples) alone would compute context over a much shorter window
        // than the patch was built from, which is a different space and not reliably > 0.95
        // even for a correct detector.
        var samples = TestTemplates.LoadShippedSamples(0);
        var (median, mad) = TestTemplates.LoadShippedStats(0);
        Assert.True(det.Score(samples, median, mad) > 0.95, "a template must match itself");
    }

    [Fact]
    public void ScoresSilenceFarBelowThreshold()
    {
        var det = new SplashDetector(TestTemplates.LoadShipped());
        Assert.True(det.Score(new float[SplashFeatures.SampleRate * 3]) < 0.35);
    }

    [Fact]
    public void RankWindowsReturnsSeparatedCandidatesWithoutThresholding()
    {
        var det = new SplashDetector(TestTemplates.LoadShipped());
        var rng = new Random(7);
        var buf = new float[SplashFeatures.SampleRate * 30];
        for (var i = 0; i < buf.Length; i++) buf[i] = (float)(rng.NextDouble() - 0.5) * 0.01f;

        var top = det.RankWindows(buf, 3, 2.0);
        Assert.Equal(3, top.Count);                                   // never thresholded
        for (var i = 1; i < top.Count; i++)
            Assert.True(Math.Abs(top[i].OffsetSeconds - top[i - 1].OffsetSeconds) >= 2.0);
        Assert.True(top[0].Score >= top[1].Score);
    }
}
