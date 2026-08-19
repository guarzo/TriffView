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

    /// <summary>Valid stats for a Ramp(32000) clip, so a test can vary exactly one thing.</summary>
    private static byte[] GoodStatsJson(float[] samples, double gain)
    {
        var bands = SplashFeatures.ComputeBands(samples);
        SplashFeatures.ComputeContextStats(bands, bands.GetLength(1), out var med, out var mad);
        return SplashTemplate.WriteStatsJson(gain, med, mad);
    }

    /// <summary>
    /// FromWav must reject every malformed input by returning null rather than throwing:
    /// SplashTemplateStore relies on that to skip one bad user file instead of aborting the
    /// whole load. A throw from any of these fails the test on its own.
    /// </summary>
    [Theory]
    [InlineData("{not json at all")]
    [InlineData("")]
    [InlineData("[1,2,3]")]                                       // valid JSON, non-object root
    [InlineData("\"a string\"")]                                  // ditto
    [InlineData("{\"version\":1,\"sampleRate\":16000,\"median\":[],\"mad\":[]}")]              // no gain
    [InlineData("{\"version\":1,\"sampleRate\":16000,\"gain\":0.0,\"median\":[],\"mad\":[]}")]  // gain not positive
    [InlineData("{\"version\":1,\"sampleRate\":16000,\"gain\":-2.0,\"median\":[],\"mad\":[]}")]
    [InlineData("{\"version\":1,\"sampleRate\":16000,\"gain\":1.0,\"median\":\"nope\",\"mad\":[]}")] // median not an array
    public void FromWav_ReturnsNullForMalformedStats(string statsJson)
    {
        var wav = SplashTemplate.WriteWav(Ramp(32000), out _);
        Assert.Null(SplashTemplate.FromWav("x", "x", false,
            wav, System.Text.Encoding.UTF8.GetBytes(statsJson)));
    }

    [Theory]
    [InlineData(0)]                                               // empty
    [InlineData(SplashFeatures.BandCount - 1)]                    // too short
    [InlineData(SplashFeatures.BandCount + 1)]                    // too long
    public void FromWav_ReturnsNullForWrongLengthStatsArrays(int length)
    {
        var samples = Ramp(32000);
        var wav = SplashTemplate.WriteWav(samples, out var gain);
        var wrong = new float[length];
        var good = new float[SplashFeatures.BandCount];

        Assert.Null(SplashTemplate.FromWav("x", "x", false,
            wav, SplashTemplate.WriteStatsJson(gain, wrong, good)));
        Assert.Null(SplashTemplate.FromWav("x", "x", false,
            wav, SplashTemplate.WriteStatsJson(gain, good, wrong)));
    }

    [Fact]
    public void FromWav_ReturnsNullForAudioThatIsNotAUsableWav()
    {
        var samples = Ramp(32000);
        SplashTemplate.WriteWav(samples, out var gain);
        var stats = GoodStatsJson(samples, gain);

        Assert.Null(SplashTemplate.FromWav("x", "x", false, Array.Empty<byte>(), stats));
        Assert.Null(SplashTemplate.FromWav("x", "x", false,
            System.Text.Encoding.UTF8.GetBytes("this is not a RIFF file at all"), stats));

        // A real WAV truncated mid-header: plausible for a file written by a crashed process.
        var truncated = SplashTemplate.WriteWav(samples, out _).AsSpan(0, 20).ToArray();
        Assert.Null(SplashTemplate.FromWav("x", "x", false, truncated, stats));
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
