using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace TriffView;

/// <summary>
/// Aggregated performance counters for the diagnostics log.
///
/// This exists because every performance measurement this project has is from one 24-core
/// workstation where the app costs 0.06% of the machine, while the reports come from user
/// hardware nobody has measured. Reasoning about the gap from here has no evidence behind it,
/// so the app records its own timings and users can send them.
///
/// Two constraints shape the design:
///
/// - The diagnostics log is capped at 2MB with a single rotation, and it carries the geometry
///   and layout-write entries (with full stack traces) it was built for. Logging per paint
///   would evict those within minutes. So samples are accumulated in memory and flushed as one
///   summary line per bucket on a slow cadence.
/// - Measuring must not become the thing being measured. Recording a sample is a Stopwatch
///   timestamp difference and a dictionary update; the dictionary is concurrent because paints
///   arrive on the UI thread while the window sweep runs on a thread-pool thread.
///
/// Buckets report count / mean / max, and optionally a summed quantity such as clip area, which
/// is what distinguishes "this paint was expensive" from "this paint covered the whole desktop".
/// </summary>
internal static class TriffViewPerfLog
{
    private sealed class Bucket
    {
        public long Count;
        public long TotalTicks;
        public long MaxTicks;
        public long Quantity;
    }

    private static readonly ConcurrentDictionary<string, Bucket> Buckets = new();
    private static readonly object FlushGate = new();
    private static long _lastFlushTicks = Stopwatch.GetTimestamp();

    /// <summary>
    /// How often accumulated counters are written out. Slow enough that an unattended session
    /// cannot fill the 2MB log with summaries, frequent enough that a user who notices a stutter
    /// and sends the log still has the window it happened in.
    /// </summary>
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(60);

    /// <summary>Starts a measurement. Pair with <see cref="Stop"/>.</summary>
    internal static long Start() => Stopwatch.GetTimestamp();

    /// <summary>
    /// Records one sample against <paramref name="bucket"/>. <paramref name="quantity"/> is an
    /// optional dimension summed across samples - clip area in pixels, windows visited, and so on.
    /// </summary>
    internal static void Stop(string bucket, long startTimestamp, long quantity = 0)
    {
        var elapsed = Stopwatch.GetTimestamp() - startTimestamp;
        if (elapsed < 0) elapsed = 0;

        var entry = Buckets.GetOrAdd(bucket, _ => new Bucket());
        lock (entry)
        {
            entry.Count++;
            entry.TotalTicks += elapsed;
            if (elapsed > entry.MaxTicks) entry.MaxTicks = elapsed;
            entry.Quantity += quantity;
        }
    }

    /// <summary>
    /// Writes and clears the accumulated counters if the flush interval has passed.
    ///
    /// Called from work the app already does periodically rather than from a timer of its own -
    /// a diagnostics feature that adds a wakeup to an idle app would be measuring a cost it
    /// created. Silent when nothing was recorded, so an idle session logs nothing at all.
    /// </summary>
    internal static void FlushIfDue() => Flush(force: false);

    /// <summary>
    /// Whether a flush is due, without taking the gate or writing anything.
    ///
    /// Callers on the UI thread use this to decide whether to hand the actual write to a pool
    /// thread: <see cref="Flush"/> ends in file I/O, and the point of these counters is to find
    /// UI-thread stalls, not add one. Deliberately unsynchronized - an aligned 64-bit read is
    /// atomic, and a stale answer only costs a dispatch that <see cref="Flush"/> then re-checks
    /// under the gate and no-ops.
    /// </summary>
    internal static bool IsFlushDue()
    {
        var elapsed = (Stopwatch.GetTimestamp() - _lastFlushTicks) / (double)Stopwatch.Frequency;
        return elapsed >= FlushInterval.TotalSeconds;
    }

    /// <summary>
    /// Writes whatever has accumulated regardless of the interval. Called on shutdown so a
    /// session shorter than one flush interval still reports something - a user who launches
    /// the app, sees it stutter and quits in irritation is exactly the person whose counters
    /// are worth having, and they would otherwise be discarded on exit.
    /// </summary>
    internal static void FlushFinal() => Flush(force: true);

    private static void Flush(bool force)
    {
        lock (FlushGate)
        {
            var now = Stopwatch.GetTimestamp();
            var elapsed = TimeSpan.FromSeconds((now - _lastFlushTicks) / (double)Stopwatch.Frequency);
            if (!force && elapsed < FlushInterval) return;
            _lastFlushTicks = now;

            var snapshot = new List<KeyValuePair<string, Bucket>>();
            foreach (var key in Buckets.Keys)
            {
                if (!Buckets.TryRemove(key, out var bucket)) continue;
                if (bucket.Count > 0) snapshot.Add(new KeyValuePair<string, Bucket>(key, bucket));
            }

            if (snapshot.Count == 0) return;

            var builder = new StringBuilder();
            builder.Append($"window={elapsed.TotalSeconds:F0}s");
            foreach (var pair in snapshot.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                var b = pair.Value;
                var meanUs = b.TotalTicks * 1_000_000.0 / Stopwatch.Frequency / b.Count;
                var maxUs = b.MaxTicks * 1_000_000.0 / Stopwatch.Frequency;
                builder.Append($" | {pair.Key}: n={b.Count} meanUs={meanUs:F0} maxUs={maxUs:F0}");
                if (b.Quantity != 0) builder.Append($" qty={b.Quantity}");
            }

            TriffViewDiagnostics.Log("perf", builder.ToString());
        }
    }
}
