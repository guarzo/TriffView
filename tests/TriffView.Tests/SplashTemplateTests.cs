using System;
using System.Linq;
using TriffView.Audio;
using Xunit;

public class SplashTemplateTests
{
    private static float[] Ramp(int n)
    {
        var s = new float[n];
        for (var i = 0; i < n; i++) s[i] = (float)Math.Sin(2 * Math.PI * 440.0 * i / 16000.0);
        return s;
    }

    [Fact]
    public void WavRoundTripsWithinQuantisationError()
    {
        var src = Ramp(16000);
        var wav = SplashTemplate.WriteWav(src, out var gain);
        var round = SplashTemplate.ReadWavMono16(wav);
        Assert.Equal(src.Length, round.Length);
        for (var i = 0; i < src.Length; i++)
            Assert.InRange(round[i] / gain - src[i], -0.001f, 0.001f);   // undo the write gain
    }

    [Fact]
    public void FromWav_ProducesNormalisedPatch()
    {
        var samples = Ramp(32000);                               // exactly 2 s
        var bands = SplashFeatures.ComputeBands(samples);
        SplashFeatures.ComputeContextStats(bands, bands.GetLength(1), out var med, out var mad);

        var wav = SplashTemplate.WriteWav(samples, out var gain);
        var t = SplashTemplate.FromWav("splash-01", "test", true,
                    wav, SplashTemplate.WriteStatsJson(gain, med, mad));

        Assert.NotNull(t);
        Assert.Equal(SplashFeatures.BandCount * SplashFeatures.WindowFrames, t!.Patch.Length);
        Assert.Equal(1.0, Math.Sqrt(t.Patch.Sum(v => (double)v * v)), 3);
    }

    [Fact]
    public void FromWav_ReturnsNullForUnknownVersion()
    {
        var samples = Ramp(32000);
        var bad = System.Text.Encoding.UTF8.GetBytes(
            "{\"version\":99,\"sampleRate\":16000,\"gain\":1.0,\"median\":[],\"mad\":[]}");
        Assert.Null(SplashTemplate.FromWav("x", "x", false,
            SplashTemplate.WriteWav(samples, out _), bad));
    }

    [Fact]
    public void FromWav_ReturnsNullForOutOfRangeNumericLiteral()
    {
        // Syntactically valid JSON, but "version" overflows Int32: JsonElement.GetInt32()
        // throws FormatException here rather than InvalidOperationException, which must
        // also be swallowed so a corrupt user-supplied file degrades to "skipped".
        var samples = Ramp(32000);
        var bad = System.Text.Encoding.UTF8.GetBytes(
            "{\"version\":99999999999999999999,\"sampleRate\":16000,\"gain\":1.0,\"median\":[],\"mad\":[]}");
        Assert.Null(SplashTemplate.FromWav("x", "x", false,
            SplashTemplate.WriteWav(samples, out _), bad));
    }
}
