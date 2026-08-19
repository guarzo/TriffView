using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace TriffView.Audio;

/// <summary>Point-in-time health of one captured EVE client, for the settings UI.</summary>
/// <param name="Status">One of "monitoring", "silent", "unavailable", "off".</param>
internal sealed record AudioClientStatus(uint ProcessId, string CharacterName, string Status);

/// <summary>
/// Owns one <see cref="WasapiProcessCapture"/>, one <see cref="AudioRingBuffer"/> (raw audio,
/// for the template-capture UI) and one <see cref="RollingBandBuffer"/> (incrementally-built
/// spectrogram, for live scoring) per EVE process, plus a single shared 4 Hz timer that scores
/// every client's newest 2 s window against <see cref="SplashTemplateStore"/>'s templates.
///
/// A first cut of this class scored each client by handing its whole 30 s ring to
/// <see cref="SplashDetector.Score(ReadOnlySpan{float})"/> once per tick. That call recomputes
/// <see cref="SplashFeatures.ComputeBands"/> - ~5,600 FFTs - over the entire buffer every time,
/// four times a second, per client; six clients could not keep up, and the scan-every-window
/// behaviour behind it made a single splash re-qualify against the alert threshold for the
/// whole 30 s it stayed in the ring, well past the alert's own cooldown. <see cref="OnSamples"/>
/// now feeds each hop's worth of audio straight into a <see cref="RollingBandBuffer"/> as it
/// arrives, and the tick below builds and scores exactly one patch - the newest window - per
/// client, via <see cref="SplashDetector.ScorePatch"/>.
///
/// Windows-only (it owns <see cref="WasapiProcessCapture"/> instances), so unlike the rest of
/// TriffAudio this does not link into the cross-platform test project; its tests live in
/// native/TriffView.Tests instead, exercising everything that does not need real audio via
/// <see cref="ForceDetectionPassForTests"/> and <see cref="FeedSamplesForTests"/>.
/// </summary>
internal sealed class TriffAudioService : IDisposable
{
    /// <summary>
    /// Exactly <see cref="SplashFeatures.ContextFrames"/> worth of samples (30.0 s). Doubles as
    /// both the raw ring buffer's capacity and (via <see cref="RollingBandBuffer.TotalFrames"/>)
    /// the warm-up threshold: below it, splash and non-splash scores do not separate (measured:
    /// 3 s of context put both classes in the 0.22-0.24 band), so no alert may fire.
    /// </summary>
    private const int RingCapacitySamples = SplashFeatures.ContextFrames * SplashFeatures.HopSize;

    private static readonly TimeSpan UnavailableIdleWindow = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SilentIdleWindow = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan DetectionInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// The rolling context median/MAD (a 32 x ContextFrames median sort, twice) is the one
    /// remaining expensive piece of a tick; the statistics are stable over a second by
    /// construction; recomputed at most this often per client and reused between ticks.
    /// </summary>
    private static readonly TimeSpan ContextStatsRefreshInterval = TimeSpan.FromSeconds(1);

