using System;
using TriffView.Preview;
using Xunit;

namespace TriffView.Tests;

/// <summary>
/// The preview alert state machine: arming, expiry, severity-replacement, and
/// acknowledgement. Extracted from the private nested `PreviewState` class inside
/// `TriffViewOverlayForm` (native/TriffView/TriffViewSubsystem.cs) so it is reachable
/// from a test project. This project has a ProjectReference to TriffView.csproj and
/// InternalsVisibleTo, so the extracted type needs no Windows-free constraint and no
/// Compile Include line.
///
/// This first pass covers only today's (pre-persistent-alerts) behaviour: every alert
/// constructed here passes persistent=false, so it is a pin for the refactor in
/// PreviewAlertState.cs having zero behaviour change relative to the code it replaces.
/// </summary>
public class PreviewAlertStateTests
{
    private static readonly DateTime Now = new(2026, 8, 17, 12, 0, 0, DateTimeKind.Utc);

    private static TriffViewPreviewAlert Alert(int severityRank, string color = "#ff3b3b", int durationMs = 1000, bool persistent = false)
        => new(severityRank, color, 2, durationMs, 3, persistent);

    [Fact]
    public void ArmedAlertIsActiveImmediately()
    {
        var state = new PreviewAlertState();

        state.Arm(Alert(1), Now, targetIsSelected: false);

        var active = state.Active(Now);
        Assert.NotNull(active);
        Assert.Equal(1, active!.SeverityRank);
    }

    [Fact]
    public void ActiveReturnsNullAfterExpiresUtc()
    {
        var state = new PreviewAlertState();
        state.Arm(Alert(1, durationMs: 1000), Now, targetIsSelected: false);

        var afterExpiry = Now.AddMilliseconds(1001);

        Assert.Null(state.Active(afterExpiry));
    }

    [Fact]
    public void ClearExpiredReturnsTrueOnceThenFalse()
    {
        var state = new PreviewAlertState();
        state.Arm(Alert(1, durationMs: 1000), Now, targetIsSelected: false);
        var afterExpiry = Now.AddMilliseconds(1001);

        Assert.True(state.ClearExpired(afterExpiry));
        Assert.False(state.ClearExpired(afterExpiry));
    }

    [Fact]
    public void HigherSeverityAlertReplacesLowerOne()
    {
        var state = new PreviewAlertState();
        state.Arm(Alert(1, color: "#ff3b3b"), Now, targetIsSelected: false);

        state.Arm(Alert(5, color: "#ffee00"), Now, targetIsSelected: false);

        var active = state.Active(Now);
        Assert.NotNull(active);
        Assert.Equal(5, active!.SeverityRank);
        Assert.Equal("#ffee00", active.Color);
    }

    [Fact]
    public void LowerSeverityAlertExtendsExpiryWithoutChangingColour()
    {
        var state = new PreviewAlertState();
        state.Arm(Alert(5, color: "#ffee00", durationMs: 1000), Now, targetIsSelected: false);
        var midFlash = Now.AddMilliseconds(500);

        state.Arm(Alert(1, color: "#ff3b3b", durationMs: 1000), midFlash, targetIsSelected: false);

        var active = state.Active(midFlash);
        Assert.NotNull(active);
        Assert.Equal(5, active!.SeverityRank);
        Assert.Equal("#ffee00", active.Color);
        Assert.Equal(midFlash.AddMilliseconds(1000), active.ExpiresUtc);
    }

    [Fact]
    public void AcknowledgeIsNoOpOnNonPersistentAlert()
    {
        var state = new PreviewAlertState();
        state.Arm(Alert(1, durationMs: 1000), Now, targetIsSelected: false);

        var cleared = state.Acknowledge();

        Assert.False(cleared);
        Assert.NotNull(state.Active(Now));
    }

    [Fact]
    public void PersistentAlertStaysActivePastExpiry()
    {
        var state = new PreviewAlertState();
        var now = new DateTime(2026, 8, 17, 12, 0, 0, DateTimeKind.Utc);
        var alert = new TriffViewPreviewAlert(2, "#ff3b3b", 3, 1000, 4, Persistent: true);

        state.Arm(alert, now, targetIsSelected: false);

        var farPast = now.AddHours(1);
        var active = state.Active(farPast);

        Assert.NotNull(active);
        Assert.True(active!.Persistent);
    }

    [Fact]
    public void ClearExpiredNeverClearsAPersistentAlert()
    {
        var state = new PreviewAlertState();
        var now = new DateTime(2026, 8, 17, 12, 0, 0, DateTimeKind.Utc);
        var alert = new TriffViewPreviewAlert(2, "#ff3b3b", 3, 1000, 4, Persistent: true);

        state.Arm(alert, now, targetIsSelected: false);

        var cleared = state.ClearExpired(now.AddHours(1));

        Assert.False(cleared);
        Assert.NotNull(state.Active(now.AddHours(1)));
    }

