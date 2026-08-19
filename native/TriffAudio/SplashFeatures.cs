using System;

namespace TriffView.Audio;

/// <summary>
/// Pure spectral feature extraction for wormhole-splash audio detection. No Windows
/// dependencies: everything here is BCL math so it can run on both the native app and the
/// cross-platform test project.
/// </summary>
public static class SplashFeatures
{
    public const int SampleRate = 16000;
    public const int FftSize = 1024;
    public const int HopSize = 85;
    public const int BandCount = 32;
    public const double BandMinHz = 80.0;
    public const double BandMaxHz = 7600.0;
    public const int WindowFrames = 376;          // 2.0 s
    public const int ContextFrames = 5647;        // 30 s
    public const double MadFloor = 1e-3;

    /// <summary>
    /// Computes a log-magnitude, geometrically-banded spectrogram from raw samples.
    /// Returns a [BandCount, frames] array where frames = floor((samples.Length - FftSize) / HopSize) + 1.
    /// </summary>
    public static float[,] ComputeBands(ReadOnlySpan<float> samples)
    {
        // Trailing frames whose window runs past the end of the buffer are zero-padded rather
        // than dropped, so frame count is simply samples.Length / HopSize.
        var frameCount = samples.Length / HopSize;

        var bands = new float[BandCount, Math.Max(frameCount, 0)];
        if (frameCount <= 0) return bands;

        var window = BuildHannWindow(FftSize);
        var edges = BuildBandBinEdges();

        // scipy.signal.stft scales its output by 1/win.sum() (amplitude-spectrum scaling); an
        // unnormalized FFT is louder by exactly that factor (~54 dB for this periodic Hann, whose
        // coefficients sum to N/2), which uniformly inflates every band's dB value relative to
        // the median/mad the reference implementation computed. Match that scaling here.
        var windowSum = 0.0;
        for (var i = 0; i < window.Length; i++)
            windowSum += window[i];

        var re = new double[FftSize];
        var im = new double[FftSize];

        for (var frame = 0; frame < frameCount; frame++)
        {
            var offset = frame * HopSize;
            for (var i = 0; i < FftSize; i++)
            {
                var sampleIndex = offset + i;
                var sample = sampleIndex < samples.Length ? samples[sampleIndex] : 0f;
                re[i] = sample * window[i];
                im[i] = 0.0;
            }

            Fft(re, im);

            // Magnitude spectrum, positive frequencies only (bins 0..FftSize/2).
            var half = FftSize / 2;
            var mag = new double[half + 1];
            for (var i = 0; i <= half; i++)
                mag[i] = Math.Sqrt(re[i] * re[i] + im[i] * im[i]) / windowSum;

            for (var b = 0; b < BandCount; b++)
            {
                var lo = edges[b];
                var hi = edges[b + 1];
                double sum = 0.0;
                var count = 0;
                for (var bin = lo; bin < hi; bin++)
                {
                    sum += mag[bin];
                    count++;
                }

                var mean = count > 0 ? sum / count : 0.0;
                bands[b, frame] = (float)(20.0 * Math.Log10(mean + 1e-10));
            }
        }

        return bands;
    }

    /// <summary>
    /// Computes per-band median and median-absolute-deviation over the trailing
    /// <see cref="ContextFrames"/> frames ending at <paramref name="endFrame"/> (exclusive).
    /// Uses whatever frames are available if fewer than ContextFrames exist.
    /// </summary>
    public static void ComputeContextStats(float[,] bands, int endFrame, out float[] median, out float[] mad)
    {
        var bandCount = bands.GetLength(0);
        median = new float[bandCount];
        mad = new float[bandCount];

        var startFrame = Math.Max(0, endFrame - ContextFrames);
        var count = endFrame - startFrame;
        if (count <= 0)
        {
            for (var b = 0; b < bandCount; b++)
                mad[b] = (float)MadFloor;
            return;
        }

        var values = new double[count];
        var deviations = new double[count];

        for (var b = 0; b < bandCount; b++)
        {
            for (var i = 0; i < count; i++)
                values[i] = bands[b, startFrame + i];

            var med = Median(values);
            median[b] = (float)med;

            for (var i = 0; i < count; i++)
                deviations[i] = Math.Abs(values[i] - med);

            var m = Median(deviations);
            mad[b] = (float)Math.Max(m, MadFloor);
        }
    }

