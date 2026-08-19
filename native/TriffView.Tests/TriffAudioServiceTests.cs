using System;
using System.IO;
using System.Threading;
using TriffView.Audio;
using Xunit;

public class TriffAudioServiceTests : IDisposable
{
    // Every test gets its own throwaway directory rather than the real
    // %APPDATA%\TriffHud\SplashTemplates default, so a test run never reads (or races with)
    // whatever user templates happen to be saved on the machine running the tests.
    private readonly string _templateDirectory = Path.Combine(Path.GetTempPath(), "TriffAudioServiceTests-" + Guid.NewGuid().ToString("N"));
    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public TriffAudioServiceTests(Xunit.Abstractions.ITestOutputHelper output)
    {
        _output = output;
    }

    public void Dispose()
    {
        if (Directory.Exists(_templateDirectory))
            Directory.Delete(_templateDirectory, recursive: true);
    }

    private TriffAudioService CreateService() => new(_templateDirectory, FakeAudioCapture.Factory);

    /// <summary>
    /// A service whose fake captures signal <paramref name="started"/> as each one starts, so a
    /// test can wait for the capture attempts to land before feeding samples. Necessary because
    /// <see cref="TriffAudioService.SetClients"/> queues <c>StartCapture</c> onto the thread pool
    /// and <c>StartCapture</c> *replaces* the session's ring and band buffers: samples fed before
    /// that swap are written into buffers that are then thrown away, and the client scores as
    /// still warming up. Waiting on a real signal rather than hoping the queued work wins the
    /// race is the difference between a test that is usually right and one that is always right.
    /// </summary>
    private TriffAudioService CreateService(CountdownEvent started) =>
        new(_templateDirectory, (_, _) => new FakeAudioCapture(started));

    /// <summary>Waits for every queued capture start, failing the test rather than proceeding
    /// into the race if the thread pool never gets to them.</summary>
    private static void WaitForCaptureStarts(CountdownEvent started) =>
        Assert.True(started.Wait(TimeSpan.FromSeconds(10)), "capture starts did not complete; the warm-up below would race the buffer swap");

    /// <summary>
    /// Stands in for <see cref="WasapiProcessCapture"/> in every test: never opens real WASAPI
    /// process-loopback capture, so <c>SetClients(enabled: true, ...)</c> here is safe to call
    /// with hardcoded PIDs that may coincide with whatever real process happens to be running on
    /// the machine executing the tests. A previous version of this suite used real capture
    /// against those PIDs, which intermittently caused a second writer into the single-writer
    /// RollingBandBuffer, stray real audio perturbing the measured tick, and a live capture thread
    /// still running at test-abort time (see the Task 9 review).
    /// </summary>
    private sealed class FakeAudioCapture : IAudioCapture
    {
        public static readonly Func<uint, WasapiProcessCapture.SampleCallback, IAudioCapture> Factory =
            (_, _) => new FakeAudioCapture();

        private readonly CountdownEvent? _started;

        public FakeAudioCapture(CountdownEvent? started = null)
        {
            _started = started;
        }

        public DateTime LastPacketUtc { get; private set; } = DateTime.UtcNow;

        public bool Start(out string? error)
        {
            error = null;
            // TriffAudioService.StartCapture installs the session's fresh buffers before calling
            // this, so a test that waits on the countdown here is guaranteed to feed samples into
            // the buffers the session will actually score.
            //
            // Signal throws once the countdown reaches zero, and StartCapture calls Start with no
            // try/catch of its own - the real WasapiProcessCapture.Start never throws, and a fake
            // must honour the contract it stands in for or it invents a thread-pool crash the real
            // code cannot have. A retried capture (backoff path) is exactly how an extra Start
            // arrives after the countdown is done, so this is reachable, not theoretical.
            try
            {
                _started?.Signal();
            }
            catch (InvalidOperationException)
            {
                // More starts than the test asked to wait for; the ordering it wanted already held.
            }

            return true;
        }

        public void Dispose()
        {
        }
    }

    [Fact]
    public void ReportsOffForEveryClientWhenDisabled()
    {
        using var svc = CreateService();
        svc.UpdateSettings(enabled: false, threshold: 0.35);
        svc.SetClients(new[] { (1234u, "Pilot") });
        Assert.All(svc.Statuses, s => Assert.Equal("off", s.Status));
    }

    [Fact]
    public void DroppingAClientRemovesItsStatus()
    {
        using var svc = CreateService();
        svc.UpdateSettings(enabled: true, threshold: 0.35);
        svc.SetClients(new[] { (1234u, "Pilot"), (5678u, "Other") });
        Assert.Equal(2, svc.Statuses.Count);
        svc.SetClients(new[] { (1234u, "Pilot") });
        Assert.Single(svc.Statuses);
    }

    [Fact]
    public void NeverAlertsForAClientWithNoCharacterName()
    {
        using var svc = CreateService();
        svc.UpdateSettings(enabled: true, threshold: 0.35);
        var raised = 0;
        svc.SplashDetected += (_, _) => raised++;
        // Deliberately NOT using TestTemplates: that helper lives in tests/TriffView.Tests,
        // which native/TriffView.Tests cannot see (it references only TriffView.csproj).
        // This test is about the no-character-name rule, not about detection quality, so
        // any audio that would score above threshold will do.
        svc.SetClients(new[] { (1234u, "") });          // character-select screen
        svc.ForceDetectionPassForTests(1234u, score: 0.99);
        Assert.Equal(0, raised);
    }

