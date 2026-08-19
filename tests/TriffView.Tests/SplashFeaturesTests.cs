using TriffView.Audio;
using Xunit;

public class SplashFeaturesTests
{
    [Fact]
    public void ComputeBands_ReturnsExpectedShape()
    {
        var samples = new float[SplashFeatures.SampleRate];      // 1 s of silence
        var bands = SplashFeatures.ComputeBands(samples);
        Assert.Equal(SplashFeatures.BandCount, bands.GetLength(0));
        Assert.InRange(bands.GetLength(1), 180, 200);            // ~188 frames at hop 85
    }

    [Fact]
    public void ComputeBands_PutsToneEnergyInTheRightBand()
    {
        var samples = new float[SplashFeatures.SampleRate];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = (float)Math.Sin(2 * Math.PI * 1000.0 * i / SplashFeatures.SampleRate);
        var bands = SplashFeatures.ComputeBands(samples);

        // band index containing 1000 Hz, using the same geometric spacing
        var ratio = Math.Log(1000.0 / SplashFeatures.BandMinHz)
                  / Math.Log(SplashFeatures.BandMaxHz / SplashFeatures.BandMinHz);
        var expected = (int)(ratio * SplashFeatures.BandCount);

        var mid = bands.GetLength(1) / 2;
        var loudest = 0;
        for (var b = 1; b < SplashFeatures.BandCount; b++)
            if (bands[b, mid] > bands[loudest, mid]) loudest = b;
        Assert.InRange(loudest, expected - 1, expected + 1);
    }

    [Fact]
    public void BuildPatch_IsZeroMeanUnitNorm()
    {
        var rng = new Random(1);
        var samples = new float[SplashFeatures.SampleRate * 3];
        for (var i = 0; i < samples.Length; i++) samples[i] = (float)(rng.NextDouble() - 0.5);
        var bands = SplashFeatures.ComputeBands(samples);
        SplashFeatures.ComputeContextStats(bands, bands.GetLength(1), out var med, out var mad);
        var patch = SplashFeatures.BuildPatch(bands, 0, med, mad);

        Assert.Equal(SplashFeatures.BandCount * SplashFeatures.WindowFrames, patch.Length);
        Assert.Equal(0.0, patch.Sum(), 3);
        Assert.Equal(1.0, Math.Sqrt(patch.Sum(v => (double)v * v)), 3);
    }

    [Fact]
    public void ComputeContextStats_FloorsMadForEmptyBands()
    {
        var bands = new float[SplashFeatures.BandCount, 100];   // all identical -> MAD would be 0
        SplashFeatures.ComputeContextStats(bands, 100, out _, out var mad);
        Assert.All(mad, m => Assert.True(m >= SplashFeatures.MadFloor));
    }
}
