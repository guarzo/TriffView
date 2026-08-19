using System;
using TriffView.Audio;
using Xunit;

public class RollingBandBufferTests
{
    [Fact]
    public void IncrementalAppendMatchesBatchComputeBandsForCompleteFrames()
    {
        var rng = new Random(11);
        var totalSamples = SplashFeatures.SampleRate * 5; // 5 s
        var samples = new float[totalSamples];
        for (var i = 0; i < totalSamples; i++)
            samples[i] = (float)Math.Sin(2 * Math.PI * 440.0 * i / SplashFeatures.SampleRate)
                       + (float)(rng.NextDouble() - 0.5) * 0.05f;

        var expected = SplashFeatures.ComputeBands(samples);
        var expectedFrameCount = expected.GetLength(1);

        // Deliberately irregular, non-hop-aligned chunk sizes to exercise the "leftover raw
        // audio carried forward" path, not just the easy case of chunks that happen to divide
        // evenly into whole frames.
        var buffer = new RollingBandBuffer(expectedFrameCount + 16);
        var offset = 0;
        var chunkSizes = new[] { 137, 500, 63, 1000, 29, 4096, 1 };
        var chunkIndex = 0;
        while (offset < samples.Length)
        {
            var size = Math.Min(chunkSizes[chunkIndex % chunkSizes.Length], samples.Length - offset);
            buffer.Append(samples.AsSpan(offset, size));
            offset += size;
            chunkIndex++;
        }

        // The very last frame(s), whose window would need samples past the end of the stream,
        // are never emitted (there is no "flush" - see Append's doc comment), so the incremental
        // buffer trails the batch computation by a few frames at most.
        Assert.True(buffer.TotalFrames <= expectedFrameCount);
        Assert.True(buffer.TotalFrames >= expectedFrameCount - (SplashFeatures.FftSize / SplashFeatures.HopSize) - 1);

        var actual = buffer.ReadRecent((int)buffer.TotalFrames);
        for (var f = 0; f < actual.GetLength(1); f++)
        {
            for (var b = 0; b < SplashFeatures.BandCount; b++)
                Assert.Equal(expected[b, f], actual[b, f], 3);
        }
    }

    [Fact]
    public void ReadRecentReturnsTheNewestColumnsOldestFirst()
    {
        var buffer = new RollingBandBuffer(SplashFeatures.WindowFrames + 10);
        var samples = new float[SplashFeatures.SampleRate * 3];
        var rng = new Random(3);
        for (var i = 0; i < samples.Length; i++)
            samples[i] = (float)(rng.NextDouble() - 0.5);

        buffer.Append(samples);
        Assert.True(buffer.TotalFrames > SplashFeatures.WindowFrames);

        var recent = buffer.ReadRecent(SplashFeatures.WindowFrames);
        Assert.Equal(SplashFeatures.WindowFrames, recent.GetLength(1));

        // The buffer's capacity (WindowFrames + 10) is smaller than TotalFrames, so ReadRecent
        // must be pulling from the circular tail, not from the start of some larger backing
        // array. The other test establishes that buffer frame index n corresponds to the same
        // absolute frame as SplashFeatures.ComputeBands's frame n over the same raw samples, so
        // the newest WindowFrames columns are exactly frames [TotalFrames-WindowFrames, TotalFrames).
        var expected = SplashFeatures.ComputeBands(samples);
        var totalFrames = (int)buffer.TotalFrames;

        for (var f = 0; f < recent.GetLength(1); f++)
        {
            var expectedFrame = totalFrames - SplashFeatures.WindowFrames + f;
            for (var b = 0; b < SplashFeatures.BandCount; b++)
                Assert.Equal(expected[b, expectedFrame], recent[b, f], 3);
        }
    }

    [Fact]
    public void ReadRecentBeforeAnyAppendReturnsEmpty()
    {
        var buffer = new RollingBandBuffer(100);
        var recent = buffer.ReadRecent(10);
        Assert.Equal(0, recent.GetLength(1));
    }
}
