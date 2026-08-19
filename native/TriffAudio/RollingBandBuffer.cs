using System;

namespace TriffView.Audio;

/// <summary>
/// Incrementally extends a per-band spectrogram as raw samples arrive in small chunks, rather
/// than recomputing <see cref="SplashFeatures.ComputeBands"/> over an entire rolling buffer on
/// every detection tick. That whole-buffer-per-tick shape is what a first cut of the live
/// detector actually did: "call Score once per client per tick" turned out to still mean
/// ~5,600 FFTs per client, four times a second, once the buffer handed to it was a 30 s ring
/// rather than a single 2 s clip. This class does the FFT work once, as each hop's worth of new
/// audio arrives (~188 times/second/client), and keeps only a fixed-size trailing window of the
/// result - so a detection tick can build a patch from already-computed bands and never touch
/// an FFT itself.
///
/// Pure BCL (Array, Math), so it links into the cross-platform test project like
/// <see cref="SplashFeatures"/>. Like <see cref="AudioRingBuffer"/>, <see cref="Append"/> runs on
/// the capture thread while <see cref="ReadRecent"/> and <see cref="TotalFrames"/> run on the
/// detection timer thread; every accessor here locks around the shared state for the same reason
/// that class's doc comment gives - an unsynchronized race here would present as a
/// detection-accuracy problem, not the threading bug it actually is.
/// </summary>
public sealed class RollingBandBuffer
{
    private readonly object _gate = new();
    private readonly int _capacityFrames;
    private readonly float[,] _bands; // circular: [band, slot]
    private float[] _pending = Array.Empty<float>();
    private int _writePos;
    private bool _full;
    private long _totalFrames;

    public RollingBandBuffer(int capacityFrames)
    {
        if (capacityFrames <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacityFrames));

        _capacityFrames = capacityFrames;
        _bands = new float[SplashFeatures.BandCount, capacityFrames];
    }

    /// <summary>Total frames ever appended, not clamped to capacity.</summary>
    public long TotalFrames { get { lock (_gate) return _totalFrames; } }

    /// <summary>
    /// Computes bands over (leftover-from-last-call + <paramref name="samples"/>), and appends
    /// only the frames whose FFT window is fully covered by real data - deferring any frame
    /// whose window would otherwise be zero-padded past the end of what has arrived so far to
    /// the next call, once more real data exists to compute it properly. The frame this defers is
    /// exactly the one <see cref="SplashFeatures.ComputeBands"/> would otherwise zero-pad, so
    /// nothing here duplicates or drops a frame relative to a single batch call over the same raw
    /// samples - only how the work is spread across calls differs.
    /// </summary>
    public void Append(ReadOnlySpan<float> samples)
    {
        var combined = new float[_pending.Length + samples.Length];
        _pending.CopyTo(combined, 0);
        samples.CopyTo(combined.AsSpan(_pending.Length));

        // How many frames combined can produce without zero-padding, computed by simple
        // arithmetic before touching ComputeBands at all - cheap, and lets us skip the call
        // entirely when nothing new is ready yet (e.g. a buffer still shorter than one FFT window).
        var usableFrameCount = 0;
        while (usableFrameCount * SplashFeatures.HopSize + SplashFeatures.FftSize <= combined.Length)
            usableFrameCount++;

        if (usableFrameCount > 0)
        {
            // ComputeBands over just enough of combined to produce those frames, not the whole
            // (typically much larger) buffer: passing the full buffer would have it also compute
            // - and then discard - however many additional trailing frames combined.Length allows,
            // each zero-padded and useless since none would pass the usable check above.
            var neededLength = (usableFrameCount - 1) * SplashFeatures.HopSize + SplashFeatures.FftSize;
            var bands = SplashFeatures.ComputeBands(combined.AsSpan(0, neededLength));

            lock (_gate)
            {
                for (var f = 0; f < usableFrameCount; f++)
                {
                    for (var b = 0; b < SplashFeatures.BandCount; b++)
                        _bands[b, _writePos] = bands[b, f];

                    _writePos++;
                    if (_writePos == _capacityFrames)
                    {
                        _writePos = 0;
                        _full = true;
                    }

                    _totalFrames++;
                }
            }
        }

        // Carry forward whatever raw audio was not yet turned into a frame - not a fixed-size
        // tail, but everything from the first unconsumed sample onward, since more than one
        // hop's worth of frames can be deferred when a batch is short. _pending is only ever
        // touched from Append (the capture thread), so it needs no lock of its own.
        var consumedSamples = usableFrameCount * SplashFeatures.HopSize;
        _pending = combined[consumedSamples..];
    }

    /// <summary>
    /// Copies the most recent <c>min(frameCount, available)</c> band columns into a fresh
    /// linear (oldest-first) array, converting out of the circular layout above - mirrors
    /// <see cref="AudioRingBuffer.Read"/> but for band columns rather than raw samples.
    /// </summary>
    public float[,] ReadRecent(int frameCount)
    {
        lock (_gate)
        {
            var available = _full ? _capacityFrames : _writePos;
            var count = Math.Min(frameCount, available);

            var result = new float[SplashFeatures.BandCount, count];
            if (count == 0)
                return result;

            var start = _full ? _writePos : 0;
            var skip = available - count;
            start = (start + skip) % _capacityFrames;

            for (var i = 0; i < count; i++)
            {
                var slot = (start + i) % _capacityFrames;
                for (var b = 0; b < SplashFeatures.BandCount; b++)
                    result[b, i] = _bands[b, slot];
            }

            return result;
        }
    }
}
