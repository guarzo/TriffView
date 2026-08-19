using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;

namespace TriffView.Audio;

/// <summary>Point-in-time health of one captured EVE client, for the settings UI.</summary>
/// <param name="Status">One of "monitoring", "silent", "unavailable", "off".</param>
internal sealed record AudioClientStatus(uint ProcessId, string CharacterName, string Status);

/// <summary>
/// Owns one <see cref="WasapiProcessCapture"/> and one <see cref="AudioRingBuffer"/> per EVE
/// process, and a single shared 4 Hz timer that scores every client's trailing audio against
/// <see cref="SplashTemplateStore"/>'s templates via <see cref="SplashDetector"/>.
///
/// Windows-only (it owns <see cref="WasapiProcessCapture"/> instances), so unlike the rest of
/// TriffAudio this does not link into the cross-platform test project; its tests live in
/// native/TriffView.Tests instead, exercising everything that does not need real audio via
/// <see cref="ForceDetectionPassForTests"/>.
/// </summary>
internal sealed class TriffAudioService : IDisposable
{
    /// <summary>
    /// Exactly <see cref="SplashFeatures.ContextFrames"/> worth of samples (30.0 s). Doubles as
    /// both the ring buffer's capacity and the warm-up threshold: a buffer this full gives
    /// <see cref="SplashDetector.Score(ReadOnlySpan{float})"/> a properly populated rolling
    /// context to z-score against. Below it, splash and non-splash scores do not separate
    /// (measured: 3 s of context put both classes in the 0.22-0.24 band) so no alert may fire.
    /// </summary>
    private const int RingCapacitySamples = SplashFeatures.ContextFrames * SplashFeatures.HopSize;

    private static readonly TimeSpan UnavailableIdleWindow = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SilentIdleWindow = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan DetectionInterval = TimeSpan.FromMilliseconds(250);