    [Fact]
    public void AcknowledgeClearsAPersistentAlertAndReturnsTrue()
    {
        var state = new PreviewAlertState();
        var now = new DateTime(2026, 8, 17, 12, 0, 0, DateTimeKind.Utc);
        var alert = new TriffViewPreviewAlert(2, "#ff3b3b", 3, 1000, 4, Persistent: true);

        state.Arm(alert, now, targetIsSelected: false);

        var acknowledged = state.Acknowledge();

        Assert.True(acknowledged);
        Assert.Null(state.Active(now.AddHours(1)));
    }

    [Fact]
    public void ArmWithTargetSelectedProducesANonPersistentAlertEvenWhenRequested()
    {
        var state = new PreviewAlertState();
        var now = new DateTime(2026, 8, 17, 12, 0, 0, DateTimeKind.Utc);
        var alert = new TriffViewPreviewAlert(2, "#ff3b3b", 3, 1000, 4, Persistent: true);

        state.Arm(alert, now, targetIsSelected: true);

        var active = state.Active(now);
        Assert.NotNull(active);
        Assert.False(active!.Persistent);

        // And because it is not persistent, it clears exactly like a normal flash.
        Assert.True(state.ClearExpired(now.AddSeconds(2)));
    }

    [Fact]
    public void LowerSeverityAlertArrivingAfterPersistentAlertsNominalExpiryDoesNotReplaceOrDowngradeIt()
    {
        var state = new PreviewAlertState();
        var now = new DateTime(2026, 8, 17, 12, 0, 0, DateTimeKind.Utc);
        var persistent = new TriffViewPreviewAlert(3, "#ff3b3b", 3, 1000, 4, Persistent: true);
        state.Arm(persistent, now, targetIsSelected: false);

        // Nominal ExpiresUtc (now + 1000ms) is long past; a persistent alert must still
        // read as logically active, so the severity guard must reject this lower-severity
        // arrival rather than treating the persistent alert as expired and free to replace.
        var muchLater = now.AddHours(1);
        var lowerSeverity = new TriffViewPreviewAlert(1, "#ffd23b", 2, 1000, 2, Persistent: false);
        state.Arm(lowerSeverity, muchLater, targetIsSelected: false);

        var active = state.Active(muchLater);
        Assert.NotNull(active);
        Assert.Equal(3, active!.SeverityRank);
        Assert.True(active.Persistent);
        Assert.Equal("#ff3b3b", active.Color);
    }

    [Fact]
    public void ReplacingHigherSeverityAlertRecomputesPersistenceRatherThanInheritingIt()
    {
        var state = new PreviewAlertState();
        var now = new DateTime(2026, 8, 17, 12, 0, 0, DateTimeKind.Utc);
        var persistentLow = new TriffViewPreviewAlert(1, "#ffd23b", 2, 1000, 2, Persistent: true);
        state.Arm(persistentLow, now, targetIsSelected: false);

        // Higher severity replaces outright (not just an expiry bump), and targetIsSelected
        // is true this time, so the replacement must come out non-persistent even though
        // the alert it replaced was persistent and the incoming alert also requests it.
        var higherSeverity = new TriffViewPreviewAlert(3, "#ff3b3b", 3, 1000, 4, Persistent: true);
        state.Arm(higherSeverity, now, targetIsSelected: true);

        var active = state.Active(now);
        Assert.NotNull(active);
        Assert.Equal(3, active!.SeverityRank);
        Assert.False(active.Persistent);
    }
}

public class PreviewAlertStatePhaseTests
{
    [Fact]
    public void NonPersistentAlert_ProgressClampsAtOne()
    {
        var started = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var farPastExpiry = started.AddMilliseconds(50_000);

        var progress = PreviewAlertState.AlertProgress(started, farPastExpiry, durationMs: 1000, persistent: false);

        Assert.Equal(1.0, progress);
    }

    [Fact]
    public void PersistentAlert_ProgressKeepsAdvancingPastDuration()
    {
        var started = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var wellPastNominalExpiry = started.AddMilliseconds(3500);

        var progress = PreviewAlertState.AlertProgress(started, wellPastNominalExpiry, durationMs: 1000, persistent: true);

        Assert.Equal(3.5, progress, precision: 6);
    }

    [Fact]
    public void PersistentAlert_BeforeDuration_MatchesNonPersistent()
    {
        var started = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var midway = started.AddMilliseconds(400);

        var persistentProgress = PreviewAlertState.AlertProgress(started, midway, durationMs: 1000, persistent: true);
        var nonPersistentProgress = PreviewAlertState.AlertProgress(started, midway, durationMs: 1000, persistent: false);

        Assert.Equal(nonPersistentProgress, persistentProgress, precision: 6);
    }
}
