using System;

namespace TriffView.Audio;

/// <summary>
/// Fixed-capacity circular buffer of mono float samples, feeding the live splash detector a
/// trailing window of audio. Pure BCL (Array, Math), so it links into the cross-platform test
/// project like <see cref="SplashFeatures"/>.
///
/// Written by the capture thread roughly every 10 ms and read by the detection timer at 4 Hz, so
/// <see cref="Write"/> and <see cref="Read"/> lock around the shared state. Without it, a Read
/// racing a Write could observe the array mid-copy and hand the detector a window with old and
/// new samples interleaved at the wrong positions — silent corruption that looks exactly like a
/// detection-accuracy problem rather than the threading bug it actually is. Contention is a
/// non-issue at these rates.
/// </summary>
public sealed class AudioRingBuffer
{
    private readonly object _gate = new();
    private readonly float[] _buffer;
    private int _writePos;
    private bool _full;
    private long _totalWritten;
    private long _nonZeroInLastWrite;

    public AudioRingBuffer(int capacitySamples)
    {
        if (capacitySamples <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacitySamples));

        _buffer = new float[capacitySamples];
    }

    /// <summary>Total samples ever written, not clamped to capacity.</summary>
    public long TotalWritten { get { lock (_gate) return _totalWritten; } }

    /// <summary>
    /// Count of non-zero samples in the most recent <see cref="Write"/> call only (not
    /// cumulative). A muted WASAPI process delivers zero-filled buffers without ever setting
    /// AUDCLNT_BUFFERFLAGS_SILENT, so this is the only reliable way to detect silence. Read from
    /// a different thread than the one that writes it (the detection timer decides whether to
    /// show a client as muted), so this getter locks like everything else here rather than being
    /// a plain auto-property read.
    /// </summary>
    public long NonZeroSamplesInLastWrite { get { lock (_gate) return _nonZeroInLastWrite; } }

    public void Write(ReadOnlySpan<float> samples)
    {
        var nonZero = 0L;
        foreach (var sample in samples)
            if (sample != 0f)
                nonZero++;

        lock (_gate)
        {
            _nonZeroInLastWrite = nonZero;
            _totalWritten += samples.Length;

            // A single incoming span can exceed capacity; only its tail (the most recent
            // capacity-worth of samples) can ever be read back, so skip the rest without ever
            // writing it.
            if (samples.Length >= _buffer.Length)
            {
                samples = samples[^_buffer.Length..];
                samples.CopyTo(_buffer);
                _writePos = 0;
                _full = true;
                return;
            }

            var firstChunk = Math.Min(samples.Length, _buffer.Length - _writePos);
            samples[..firstChunk].CopyTo(_buffer.AsSpan(_writePos));

            var remaining = samples.Length - firstChunk;
            if (remaining > 0)
            {
                samples[firstChunk..].CopyTo(_buffer);
                _writePos = remaining;
                _full = true;
            }
            else
            {
                _writePos += firstChunk;
                if (_writePos == _buffer.Length)
                {
                    _writePos = 0;
                    _full = true;
                }
            }

            if (!_full && _totalWritten >= _buffer.Length)
                _full = true;
        }
    }

    /// <summary>
    /// Copies the most recent <c>min(destination.Length, available)</c> samples into
    /// <paramref name="destination"/>, oldest first, and returns the count copied.
    /// </summary>
    public int Read(Span<float> destination)
    {
        lock (_gate)
        {
            var available = _full ? _buffer.Length : _writePos;
            var count = Math.Min(destination.Length, available);
            if (count == 0)
                return 0;

            // Oldest-first means starting at _writePos (the next slot to be overwritten, i.e. the
            // oldest retained sample) when full, or at 0 when not yet full (buffer never wrapped).
            var start = _full ? _writePos : 0;

            // Only the most recent `count` samples are wanted, which may be fewer than everything
            // retained; skip forward within the retained region so the tail (the very newest
            // samples, ending at the current write position) is what gets copied.
            var skip = available - count;
            start = (start + skip) % _buffer.Length;

            var firstChunk = Math.Min(count, _buffer.Length - start);
            _buffer.AsSpan(start, firstChunk).CopyTo(destination);
            if (firstChunk < count)
                _buffer.AsSpan(0, count - firstChunk).CopyTo(destination[firstChunk..]);

            return count;
        }
    }
}