    private readonly object _gate = new();
    private readonly Dictionary<uint, ClientSession> _sessions = new();
    private readonly SplashTemplateStore _templateStore;
    private readonly SplashDetector _detector;
    private readonly System.Threading.Timer _detectionTimer;
    private readonly ConcurrentQueue<(string CharacterName, double Score)> _pendingDetections = new();

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
        _detector = new SplashDetector(_templateStore.Templates);

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
    /// </summary>
    public void SetClients(IReadOnlyList<(uint ProcessId, string CharacterName)> clients)
    {
        var toDispose = new List<ClientSession>();
        var toStart = new List<ClientSession>();

        lock (_gate)
        {
            var wanted = new Dictionary<uint, string>();
            foreach (var client in clients)
                wanted[client.ProcessId] = client.CharacterName ?? "";

            foreach (var pid in _sessions.Keys.ToList())
            {
                if (wanted.ContainsKey(pid)) continue;
                toDispose.Add(_sessions[pid]);
                _sessions.Remove(pid);
            }

            foreach (var (pid, name) in wanted)
            {
                if (_sessions.TryGetValue(pid, out var existing))
                {
                    existing.CharacterName = name;
                    if (existing.Capture is null && !existing.StartInProgress)
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

        // Start() blocks on WASAPI activation (up to 5 s on failure), so it must run outside
        // the gate - holding it here would stall Statuses/SetClients for every other client
        // for as long as the slowest activation takes.
        foreach (var session in toStart)
            StartCapture(session);
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
    /// Ranks every 250 ms window across a client's currently buffered audio with no threshold
    /// applied, for the template-capture UI (Task 10) to offer as candidates - including ones
    /// the live detector currently scores below threshold and would never alert on.
    /// </summary>
    public IReadOnlyList<(double Score, double OffsetSeconds, float[] Samples)> CaptureCandidates(uint processId, int maxResults)
    {
        var buffer = ReadBuffer(processId, out var count);
        if (count == 0)
            return Array.Empty<(double, double, float[])>();

        var windowSampleCount = SplashFeatures.WindowFrames * SplashFeatures.HopSize;
        var ranked = _detector.RankWindows(buffer.AsSpan(0, count), maxResults, minSeparationSeconds: SplashFeatures.WindowFrames * (double)SplashFeatures.HopSize / SplashFeatures.SampleRate);

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
    /// Context stats (median/MAD) over a client's currently buffered audio, for the
    /// template-capture UI to persist alongside a newly saved template. False if the client is
    /// unknown or nothing has been captured yet.
    /// </summary>
    public bool TryGetContextStats(uint processId, out float[] median, out float[] mad)
    {
        var buffer = ReadBuffer(processId, out var count);
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
        // A Timer with a fixed period can fire again before a slow tick (many clients, each a
        // full 30 s FFT) finishes; without this guard, overlapping ticks could race on the same
        // session's capture teardown.
        if (Interlocked.CompareExchange(ref _detectionTickInProgress, 1, 0) != 0) return;

        try
        {
            bool enabled;
            lock (_gate) enabled = _enabled;
            if (!enabled) return;

            var toScore = new List<(ClientSession Session, float[] Buffer)>();

            lock (_gate)
            {
                var now = DateTime.UtcNow;
                foreach (var session in _sessions.Values)
                {
                    if (session.Capture is null) continue; // already down; SetClients will retry it

                    if (ComputeStatus(session, enabled: true, now) == "unavailable")
                    {
                        // Tear down now rather than wait for SetClients to notice: the next
                        // SetClients call sees Capture == null and starts a fresh session,
                        // which is also what resets warm-up for a stream that died and came back.
                        session.Capture.Dispose();
                        session.Capture = null;
                        continue;
                    }

                    if (session.Ring.TotalWritten < RingCapacitySamples) continue; // still warming up

                    var buffer = new float[RingCapacitySamples];
                    session.Ring.Read(buffer);
                    toScore.Add((session, buffer));
                }
            }

            // Scoring runs a full ComputeBands pass per client (one call, not one per candidate
            // window - RankWindows/ScoreCore reuse the same computed bands internally), so it
            // must happen outside the gate to avoid blocking Statuses/SetClients for its duration.
            foreach (var (session, buffer) in toScore)
            {
                var score = _detector.Score(buffer);
                EvaluateDetection(session, score);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _detectionTickInProgress, 0);
        }
    }

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
        // A fresh ring buffer, not a reused one: a client whose stream died and is only now
        // being restarted must re-earn its 30 s of warm-up rather than inherit whatever stale
        // audio (or lack of it) the old ring held.
        lock (_gate)
            session.Ring = new AudioRingBuffer(RingCapacitySamples);

        var capture = new WasapiProcessCapture(session.ProcessId, samples => OnSamples(session, samples));
        var ok = capture.Start(out var error);

        lock (_gate)
        {
            session.StartInProgress = false;

            if (!_sessions.TryGetValue(session.ProcessId, out var current) || !ReferenceEquals(current, session))
            {
                // Superseded (removed by a later SetClients) while Start() was blocking; discard.
                if (ok) capture.Dispose();
                return;
            }

            if (ok)
            {
                session.Capture = capture;
                session.StartedUtc = DateTime.UtcNow;
                session.LastNonZeroUtc = session.StartedUtc;
            }
            else
            {
                session.Capture = null;
                TriffViewDiagnostics.Log("audio-capture", $"pid {session.ProcessId}: {error}");
            }
        }
    }

    private void OnSamples(ClientSession session, ReadOnlySpan<float> samples)
    {
        session.Ring.Write(samples);

        // AUDCLNT_BUFFERFLAGS_SILENT is never set for a muted process (measured), so
        // NonZeroSamplesInLastWrite - not the capture flags - is the only reliable silence
        // signal available here.
        if (session.Ring.NonZeroSamplesInLastWrite > 0)
            session.LastNonZeroUtc = DateTime.UtcNow;
    }

    private float[] ReadBuffer(uint processId, out int count)
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
        public WasapiProcessCapture? Capture;
        public bool StartInProgress;
        public DateTime StartedUtc = DateTime.UtcNow;
        public DateTime LastNonZeroUtc = DateTime.UtcNow;

        public ClientSession(uint processId, string characterName)
        {
            ProcessId = processId;
            CharacterName = characterName;
        }

        public void Dispose() => Capture?.Dispose();
    }
}
