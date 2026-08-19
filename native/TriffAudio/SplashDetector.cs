using System;
using System.Collections.Generic;
using System.Linq;

namespace TriffView.Audio;

/// <summary>
/// Matches a buffer of samples against a set of splash templates, scoring by the maximum
/// normalised dot product between the query patch and each template's patch, over every
/// 250 ms-spaced window in the buffer. Pure BCL, so it links into the cross-platform test
/// project like <see cref="SplashFeatures"/>.
/// </summary>
public sealed class SplashDetector
{
    /// <summary>250 ms between evaluated window starts, in frames (47 at 16 kHz / hop 85).</summary>
    private static readonly int StepFrames =
        Math.Max(1, (int)Math.Round(0.25 * SplashFeatures.SampleRate / SplashFeatures.HopSize));

    private readonly IReadOnlyList<SplashTemplate> _templates;

    public SplashDetector(IReadOnlyList<SplashTemplate> templates)
    {
        _templates = templates;
    }

    public int TemplateCount => _templates.Count;

    /// <summary>
    /// Scores <paramref name="samples"/> against every template, deriving context statistics
    /// (median/MAD) from the trailing <see cref="SplashFeatures.ContextFrames"/> of the buffer
    /// itself. Use the overload with caller-supplied context when the buffer is much shorter
    /// than 30 s, or the context stats will be computed over an unrepresentative window.
    /// </summary>
    public double Score(ReadOnlySpan<float> samples)
    {
        var bands = SplashFeatures.ComputeBands(samples);
        var frameCount = bands.GetLength(1);
        SplashFeatures.ComputeContextStats(bands, frameCount, out var median, out var mad);
        return ScoreCore(bands, median, mad);
    }

    /// <summary>
    /// Scores <paramref name="samples"/> against every template using caller-supplied context
    /// statistics (e.g. a template's own stored median/MAD, for an identity check; or a live
    /// running estimate, for continuous detection).
    /// </summary>
    public double Score(ReadOnlySpan<float> samples, float[] median, float[] mad)
    {
        var bands = SplashFeatures.ComputeBands(samples);
        return ScoreCore(bands, median, mad);
    }

    /// <summary>
    /// Scores an already-built patch (see <see cref="SplashFeatures.BuildPatch"/>) directly,
    /// without computing bands at all. For a caller maintaining its own rolling spectrogram
    /// (<see cref="RollingBandBuffer"/>) and building one patch per tick from it - recomputing
    /// <see cref="SplashFeatures.ComputeBands"/> over a whole buffer on every call, the way the
    /// two overloads above do, is the wrong shape for that caller: it repeats work its rolling
    /// buffer already did incrementally.
    /// </summary>
    public double ScorePatch(float[] patch)
    {
        var best = double.NegativeInfinity;
        foreach (var template in _templates)
        {
            var dot = Dot(patch, template.Patch);
            if (dot > best)
                best = dot;
        }

        return best;
    }

    /// <summary>
    /// Evaluates every 250 ms across the whole buffer and returns up to <paramref name="maxResults"/>
    /// candidates, highest score first, each at least <paramref name="minSeparationSeconds"/> apart.
    /// Deliberately applies no threshold: this feeds the template-capture UI, whose purpose is to
    /// surface candidates the live detector would otherwise reject.
    /// </summary>
    public IReadOnlyList<(double Score, double OffsetSeconds)> RankWindows(
        ReadOnlySpan<float> samples, int maxResults, double minSeparationSeconds)
        => RankWindows(samples, maxResults, minSeparationSeconds, out _, out _);

    /// <summary>
    /// Same ranking as the overload above, but also hands back the context median/MAD this call
    /// derived from <paramref name="samples"/> - the template-capture flow (Task 9) must persist
    /// a saved template's statistics alongside the exact audio they were computed from, and a
    /// second, separate call over the (live, still-filling) ring buffer could pair a candidate
    /// with different context than it was ranked against.
    /// </summary>
    public IReadOnlyList<(double Score, double OffsetSeconds)> RankWindows(
        ReadOnlySpan<float> samples, int maxResults, double minSeparationSeconds,
        out float[] median, out float[] mad)
    {
        var bands = SplashFeatures.ComputeBands(samples);
        var frameCount = bands.GetLength(1);
        SplashFeatures.ComputeContextStats(bands, frameCount, out median, out mad);

        var candidates = new List<(double Score, double OffsetSeconds)>();
        for (var startFrame = 0; startFrame + SplashFeatures.WindowFrames <= frameCount; startFrame += StepFrames)
        {
            var score = ScoreWindow(bands, startFrame, median, mad);
            var offsetSeconds = startFrame * (double)SplashFeatures.HopSize / SplashFeatures.SampleRate;
            candidates.Add((score, offsetSeconds));
        }

        candidates.Sort((a, b) => b.Score.CompareTo(a.Score));

        var results = new List<(double Score, double OffsetSeconds)>();
        foreach (var candidate in candidates)
        {
            if (results.Count >= maxResults)
                break;

            var tooClose = results.Any(r => Math.Abs(r.OffsetSeconds - candidate.OffsetSeconds) < minSeparationSeconds);
            if (tooClose)
                continue;

            results.Add(candidate);
        }

        return results;
    }

    /// <summary>
    /// Best score over every window in the buffer, spaced <see cref="StepFrames"/> apart. The
    /// upper bound is exclusive (<c>start + WindowFrames &lt; frameCount</c>), matching the
    /// reference implementation's <c>range(0, frames - WindowFrames, step)</c>; including the
    /// final exactly-fitting window changes the scores of the accuracy fixtures. A buffer too
    /// short to produce any window is still scored once, at frame 0, so an exactly-window-length
    /// buffer (a template scored against itself) does not fall through to negative infinity.
    /// </summary>
    private double ScoreCore(float[,] bands, float[] median, float[] mad)
    {
        var frameCount = bands.GetLength(1);

        var best = double.NegativeInfinity;
        var scoredAny = false;
        for (var startFrame = 0; startFrame + SplashFeatures.WindowFrames < frameCount; startFrame += StepFrames)
        {
            var score = ScoreWindow(bands, startFrame, median, mad);
            if (score > best)
                best = score;
            scoredAny = true;
        }

        return scoredAny ? best : ScoreWindow(bands, 0, median, mad);
    }

    private double ScoreWindow(float[,] bands, int startFrame, float[] median, float[] mad)
    {
        var patch = SplashFeatures.BuildPatch(bands, startFrame, median, mad);

        var best = double.NegativeInfinity;
        foreach (var template in _templates)
        {
            var dot = Dot(patch, template.Patch);
            if (dot > best)
                best = dot;
        }

        return best;
    }

    private static double Dot(float[] a, float[] b)
    {
        double sum = 0.0;
        var n = Math.Min(a.Length, b.Length);
        for (var i = 0; i < n; i++)
            sum += (double)a[i] * b[i];
        return sum;
    }
}