    [Fact]
    public void AlertsForAClientWithACharacterNameAboveThreshold()
    {
        using var svc = CreateService();
        svc.UpdateSettings(enabled: true, threshold: 0.35);
        (string CharacterName, double Score)? seen = null;
        svc.SplashDetected += (_, detection) => seen = detection;

        svc.SetClients(new[] { (1234u, "Pilot") });
        svc.ForceDetectionPassForTests(1234u, score: 0.99);

        // The event is dispatched off a thread-pool work item, never inline, so give it a
        // moment to arrive rather than asserting immediately.
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (seen is null && DateTime.UtcNow < deadline)
            Thread.Sleep(10);

        Assert.NotNull(seen);
        Assert.Equal("Pilot", seen!.Value.CharacterName);
        Assert.Equal(0.99, seen.Value.Score, 3);
    }

    [Fact]
    public void ScoreBelowThresholdDoesNotAlert()
    {
        using var svc = CreateService();
        svc.UpdateSettings(enabled: true, threshold: 0.35);
        var raised = 0;
        svc.SplashDetected += (_, _) => raised++;

        svc.SetClients(new[] { (1234u, "Pilot") });
        svc.ForceDetectionPassForTests(1234u, score: 0.10);

        Thread.Sleep(100);
        Assert.Equal(0, raised);
    }

    [Fact]
    public void ForceDetectionPassIsANoOpForAnUnknownProcessId()
    {
        using var svc = CreateService();
        svc.UpdateSettings(enabled: true, threshold: 0.35);
        var raised = 0;
        svc.SplashDetected += (_, _) => raised++;

        // No SetClients call at all: process 9999 has no session.
        svc.ForceDetectionPassForTests(9999u, score: 0.99);

        Thread.Sleep(100);
        Assert.Equal(0, raised);
    }

    [Fact]
    public void StatusesAreEmptyBeforeAnyClientIsSet()
    {
        using var svc = CreateService();
        svc.UpdateSettings(enabled: true, threshold: 0.35);
        Assert.Empty(svc.Statuses);
    }

    [Fact]
    public void DisposeIsIdempotentAndDoesNotThrow()
    {
        var svc = CreateService();
        svc.UpdateSettings(enabled: true, threshold: 0.35);
        svc.SetClients(new[] { (1234u, "Pilot") });
        svc.Dispose();
        svc.Dispose();
    }

    // --- Measured tick duration -------------------------------------------------------------
    //
    // These are not correctness tests (nothing here asserts on the score); they exist to
    // produce a real measured number for "how long does one detection tick take", per the
    // Task 8 review's explicit request for a measured rather than estimated figure. Each client
    // is warmed up with FeedSamplesForTests to the full 30 s context window - the same
    // RollingBandBuffer.Append path OnSamples uses for real captured audio - then
    // RunDetectionPassForTests times one full pass over every warmed-up client.

    private const int WarmUpSamples = SplashFeatures.ContextFrames * SplashFeatures.HopSize + SplashFeatures.FftSize;

    private static void WarmUp(TriffAudioService svc, uint processId, int seed)
    {
        var rng = new Random(seed);
        var samples = new float[WarmUpSamples];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = (float)(rng.NextDouble() - 0.5) * 0.2f;
        svc.FeedSamplesForTests(processId, samples);
    }

    [Fact]
    public void MeasuresOneDetectionTickForOneWarmedUpClient()
    {
        using var started = new CountdownEvent(1);
        using var svc = CreateService(started);
        svc.UpdateSettings(enabled: true, threshold: 0.35);
        svc.SetClients(new[] { (1234u, "Pilot") });
        WaitForCaptureStarts(started);
        WarmUp(svc, 1234u, seed: 1);

        // First pass also computes the once-per-second context stats (median/MAD); run it once
        // unmeasured so the timed pass reflects the common case where they are still fresh.
        svc.RunDetectionPassForTests();
        var (elapsed, scoredCount) = svc.RunDetectionPassForTests();

        _output.WriteLine($"MEASURED one-client tick: {elapsed.TotalMilliseconds:F3} ms");
        // This is a measurement, not a correctness assertion (see the section comment), but it
        // must still assert that the pass actually did the work being measured - ScoredCount
        // catches the failure mode where every client is skipped (still warming up, or torn down
        // as unavailable) and the "measurement" is really just timing an empty loop. Trip on
        // catastrophic regressions an order of magnitude above the 250 ms tick budget, never on
        // ordinary machine load.
        Assert.Equal(1, scoredCount);
        Assert.True(elapsed < TimeSpan.FromSeconds(2), $"one-client tick took {elapsed.TotalMilliseconds:F2} ms, an order of magnitude above budget");
    }

    [Fact]
    public void MeasuresOneDetectionTickForSixWarmedUpClients()
    {
        using var started = new CountdownEvent(6);
        using var svc = CreateService(started);
        svc.UpdateSettings(enabled: true, threshold: 0.35);

        var clients = new (uint ProcessId, string CharacterName)[6];
        for (var i = 0; i < clients.Length; i++)
            clients[i] = ((uint)(1000 + i), $"Pilot{i}");
        svc.SetClients(clients);
        WaitForCaptureStarts(started);

        for (var i = 0; i < clients.Length; i++)
            WarmUp(svc, clients[i].ProcessId, seed: i + 1);

        svc.RunDetectionPassForTests();
        var (elapsed, scoredCount) = svc.RunDetectionPassForTests();

        _output.WriteLine($"MEASURED six-client tick: {elapsed.TotalMilliseconds:F3} ms");
        // Measurement, not correctness (see above), but must confirm the pass actually scored all
        // six clients rather than timing an empty loop; only trip on a catastrophic,
        // order-of-magnitude regression.
        Assert.Equal(6, scoredCount);
        Assert.True(elapsed < TimeSpan.FromSeconds(2), $"six-client tick took {elapsed.TotalMilliseconds:F2} ms, an order of magnitude above budget");
    }
}
