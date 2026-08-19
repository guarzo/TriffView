using System.Threading;
using TriffView.Audio;
using Xunit;

public class TriffAudioServiceTests
{
    [Fact]
    public void ReportsOffForEveryClientWhenDisabled()
    {
        using var svc = new TriffAudioService();
        svc.UpdateSettings(enabled: false, threshold: 0.35);
        svc.SetClients(new[] { (1234u, "Pilot") });
        Assert.All(svc.Statuses, s => Assert.Equal("off", s.Status));
    }

    [Fact]
    public void DroppingAClientRemovesItsStatus()
    {
        using var svc = new TriffAudioService();
        svc.UpdateSettings(enabled: true, threshold: 0.35);
        svc.SetClients(new[] { (1234u, "Pilot"), (5678u, "Other") });
        Assert.Equal(2, svc.Statuses.Count);
        svc.SetClients(new[] { (1234u, "Pilot") });
        Assert.Single(svc.Statuses);
    }

    [Fact]
    public void NeverAlertsForAClientWithNoCharacterName()
    {
        using var svc = new TriffAudioService();
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
        using var svc = new TriffAudioService();
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
        using var svc = new TriffAudioService();
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
        using var svc = new TriffAudioService();
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
        using var svc = new TriffAudioService();
        svc.UpdateSettings(enabled: true, threshold: 0.35);
        Assert.Empty(svc.Statuses);
    }

    [Fact]
    public void DisposeIsIdempotentAndDoesNotThrow()
    {
        var svc = new TriffAudioService();
        svc.UpdateSettings(enabled: true, threshold: 0.35);
        svc.SetClients(new[] { (1234u, "Pilot") });
        svc.Dispose();
        svc.Dispose();
    }
}