    /// <summary>Backoff after a failed capture start: 1, 2, 4, 8, 15, 30 s, then holds at 30 s.
    /// Without this, a permanently-broken client retried every ~700 ms (the caller's typical
    /// SetClients cadence) forever, each attempt costing up to 5 s and a diagnostics-log line.</summary>
    private static readonly TimeSpan[] RetryBackoffSteps =
    {
        TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30),
    };

    private readonly object _gate = new();
    private readonly Dictionary<uint, ClientSession> _sessions = new();
    private readonly SplashTemplateStore _templateStore;
    private readonly System.Threading.Timer _detectionTimer;
    private readonly ConcurrentQueue<(string CharacterName, double Score)> _pendingDetections = new();

    /// <summary>Candidates handed to the template-capture UI, keyed by the id it was given -
    /// consumed (removed) by <see cref="SaveTemplate"/>. Never persisted: a restart or an unsaved
    /// capture simply expires it, which is fine since the UI holds no reference either.</summary>
    private readonly ConcurrentDictionary<string, PendingCandidate> _pendingCandidates = new();

    private readonly record struct PendingCandidate(float[] Samples, float[] Median, float[] Mad);

    private bool _enabled;
    private double _threshold = 0.35;
    private int _detectionTickInProgress;
    private int _detectionDispatchScheduled;
    private bool _disposed;

    public TriffAudioService(string? userTemplateDirectory = null)
    {
        var directory = string.IsNullOrWhiteSpace(userTemplateDirectory)
            ? DefaultUserTemplateDirectory()
            : userTemplateDirectory;

        _templateStore = new SplashTemplateStore(directory);

        // Runs from the moment the service exists rather than only once enabled: the tick
        // itself no-ops while disabled, and starting it unconditionally means UpdateSettings
        // never has to start or stop a timer, only flip a flag it reads.
        _detectionTimer = new System.Threading.Timer(OnDetectionTick, null, DetectionInterval, DetectionInterval);
    }

    /// <summary>Raised off any lock, on a thread-pool work item, never on the caller's thread.</summary>
    public event EventHandler<(string CharacterName, double Score)>? SplashDetected;

    public void UpdateSettings(bool enabled, double threshold)
    {
        lock (_gate)
        {
            _enabled = enabled;
            _threshold = threshold;
        }
    }

    /// <summary>
    /// Diffs against the currently-captured sessions: starts a capture for every new PID,
    /// disposes sessions for PIDs no longer present, and leaves everything else alone. A PID
    /// already capturing is never restarted here - only the detection tick tears a session's
    /// capture down (on the unavailable timeout), and only then does this method start it again.
    /// While disabled (<see cref="UpdateSettings"/>), <paramref name="clients"/> is treated as
    /// empty regardless of what the caller passes: every existing capture is torn down and none
    /// started, so a caller can call this unconditionally from its own refresh loop without
    /// checking the enabled flag itself.
    /// </summary>
    public void SetClients(IReadOnlyList<(uint ProcessId, string CharacterName)> clients)
    {
        var toDispose = new List<ClientSession>();
        var toStart = new List<ClientSession>();

        lock (_gate)
        {
            if (_disposed) return; // nothing started here would ever be torn down again

            // While splash detection is switched off, no client should have a live capture -
            // an unconditional diff here would open WASAPI process-loopback capture and run
            // per-packet FFT work for audio nobody is scoring, for a feature that defaults to
            // disabled. Treating the wanted set as empty whenever disabled reuses the diff
            // logic below to tear every existing session down and start none, rather than
            // adding a second enabled check at every call site that reaches SetClients.
            var effectiveClients = _enabled ? clients : Array.Empty<(uint ProcessId, string CharacterName)>();

            var wanted = new Dictionary<uint, string>();
            foreach (var client in effectiveClients)
                wanted[client.ProcessId] = client.CharacterName ?? "";

            foreach (var pid in _sessions.Keys.ToList())
            {
                if (wanted.ContainsKey(pid)) continue;
                toDispose.Add(_sessions[pid]);
                _sessions.Remove(pid);
            }

            var now = DateTime.UtcNow;
            foreach (var (pid, name) in wanted)
            {
                if (_sessions.TryGetValue(pid, out var existing))
                {
                    existing.CharacterName = name;
                    if (existing.Capture is null && !existing.StartInProgress && now >= existing.NextRetryUtc)
                    {
                        existing.StartInProgress = true;
                        toStart.Add(existing);
                    }
                    continue;
                }

                var session = new ClientSession(pid, name);
                session.StartInProgress = true;
                _sessions[pid] = session;
                toStart.Add(session);
            }
        }

        foreach (var session in toDispose)
            session.Dispose();

        // Start() blocks on WASAPI activation (up to 5 s on failure), so every attempt is queued
        // onto the thread pool rather than run inline: a caller driving this from a UI-thread
        // timer (as TriffViewSubsystem's periodic refresh does) would otherwise freeze for as
        // long as the slowest activation among however many clients just appeared.
        foreach (var session in toStart)
            ThreadPool.UnsafeQueueUserWorkItem(s => StartCapture(s), session, preferLocal: false);
    }

    public IReadOnlyList<AudioClientStatus> Statuses
    {
        get
        {
            lock (_gate)
            {
                var now = DateTime.UtcNow;
                var result = new List<AudioClientStatus>(_sessions.Count);
                foreach (var session in _sessions.Values)
                    result.Add(new AudioClientStatus(session.ProcessId, session.CharacterName, ComputeStatus(session, _enabled, now)));
                return result;
            }
        }
    }

    /// <summary>
    /// Ranks every 250 ms window across a client's currently buffered raw audio with no
    /// threshold applied, for the template-capture UI (Task 10) to offer as candidates -
    /// including ones the live detector currently scores below threshold and would never alert
    /// on. This is a full <see cref="SplashFeatures.ComputeBands"/> pass over up to 30 s of
    /// audio (unlike the live detection tick, which never recomputes bands from scratch) - call
    /// it from a background thread, never the dispatcher.
    /// </summary>
    public IReadOnlyList<(double Score, double OffsetSeconds, float[] Samples)> CaptureCandidates(uint processId, int maxResults)
    {
        var buffer = ReadRawBuffer(processId, out var count);
        if (count == 0)
            return Array.Empty<(double, double, float[])>();

        var windowSampleCount = SplashFeatures.WindowFrames * SplashFeatures.HopSize;
        var detector = SnapshotDetector();
        var ranked = detector.RankWindows(buffer.AsSpan(0, count), maxResults, minSeparationSeconds: SplashFeatures.WindowFrames * (double)SplashFeatures.HopSize / SplashFeatures.SampleRate);

        var results = new List<(double, double, float[])>(ranked.Count);
        foreach (var (score, offsetSeconds) in ranked)
        {
            var startSample = (int)Math.Round(offsetSeconds * SplashFeatures.SampleRate);
            var samples = new float[windowSampleCount];
            var available = Math.Clamp(count - startSample, 0, windowSampleCount);
            if (available > 0)
                Array.Copy(buffer, startSample, samples, 0, available);
            results.Add((score, offsetSeconds, samples));
        }

        return results;
    }

    /// <summary>
    /// Context stats (median/MAD) over a client's currently buffered raw audio, for the
    /// template-capture UI to persist alongside a newly saved template. False if the client is
    /// unknown or nothing has been captured yet. A full <see cref="SplashFeatures.ComputeBands"/>
    /// pass, like <see cref="CaptureCandidates"/> - call it from a background thread, never the
    /// dispatcher.
    /// </summary>
    public bool TryGetContextStats(uint processId, out float[] median, out float[] mad)
    {
        var buffer = ReadRawBuffer(processId, out var count);
        if (count == 0)
        {
            median = Array.Empty<float>();
            mad = Array.Empty<float>();
            return false;
        }

        var bands = SplashFeatures.ComputeBands(buffer.AsSpan(0, count));
        SplashFeatures.ComputeContextStats(bands, bands.GetLength(1), out median, out mad);
        return true;
    }

    /// <summary>
    /// Ranks candidates exactly like <see cref="CaptureCandidates"/>, but for the template-save
    /// flow rather than mere display: each candidate's samples and the context median/MAD they
    /// were ranked against (one <see cref="SplashDetector.RankWindows"/> call, one buffer
    /// snapshot) are cached here under a generated id, so a later <see cref="SaveTemplate"/> can
    /// persist audio and statistics that are guaranteed to match. Re-deriving stats at save time
    /// instead would risk pairing a candidate with whatever the ring buffer holds by then - the
    /// same failure class the Task 8 template pipeline had to be built around. Same cost as
    /// <see cref="CaptureCandidates"/> - a full <see cref="SplashFeatures.ComputeBands"/> pass -
    /// so call it from a background thread, never the dispatcher.
    /// </summary>
    public IReadOnlyList<(string Id, double Score, float[] Samples)> CaptureTemplateCandidates(uint processId, int maxResults)
    {
        var buffer = ReadRawBuffer(processId, out var count);
        if (count == 0)
            return Array.Empty<(string, double, float[])>();

        var windowSampleCount = SplashFeatures.WindowFrames * SplashFeatures.HopSize;
        var detector = SnapshotDetector();
        var ranked = detector.RankWindows(
            buffer.AsSpan(0, count), maxResults,
            minSeparationSeconds: SplashFeatures.WindowFrames * (double)SplashFeatures.HopSize / SplashFeatures.SampleRate,
            out var median, out var mad);

        var results = new List<(string, double, float[])>(ranked.Count);
        foreach (var (score, offsetSeconds) in ranked)
        {
            var startSample = (int)Math.Round(offsetSeconds * SplashFeatures.SampleRate);
            var samples = new float[windowSampleCount];
            var available = Math.Clamp(count - startSample, 0, windowSampleCount);
            if (available > 0)
                Array.Copy(buffer, startSample, samples, 0, available);

            var id = Guid.NewGuid().ToString("N");
            _pendingCandidates[id] = new PendingCandidate(samples, median, mad);
            results.Add((id, score, samples));
        }

        return results;
    }

    /// <summary>
    /// Persists a candidate handed back by <see cref="CaptureTemplateCandidates"/> as a named
    /// user template, using the samples and context stats cached at capture time. Null if the
    /// candidate id is unknown - the cache is not persisted, so this is expected after a restart,
    /// or if the same candidate is saved twice (the entry is consumed on first save).
    /// </summary>
    public string? SaveTemplate(string candidateId, string name)
    {
        if (!_pendingCandidates.TryRemove(candidateId, out var pending))
            return null;

        return _templateStore.Save(name, pending.Samples, pending.Median, pending.Mad);
    }

    /// <summary>Pass-through to <see cref="SplashTemplateStore.Delete"/> - kept encapsulated here
    /// rather than exposing the store itself, so this service stays the one owner of it.</summary>
    public bool DeleteTemplate(string templateId) => _templateStore.Delete(templateId);

    /// <summary>Pass-through to <see cref="SplashTemplateStore.Templates"/>, same reasoning as
    /// <see cref="DeleteTemplate"/>.</summary>
    public IReadOnlyList<SplashTemplate> Templates => _templateStore.Templates;

    /// <summary>
    /// Test-only hook. Runs exactly the decision code the real detection tick runs once it has
    /// a score in hand (character-name rule, threshold comparison, event raising) - not warm-up
    /// or scoring, which happen before a score exists at all. Lets the no-audio unit tests drive
    /// that decision path directly.
    /// </summary>
    internal void ForceDetectionPassForTests(uint processId, double score)
    {
        ClientSession? session;
        lock (_gate)
            _sessions.TryGetValue(processId, out session);

        if (session is not null)
            EvaluateDetection(session, score);
    }

    /// <summary>
    /// Test-only hook. Feeds synthetic samples directly into a client's raw ring and rolling
    /// band buffer, the same two places <see cref="OnSamples"/> writes real captured audio -
    /// letting a test warm a session up (and time a real detection tick against it) without a
    /// live WASAPI capture, which <see cref="SetClients"/> alone cannot provide in a test
    /// environment.
    /// </summary>
    internal void FeedSamplesForTests(uint processId, float[] samples)
    {
        ClientSession? session;
        lock (_gate)
            _sessions.TryGetValue(processId, out session);

        session?.Ring.Write(samples);
        session?.BandBuffer.Append(samples);
    }

    /// <summary>
    /// Test-only hook. Runs one detection pass synchronously (bypassing the timer and its
    /// re-entrancy guard) and returns how long it took - the number this design most needed
    /// measured rather than estimated from the algorithm's shape.
    /// </summary>
    internal TimeSpan RunDetectionPassForTests()
    {
        var stopwatch = Stopwatch.StartNew();
        RunDetectionPass();
        stopwatch.Stop();
        return stopwatch.Elapsed;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        _detectionTimer.Dispose();

        List<ClientSession> sessions;
        lock (_gate)
        {
            sessions = _sessions.Values.ToList();
            _sessions.Clear();
        }

        foreach (var session in sessions)
            session.Dispose();
    }

    // --- Detection tick ------------------------------------------------------------------------

    private void OnDetectionTick(object? state)
    {
        // A Timer with a fixed period can fire again before a slow tick finishes; without this
        // guard, overlapping ticks could race on the same session's capture teardown.
        if (Interlocked.CompareExchange(ref _detectionTickInProgress, 1, 0) != 0) return;

        try
        {
            RunDetectionPass();
        }
        catch (Exception ex)
        {
            // A System.Threading.Timer callback that throws takes the whole process down with
            // it. Nothing this loop does is worth that - log and wait for the next tick.
            TriffViewDiagnostics.Log("audio-detection", $"detection tick failed: {ex}");
        }
        finally
        {
            Interlocked.Exchange(ref _detectionTickInProgress, 0);
        }
    }

    private void RunDetectionPass()
    {
        bool enabled;
        lock (_gate) enabled = _enabled;
        if (!enabled) return;

        var now = DateTime.UtcNow;
        var toScore = new List<ClientSession>();

        lock (_gate)
        {
            foreach (var session in _sessions.Values)
            {
                if (session.Capture is null) continue; // already down; SetClients will retry it

                if (ComputeStatus(session, enabled: true, now) == "unavailable")
                {
                    // Tear down now rather than wait for SetClients to notice: the next
                    // SetClients call sees Capture == null and starts a fresh session, which is
                    // also what resets warm-up for a stream that died and came back.
                    session.Capture.Dispose();
                    session.Capture = null;
                    continue;
                }

                if (session.BandBuffer.TotalFrames < SplashFeatures.ContextFrames) continue; // still warming up

                toScore.Add(session);
            }
        }

        if (toScore.Count == 0) return;

        // SplashTemplateStore.Templates is a reference swap (Reload/Delete build a new list and
        // assign it), not a mutated-in-place one, so reading it once here and holding onto the
        // result for the rest of this tick is safe even if a template-capture save (Task 10)
        // lands concurrently: this tick either sees the old, complete list or the new one, never
        // a partial one.
        var detector = SnapshotDetector();

        foreach (var session in toScore)
        {
            var bandBuffer = session.BandBuffer;

            if (session.ContextMedian is null || now - session.ContextStatsComputedUtc >= ContextStatsRefreshInterval)
            {
                var contextBands = bandBuffer.ReadRecent(SplashFeatures.ContextFrames);
                SplashFeatures.ComputeContextStats(contextBands, contextBands.GetLength(1), out var median, out var mad);
                session.ContextMedian = median;
                session.ContextMad = mad;
                session.ContextStatsComputedUtc = now;
            }

            var windowBands = bandBuffer.ReadRecent(SplashFeatures.WindowFrames);
            if (windowBands.GetLength(1) < SplashFeatures.WindowFrames) continue; // shouldn't happen once warmed up; be safe anyway

            var patch = SplashFeatures.BuildPatch(windowBands, 0, session.ContextMedian, session.ContextMad!);
            var score = detector.ScorePatch(patch);
            EvaluateDetection(session, score);
        }
    }

    private SplashDetector SnapshotDetector() => new(_templateStore.Templates);

    /// <summary>
    /// The decision made once a score exists, whether it came from the real tick above or
    /// <see cref="ForceDetectionPassForTests"/>. A client with no character name (at the
    /// character-select screen) is captured but can never alert: a cooldown keyed by
    /// "{name}|{type}" would collide across every such client, and an alert with no name could
    /// not be matched to a preview anyway.
    /// </summary>
    private void EvaluateDetection(ClientSession session, double score)
    {
        if (string.IsNullOrEmpty(session.CharacterName)) return;

        double threshold;
        lock (_gate) threshold = _threshold;
        if (score < threshold) return;

        _pendingDetections.Enqueue((session.CharacterName, score));
        ScheduleDetectionDispatch();
    }

    // --- Detection dispatch: same Interlocked.CompareExchange idiom as
    // TriffAlertsService.ScheduleNotificationDispatch/DispatchPendingNotifications and
    // TriffViewSubsystem's equivalent, so SplashDetected is always raised off a thread-pool
    // work item rather than inline under whichever lock produced the detection. ------------------

    private void ScheduleDetectionDispatch()
    {
        if (Interlocked.CompareExchange(ref _detectionDispatchScheduled, 1, 0) != 0) return;
        ThreadPool.UnsafeQueueUserWorkItem(static service => service.DispatchPendingDetections(), this, preferLocal: false);
    }

    private void DispatchPendingDetections()
    {
        try
        {
            while (_pendingDetections.TryDequeue(out var detection))
                SplashDetected?.Invoke(this, detection);
        }
        finally
        {
            Interlocked.Exchange(ref _detectionDispatchScheduled, 0);
            if (!_pendingDetections.IsEmpty) ScheduleDetectionDispatch();
        }
    }

    // --- Capture lifecycle -----------------------------------------------------------------------

    private void StartCapture(ClientSession session)
    {
        // Fresh buffers, not reused ones: a client whose stream died and is only now being
        // restarted must re-earn its 30 s of warm-up rather than inherit whatever stale audio
        // (or lack of it) the old buffers held. Captured locally rather than resolved through
        // `session.Ring`/`session.BandBuffer` inside the capture callback below, so the
        // callback always writes to the buffers this particular capture attempt created.
        var ring = new AudioRingBuffer(RingCapacitySamples);
        var bandBuffer = new RollingBandBuffer(SplashFeatures.ContextFrames);

        lock (_gate)
        {
            session.Ring = ring;
            session.BandBuffer = bandBuffer;
            session.ContextMedian = null;
            session.ContextMad = null;
            session.ContextStatsComputedUtc = DateTime.MinValue;
        }

        var capture = new WasapiProcessCapture(session.ProcessId, samples => OnSamples(session, ring, bandBuffer, samples));
        var ok = capture.Start(out var error);

        var keep = false;
        lock (_gate)
        {
            session.StartInProgress = false;

            var stillCurrent = _sessions.TryGetValue(session.ProcessId, out var current) && ReferenceEquals(current, session);
            if (stillCurrent && ok)
            {
                session.Capture = capture;
                session.StartedUtc = DateTime.UtcNow;
                session.LastNonZeroUtc = session.StartedUtc;
                session.FailedAttempts = 0;
                session.NextRetryUtc = DateTime.MinValue;
                keep = true;
            }
            else if (stillCurrent) // !ok
            {
                session.Capture = null;
                session.FailedAttempts++;
                session.NextRetryUtc = DateTime.UtcNow + RetryBackoff(session.FailedAttempts - 1);

                // Log the transition into failing, not every attempt: a permanently-broken
                // client would otherwise churn the 2 MB diagnostics log at the retry cadence,
                // destroying its value for the geometry investigations it exists for.
                if (session.FailedAttempts == 1)
                    TriffViewDiagnostics.Log("audio-capture", $"pid {session.ProcessId}: {error}");
            }
            // else: superseded (removed by a later SetClients) while Start() was blocking;
            // nothing on the session to update, just discard the capture below.
        }

        // Dispose outside the lock (unconditionally, whether Start failed or the attempt was
        // superseded): WasapiProcessCapture.Start can fail partway through - after activating
        // and initializing the audio client but before Start() itself succeeds - leaving a live
        // audio client and two Win32 event handles behind if nothing releases them, and Dispose
        // can block up to 2 s joining a capture thread that may not even exist yet.
        if (!keep)
            capture.Dispose();
    }

    private static TimeSpan RetryBackoff(int failedAttempts)
    {
        var index = Math.Clamp(failedAttempts, 0, RetryBackoffSteps.Length - 1);
        return RetryBackoffSteps[index];
    }

    private static void OnSamples(ClientSession session, AudioRingBuffer ring, RollingBandBuffer bandBuffer, ReadOnlySpan<float> samples)
    {
        ring.Write(samples);
        bandBuffer.Append(samples);

        // AUDCLNT_BUFFERFLAGS_SILENT is never set for a muted process (measured), so
        // NonZeroSamplesInLastWrite - not the capture flags - is the only reliable silence
        // signal available here.
        if (ring.NonZeroSamplesInLastWrite > 0)
            session.LastNonZeroUtc = DateTime.UtcNow;
    }

    private float[] ReadRawBuffer(uint processId, out int count)
    {
        ClientSession? session;
        lock (_gate)
            _sessions.TryGetValue(processId, out session);

        if (session is null)
        {
            count = 0;
            return Array.Empty<float>();
        }

        var buffer = new float[RingCapacitySamples];
        count = session.Ring.Read(buffer);
        return buffer;
    }

    private static string ComputeStatus(ClientSession session, bool enabled, DateTime now)
    {
        if (!enabled) return "off";

        var capture = session.Capture;
        if (capture is null) return "unavailable";

        // LastPacketUtc defaults to DateTime.MinValue until the first packet arrives; treat the
        // capture's own start time as the baseline until then so a client isn't marked
        // unavailable in the moment before its first packet lands.
        var lastPacket = capture.LastPacketUtc == default ? session.StartedUtc : capture.LastPacketUtc;
        if (now - lastPacket > UnavailableIdleWindow) return "unavailable";

        if (now - session.LastNonZeroUtc > SilentIdleWindow) return "silent";

        return "monitoring";
    }

    private static string DefaultUserTemplateDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TriffHud", "SplashTemplates");

    private sealed class ClientSession
    {
        public readonly uint ProcessId;
        public string CharacterName;
        public AudioRingBuffer Ring = new(RingCapacitySamples);
        public RollingBandBuffer BandBuffer = new(SplashFeatures.ContextFrames);
        public WasapiProcessCapture? Capture;
        public bool StartInProgress;
        public DateTime StartedUtc = DateTime.UtcNow;
        public DateTime LastNonZeroUtc = DateTime.UtcNow;
        public int FailedAttempts;
        public DateTime NextRetryUtc = DateTime.MinValue;

        // Cached rolling context stats, refreshed at most once a second - see
        // ContextStatsRefreshInterval.
        public float[]? ContextMedian;
        public float[]? ContextMad;
        public DateTime ContextStatsComputedUtc = DateTime.MinValue;

        public ClientSession(uint processId, string characterName)
        {
            ProcessId = processId;
            CharacterName = characterName;
        }

        public void Dispose() => Capture?.Dispose();
    }
}