    /// <summary>
    /// Builds a normalized (zero-mean, unit-L2-norm) patch of z-scored band energy over
    /// <see cref="WindowFrames"/> frames starting at <paramref name="startFrame"/>, flattened row-major.
    /// </summary>
    public static float[] BuildPatch(float[,] bands, int startFrame, float[] median, float[] mad)
    {
        var bandCount = bands.GetLength(0);
        var frameCount = bands.GetLength(1);
        var patch = new float[bandCount * WindowFrames];

        var idx = 0;
        for (var b = 0; b < bandCount; b++)
        {
            var m = median[b];
            var d = mad[b];
            for (var f = 0; f < WindowFrames; f++)
            {
                var frame = startFrame + f;
                var value = frame >= 0 && frame < frameCount ? bands[b, frame] : m;
                patch[idx++] = (float)((value - m) / d);
            }
        }

        double mean = 0.0;
        for (var i = 0; i < patch.Length; i++)
            mean += patch[i];
        mean /= patch.Length;

        double normSq = 0.0;
        for (var i = 0; i < patch.Length; i++)
        {
            patch[i] -= (float)mean;
            normSq += (double)patch[i] * patch[i];
        }

        var norm = Math.Sqrt(normSq);
        if (norm < 1e-9)
        {
            Array.Clear(patch, 0, patch.Length);
            return patch;
        }

        for (var i = 0; i < patch.Length; i++)
            patch[i] = (float)(patch[i] / norm);

        return patch;
    }

    private static double Median(double[] values)
    {
        var sorted = (double[])values.Clone();
        Array.Sort(sorted);
        var n = sorted.Length;
        if (n == 0) return 0.0;
        return n % 2 == 1
            ? sorted[n / 2]
            : (sorted[n / 2 - 1] + sorted[n / 2]) / 2.0;
    }

    private static double[] BuildHannWindow(int size)
    {
        // Periodic Hann (divide by size, not size-1) to match scipy.signal.stft's default
        // (get_window('hann', nperseg, fftbins=True)), which generated the committed
        // template assets. Do not "correct" this to the symmetric variant.
        var window = new double[size];
        for (var i = 0; i < size; i++)
            window[i] = 0.5 - 0.5 * Math.Cos(2.0 * Math.PI * i / size);
        return window;
    }

    /// <summary>
    /// Maps BandCount geometric band edges (BandMinHz..BandMaxHz) onto FFT bin indices
    /// (0..FftSize/2), returning BandCount+1 edges.
    /// </summary>
    private static int[] BuildBandBinEdges()
    {
        var edges = new int[BandCount + 1];
        var half = FftSize / 2;
        var binHz = (double)SampleRate / FftSize;

        for (var b = 0; b <= BandCount; b++)
        {
            var ratio = (double)b / BandCount;
            var hz = BandMinHz * Math.Pow(BandMaxHz / BandMinHz, ratio);
            var bin = (int)Math.Ceiling(hz / binHz);
            edges[b] = Math.Clamp(bin, 0, half);
        }

        // Ensure strictly non-decreasing edges (low bands can collapse to the same bin at
        // this FFT resolution, which is expected: those bands legitimately contain no bins).
        for (var b = 1; b <= BandCount; b++)
            if (edges[b] < edges[b - 1]) edges[b] = edges[b - 1];

        return edges;
    }

    /// <summary>
    /// In-place radix-2 Cooley-Tukey FFT. Length must be a power of two.
    /// </summary>
    private static void Fft(double[] re, double[] im)
    {
        var n = re.Length;

        // Bit-reversal permutation.
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
                j ^= bit;
            j ^= bit;

            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }

        for (var len = 2; len <= n; len <<= 1)
        {
            var ang = -2.0 * Math.PI / len;
            var wRe = Math.Cos(ang);
            var wIm = Math.Sin(ang);

            for (var i = 0; i < n; i += len)
            {
                double curWRe = 1.0, curWIm = 0.0;
                for (var k = 0; k < len / 2; k++)
                {
                    var uRe = re[i + k];
                    var uIm = im[i + k];
                    var vRe = re[i + k + len / 2] * curWRe - im[i + k + len / 2] * curWIm;
                    var vIm = re[i + k + len / 2] * curWIm + im[i + k + len / 2] * curWRe;

                    re[i + k] = uRe + vRe;
                    im[i + k] = uIm + vIm;
                    re[i + k + len / 2] = uRe - vRe;
                    im[i + k + len / 2] = uIm - vIm;

                    var nextWRe = curWRe * wRe - curWIm * wIm;
                    var nextWIm = curWRe * wIm + curWIm * wRe;
                    curWRe = nextWRe;
                    curWIm = nextWIm;
                }
            }
        }
    }
}
