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
    public void ScoresWindowZeroCorrectlyWhenNotTheOnlyWindow()
    {
        // A 376-frame buffer (WindowFrames) is exactly one window, so ScoresAShippedTemplateAgainstItselfNearOne
        // above cannot catch a single-window-scoring bug: it scores "window 0" whether the
        // implementation scans every window or only the trailing one. Append 2 s of silence so
        // the buffer is 752 frames, the scan evaluates starts 0..329, and window 0 (the real
        // splash) must still score ~1.0 even though it is no longer the trailing window.
        var det = new SplashDetector(TestTemplates.LoadShipped());
        var samples = TestTemplates.LoadShippedSamples(0);
        var (median, mad) = TestTemplates.LoadShippedStats(0);

        var padded = new float[samples.Length + SplashFeatures.SampleRate * 2];
        samples.CopyTo(padded, 0);

        Assert.True(det.Score(padded, median, mad) > 0.95,
            "window 0 must still score ~1.0 once it is no longer the trailing window");
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

        // Results come back score-ordered, not offset-ordered, so adjacent entries say nothing
        // about the separation guarantee - every pair has to be checked.
        for (var i = 0; i < top.Count; i++)
            for (var j = i + 1; j < top.Count; j++)
                Assert.True(
                    Math.Abs(top[i].OffsetSeconds - top[j].OffsetSeconds) >= 2.0,
                    $"candidates {i} ({top[i].OffsetSeconds}s) and {j} ({top[j].OffsetSeconds}s) are closer than 2s apart");

        for (var i = 1; i < top.Count; i++)
            Assert.True(
                top[i - 1].Score >= top[i].Score,
                $"candidate {i} scored {top[i].Score} above its predecessor's {top[i - 1].Score}");
    }
}
