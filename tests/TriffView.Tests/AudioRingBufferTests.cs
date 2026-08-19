using TriffView.Audio;
using Xunit;

public class AudioRingBufferTests
{
    [Fact]
    public void ReadsMostRecentSamplesOldestFirst()
    {
        var rb = new AudioRingBuffer(4);
        rb.Write(new float[] { 1, 2, 3, 4, 5, 6 });
        var dst = new float[4];
        Assert.Equal(4, rb.Read(dst));
        Assert.Equal(new float[] { 3, 4, 5, 6 }, dst);
    }

    [Fact]
    public void ReadsFewerWhenNotYetFull()
    {
        var rb = new AudioRingBuffer(8);
        rb.Write(new float[] { 1, 2, 3 });
        var dst = new float[8];
        Assert.Equal(3, rb.Read(dst));
    }

    [Fact]
    public void TracksNonZeroSamplesForSilenceDetection()
    {
        var rb = new AudioRingBuffer(8);
        rb.Write(new float[] { 0, 0, 0, 0 });
        Assert.Equal(0, rb.NonZeroSamplesInLastWrite);
        rb.Write(new float[] { 0, 0.5f, 0, 0 });
        Assert.Equal(1, rb.NonZeroSamplesInLastWrite);
    }

    [Fact]
    public void HandlesWritesThatWrapAcrossTheCapacityBoundary()
    {
        // Neither a single write past capacity nor a not-yet-full buffer exercises the case
        // where a later write straddles the wrap point (part of it lands at the tail, part at
        // the head). Two writes totaling 5 samples into a capacity-4 buffer force that path.
        var rb = new AudioRingBuffer(4);
        rb.Write(new float[] { 1, 2, 3 });
        rb.Write(new float[] { 4, 5 });

        var dst = new float[4];
        Assert.Equal(4, rb.Read(dst));
        Assert.Equal(new float[] { 2, 3, 4, 5 }, dst);
    }

    [Fact]
    public void ReadsOnlyTheRequestedTailWhenDestinationIsSmallerThanAvailable()
    {
        var rb = new AudioRingBuffer(8);
        rb.Write(new float[] { 1, 2, 3, 4, 5, 6 });

        var dst = new float[3];
        Assert.Equal(3, rb.Read(dst));
        Assert.Equal(new float[] { 4, 5, 6 }, dst);
    }

    [Fact]
    public void TotalWrittenIsNotClampedToCapacity()
    {
        var rb = new AudioRingBuffer(4);
        rb.Write(new float[] { 1, 2, 3, 4, 5, 6, 7 });
        Assert.Equal(7, rb.TotalWritten);
    }
}
