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
}
