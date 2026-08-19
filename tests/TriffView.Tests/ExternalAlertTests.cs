using TriffView.Alerts;
using Xunit;

public class ExternalAlertTests
{
    private static TriffAlertsService NewService(out TriffAlertsSettings settings)
    {
        settings = new TriffAlertsSettings { Enabled = true };
        settings.Normalize();
        var svc = new TriffAlertsService(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        svc.UpdateSettings(settings);
        return svc;
    }

    // AlertTriggered is raised from a thread-pool drain (TriffAlertsService.cs:873-885),
    // never synchronously. Every test here must wait rather than assert immediately.
    private static bool Wait(ManualResetEventSlim gate) => gate.Wait(TimeSpan.FromSeconds(5));

    [Fact]
    public void RaisesAndRecordsHistory()
    {
        var svc = NewService(out _);
        TriffAlertEvent? seen = null;
        using var gate = new ManualResetEventSlim();
        svc.AlertTriggered += (_, e) => { seen = e; gate.Set(); };

        svc.RaiseExternalAlert("wormhole_splash", "Pilot", "audio", "Wormhole activated");

        Assert.True(Wait(gate), "AlertTriggered did not fire within 5s");
        Assert.NotNull(seen);
        Assert.Equal("wormhole_splash", seen!.Type);
        Assert.Equal("Pilot", seen.CharacterName);
        Assert.Contains(svc.History, a => a.Id == seen.Id);
    }

    [Fact]
    public void AppliesTheSameCooldownAsLogAlerts()
    {
        var svc = NewService(out _);
        var count = 0;
        using var gate = new ManualResetEventSlim();
        svc.AlertTriggered += (_, _) => { Interlocked.Increment(ref count); gate.Set(); };

        svc.RaiseExternalAlert("wormhole_splash", "Pilot", "audio", "one");
        svc.RaiseExternalAlert("wormhole_splash", "Pilot", "audio", "two");

        Assert.True(Wait(gate), "AlertTriggered did not fire within 5s");
        Thread.Sleep(500);                   // allow a second (incorrect) raise to arrive
        Assert.Equal(1, Volatile.Read(ref count));   // second suppressed by the 15 s cooldown
    }

    [Fact]
    public void DoesNotRaiseWhenTheEventTypeIsDisabled()
    {
        var settings = new TriffAlertsSettings { Enabled = true };
        settings.Normalize();
        settings.Events["wormhole_splash"].Enabled = false;
        var svc = new TriffAlertsService(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        svc.UpdateSettings(settings);

        var count = 0;
        svc.AlertTriggered += (_, _) => Interlocked.Increment(ref count);
        svc.RaiseExternalAlert("wormhole_splash", "Pilot", "audio", "x");
        Thread.Sleep(500);                   // a disabled event must produce nothing at all
        Assert.Equal(0, Volatile.Read(ref count));
    }

    [Fact]
    public void SplashThresholdIsClamped()
    {
        var s = new TriffAlertsSettings { SplashThreshold = 5.0 };
        s.Normalize();
        Assert.InRange(s.SplashThreshold, 0.10, 0.90);
    }
}
