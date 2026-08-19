using System;
using System.Collections.Generic;
using System.Linq;

namespace TriffView.Audio;

/// <summary>
/// Matches a buffer of samples against a set of splash templates, scoring by the maximum
/// normalised dot product between the query patch and each template's patch. Pure BCL, so it
/// links into the cross-platform test project like <see cref="SplashFeatures"/>.
/// </summary>
public sealed class SplashDetector
{
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
        return ScoreCore(bands, frameCount - SplashFeatures.WindowFrames, median, mad);
    }

    /// <summary>
    /// Scores <paramref name="samples"/> against every template using caller-supplied context
    /// statistics (e.g. a template's own stored median/MAD, for an identity check; or a live
    /// running estimate, for continuous detection).
    /// </summary>
    public double Score(ReadOnlySpan<float> samples, float[] median, float[] mad)
    {
        var bands = SplashFeatures.ComputeBands(samples);
        var frameCount = bands.GetLength(1);
        return ScoreCore(bands, frameCount - SplashFeatures.WindowFrames, median, mad);
    }

    /// <summary>
    /// Evaluates every 250 ms across the whole buffer and returns up to <paramref name="maxResults"/>
    /// candidates, highest score first, each at least <paramref name="minSeparationSeconds"/> apart.
    /// Deliberately applies no threshold: this feeds the template-capture UI, whose purpose is to
    /// surface candidates the live detector would otherwise reject.
    /// </summary>
    public IReadOnlyList<(double Score, double OffsetSeconds)> RankWindows(
        ReadOnlySpan<float> samples, int maxResults, double minSeparationSeconds)
    {
        var bands = SplashFeatures.ComputeBands(samples);
        var frameCount = bands.GetLength(1);
        SplashFeatures.ComputeContextStats(bands, frameCount, out var median, out var mad);

        const double stepSeconds = 0.25;
        var stepFrames = Math.Max(1, (int)Math.Round(stepSeconds * SplashFeatures.SampleRate / SplashFeatures.HopSize));

        var candidates = new List<(double Score, double OffsetSeconds)>();
        for (var startFrame = 0; startFrame + SplashFeatures.WindowFrames <= frameCount; startFrame += stepFrames)
        {
            var score = ScoreCore(bands, startFrame, median, mad);
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

    private double ScoreCore(float[,] bands, int startFrame, float[] median, float[] mad)
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
