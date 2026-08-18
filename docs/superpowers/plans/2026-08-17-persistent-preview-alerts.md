# Persistent Preview Alerts Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add an off-by-default global setting that makes preview-overlay flash alerts keep pulsing until the character they fired for is the foreground EVE client.

**Architecture:** The alert state machine is extracted from the private nested `PreviewState` onto a new internal top-level `PreviewAlertState`, which is the only place activity, expiry, severity replacement, and acknowledgement are decided. The overlay gains one new signal, `_selectedHandle` — the real foreground window when it belongs to an EVE client, and `nint.Zero` otherwise — which is deliberately distinct from the existing `_activeClientHandle`. Rendering changes only by letting the existing sine wave free-run instead of clamping, and the repaint tick moves from whole-form invalidation to a bounded dirty-rect union.

**Tech Stack:** C# / .NET 8 (`net8.0-windows`, WPF + WinForms), WinForms overlay form with DWM thumbnails, xunit, React 18 + Vite (`.jsx`, not TypeScript).

**Spec:** `docs/superpowers/specs/2026-08-17-persistent-preview-alerts-design.md`

## Global Constraints

Every task's requirements implicitly include this section.

- **Setting name and default.** `Alerts.PersistUntilSelected`, C# `bool`, default `false`. Web/JSON key `persistUntilSelected`. Default `false` is what preserves existing behaviour on upgrade — never change it to `true` "for testing".
- **No migration.** `DefaultsVersion` and `MigrateFromLegacyDefaults` are per-event concerns and must not be touched. An absent key deserializes to `false`; an unknown key is ignored by `System.Text.Json` and by the per-key patch switch on both sides.
- **No new WebView2 message type.** The setting rides the existing `triffalerts:update-settings` patch. Adding a new `type` string risks collision in the first-handler-wins dispatch chain.
- **Windows-only verification.** This repo cannot be built or run from WSL. Every build/test step is a `powershell.exe` invocation using the explicit-SDK command below. The web UI is the exception: it builds in WSL, because npm is not on the Windows PATH here.
- **`scripts/*.ps1` do not work from a worktree.** `build-native.ps1:4` resolves the repo root exactly one level up with no upward walk, so from a worktree it finds no `.dotnet` and dies with "No .NET SDKs were found". Always invoke the SDK explicitly.
- **Build/test command** (env vars always point at the MAIN checkout's caches; only the csproj path is worktree-local):

```powershell
powershell.exe -NoProfile -Command "$env:DOTNET_CLI_HOME='C:\dev\TriffView\.dotnet-home'; $env:NUGET_PACKAGES='C:\dev\TriffView\.nuget'; $env:APPDATA='C:\dev\TriffView\.appdata'; $env:NUGET_HTTP_CACHE_PATH='C:\dev\TriffView\.nuget-cache'; $env:DOTNET_CLI_TELEMETRY_OPTOUT='1'; & 'C:\dev\TriffView\.dotnet\dotnet.exe' test 'C:\dev\TriffView\.claude\worktrees\feat+persistent-preview-alerts\native\TriffView.Tests\TriffView.Tests.csproj' -c Release"
```

- **Overriding `APPDATA` redirects everything else in that shell.** Never combine a build invocation with runtime file work (log inspection, settings editing) in the same call — it will silently target `C:\dev\TriffView\.appdata\TriffHud\` instead of the real settings directory.
- **Do not compare preview positions against monitor bounds.** Saved preview positions and `Screen.AllScreens` bounds are not reliably in the same coordinate space on this hardware. An earlier attempt to validate positions this way moved correctly-placed previews off screen. This change moves no rectangles; it only invalidates ones that already exist.
- **Do not generalise the ghosting comment at `TriffViewSubsystem.cs:3971`.** It describes the *layered* `TriffViewLabelOverlayForm`. `TriffViewOverlayForm` is not layered and already uses bounded invalidation at `:3054` and `:3091`.
- **Out of scope**, per the spec: per-event-type persistence, severity thresholds, a maximum persistence cap, an alternate slow cadence, and any repeating sound. Persistence is visual only — sound and tray notification stay one-shot.

## File Structure

| File | Responsibility | Task |
|------|----------------|------|
| `native/TriffView/PreviewAlertState.cs` *(new)* | The only home for alert data and lifetime logic: `TriffViewPreviewAlert`, `ActivePreviewAlert`, and the `PreviewAlertState` machine (arm, expire, acknowledge, severity-replace, pulse phase). No WinForms dependency beyond what the alert types already carry. | 1, 4 |
| `native/TriffView/TriffViewSubsystem.cs` | Loses the alert lifetime logic. Keeps orchestration: computing `_selectedHandle`, passing the setting into `ProcessPendingAlerts`, and the repaint tick. | 1, 3, 4 |
| `native/TriffAlerts/TriffAlertsService.cs` | Owns the persisted setting and its serialization. | 2 |
| `app/src/tools/TriffViewSettings.jsx` | Owns the toggle, its default, and its normalization. | 2 |
| `native/TriffView.Tests/PreviewAlertStateTests.cs` *(new)* | Every behavioural assertion about alert lifetime. | 1, 3, 4 |

`PreviewState` stays a private nested class inside `TriffViewOverlayForm` and stays untestable by construction. After Task 1 it holds only window and geometry concerns plus a `PreviewAlertState` reference, which is what makes the behaviour reachable from tests at all.

---
### Task 1: Extract the alert state machine

**Files:**
- Create: `native/TriffView/PreviewAlertState.cs`
- Modify: `native/TriffView/TriffViewSubsystem.cs:2806-2834` (delete `TriffViewPreviewAlert`/`ActivePreviewAlert`)
- Modify: `native/TriffView/TriffViewSubsystem.cs:1192-1198` (`ProcessPendingAlerts` — add trailing `false`)
- Modify: `native/TriffView/TriffViewSubsystem.cs:3141-3157` (`ShowAlert`)
- Modify: `native/TriffView/TriffViewSubsystem.cs:3590-3594` (paint path)
- Modify: `native/TriffView/TriffViewSubsystem.cs:3618-3637` (`TickAlertFlashes`, `UpdateAlertTimer`)
- Modify: `native/TriffView/TriffViewSubsystem.cs:3808-3851` (`PreviewState`)
- Test: `native/TriffView.Tests/PreviewAlertStateTests.cs`

**Interfaces:**
- Consumes: nothing — this is the first task.
- Produces (later tasks build on these exact signatures):
  - `internal sealed record TriffViewPreviewAlert(int SeverityRank, string Color, int Thickness, int DurationMs, int PulseCount, bool Persistent);`
  - `internal sealed class ActivePreviewAlert` with ctor `(int severityRank, string color, int thickness, int durationMs, int pulseCount, DateTime startedUtc, DateTime expiresUtc, bool persistent)` and get-only `Persistent`.
  - `internal sealed class PreviewAlertState { ActivePreviewAlert? Active(DateTime now); bool Arm(TriffViewPreviewAlert alert, DateTime now, bool targetIsSelected); bool ClearExpired(DateTime now); bool Acknowledge(); }`
  - `PreviewState.Alerts` (public, get-only, type `PreviewAlertState`) replaces the old `Alert`/`SetAlert`/`ActiveAlert`/`ClearExpiredAlert` members.

- [ ] **Step 1: Write the failing test file against the new (not-yet-existing) type**

Create `native/TriffView.Tests/PreviewAlertStateTests.cs`:

```csharp
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
```

- [ ] **Step 2: Run the test project and confirm it fails to compile**

```powershell
powershell.exe -NoProfile -Command "$env:DOTNET_CLI_HOME='C:\dev\TriffView\.dotnet-home'; $env:NUGET_PACKAGES='C:\dev\TriffView\.nuget'; $env:APPDATA='C:\dev\TriffView\.appdata'; $env:NUGET_HTTP_CACHE_PATH='C:\dev\TriffView\.nuget-cache'; $env:DOTNET_CLI_TELEMETRY_OPTOUT='1'; & 'C:\dev\TriffView\.dotnet\dotnet.exe' test 'C:\dev\TriffView\.claude\worktrees\feat+persistent-preview-alerts\native\TriffView.Tests\TriffView.Tests.csproj' -c Release --filter \"FullyQualifiedName~PreviewAlertStateTests\""
```

Expected failure: this does not run any test — it fails at build. `PreviewAlertState` does not exist yet (`CS0246: The type or namespace name 'PreviewAlertState' could not be found`), and the `TriffViewPreviewAlert` record still only takes 5 positional parameters, so the 6-argument constructor calls in the test (`persistent` as the trailing arg) fail with `CS1729: 'TriffViewPreviewAlert' does not contain a constructor that takes 6 arguments`. Confirm the build output shows exactly these two error classes before moving on.

- [ ] **Step 3: Create `native/TriffView/PreviewAlertState.cs` with the moved types plus the new state machine**

```csharp
using System;

namespace TriffView.Preview;

internal sealed record TriffViewPreviewAlert(
    int SeverityRank,
    string Color,
    int Thickness,
    int DurationMs,
    int PulseCount,
    bool Persistent
);

internal sealed class ActivePreviewAlert
{
    public ActivePreviewAlert(int severityRank, string color, int thickness, int durationMs, int pulseCount, DateTime startedUtc, DateTime expiresUtc, bool persistent)
    {
        SeverityRank = severityRank;
        Color = color;
        Thickness = thickness;
        DurationMs = durationMs;
        PulseCount = pulseCount;
        StartedUtc = startedUtc;
        ExpiresUtc = expiresUtc;
        Persistent = persistent;
    }

    public int SeverityRank { get; }
    public string Color { get; }
    public int Thickness { get; }
    public int DurationMs { get; }
    public int PulseCount { get; }
    public DateTime StartedUtc { get; }
    public DateTime ExpiresUtc { get; set; }
    public bool Persistent { get; }
}

internal sealed class PreviewAlertState
{
    private ActivePreviewAlert? _alert;

    private static bool IsLogicallyActive(ActivePreviewAlert? alert, DateTime now)
        => alert != null && (alert.Persistent || alert.ExpiresUtc > now);

    public ActivePreviewAlert? Active(DateTime now) => IsLogicallyActive(_alert, now) ? _alert : null;

    public bool Arm(TriffViewPreviewAlert alert, DateTime now, bool targetIsSelected)
    {
        var persistent = alert.Persistent && !targetIsSelected;
        if (IsLogicallyActive(_alert, now) && _alert!.SeverityRank > alert.SeverityRank)
        {
            _alert.ExpiresUtc = now.AddMilliseconds(Math.Max(1, alert.DurationMs));
            return true;
        }

        _alert = new ActivePreviewAlert(
            alert.SeverityRank,
            alert.Color,
            alert.Thickness,
            alert.DurationMs,
            alert.PulseCount,
            now,
            now.AddMilliseconds(Math.Max(1, alert.DurationMs)),
            persistent
        );
        return true;
    }

    public bool ClearExpired(DateTime now)
    {
        if (_alert == null || IsLogicallyActive(_alert, now)) return false;
        _alert = null;
        return true;
    }

    public bool Acknowledge()
    {
        if (_alert == null || !_alert.Persistent) return false;
        _alert = null;
        return true;
    }
}
```

- [ ] **Step 4: Delete the old `TriffViewPreviewAlert`/`ActivePreviewAlert` definitions from `TriffViewSubsystem.cs`**

Remove lines 2806-2834 of `native/TriffView/TriffViewSubsystem.cs` (the `internal sealed record TriffViewPreviewAlert(...)` block and the `internal sealed class ActivePreviewAlert { ... }` block immediately above `internal sealed class TriffViewOverlayForm`). Nothing replaces them in this file — they now live only in `PreviewAlertState.cs`, same namespace (`TriffView.Preview`), so no `using` changes are needed elsewhere.

- [ ] **Step 5: Replace the alert members on the private nested `PreviewState` class**

In `native/TriffView/TriffViewSubsystem.cs`, inside `private sealed class PreviewState` (around line 3808), replace:

```csharp
        public bool Active { get; set; }
        public bool Visible { get; set; }
        private ActivePreviewAlert? Alert { get; set; }

        public void SetAlert(TriffViewPreviewAlert alert, DateTime now)
        {
            if (Alert != null && Alert.ExpiresUtc > now && Alert.SeverityRank > alert.SeverityRank)
            {
                Alert.ExpiresUtc = now.AddMilliseconds(Math.Max(1, alert.DurationMs));
                return;
            }

            Alert = new ActivePreviewAlert(
                alert.SeverityRank,
                alert.Color,
                alert.Thickness,
                alert.DurationMs,
                alert.PulseCount,
                now,
                now.AddMilliseconds(Math.Max(1, alert.DurationMs))
            );
        }

        public ActivePreviewAlert? ActiveAlert(DateTime now) => Alert != null && Alert.ExpiresUtc > now ? Alert : null;

        public bool ClearExpiredAlert(DateTime now)
        {
            if (Alert == null || Alert.ExpiresUtc > now) return false;
            Alert = null;
            return true;
        }
    }
```

with:

```csharp
        public bool Active { get; set; }
        public bool Visible { get; set; }
        public PreviewAlertState Alerts { get; } = new();
    }
```

- [ ] **Step 6: Update `ShowAlert` to arm through `state.Alerts`**

In `ShowAlert` (around line 3150), replace:

```csharp
            if (!MatchesAlertTarget(state, characterName)) continue;
            state.SetAlert(alert, now);
            matched = true;
```

with:

```csharp
            if (!MatchesAlertTarget(state, characterName)) continue;
            state.Alerts.Arm(alert, now, targetIsSelected: false);
            matched = true;
```

`targetIsSelected` stays a hard-coded `false` in this task — nothing sets `Persistent` true yet, so the value is inert. Task 2 (the `selectedHandle` plumbing) is what makes it meaningful.

- [ ] **Step 7: Update the paint path**

In the paint method (around line 3590), replace:

```csharp
        var activeAlert = state.ActiveAlert(DateTime.UtcNow);
```

with:

```csharp
        var activeAlert = state.Alerts.Active(DateTime.UtcNow);
```

- [ ] **Step 8: Update `TickAlertFlashes` and `UpdateAlertTimer`**

Replace:

```csharp
    private void TickAlertFlashes()
    {
        var now = DateTime.UtcNow;
        var removed = false;
        foreach (var state in _previews.Values)
        {
            removed |= state.ClearExpiredAlert(now);
        }

        var anyActive = _previews.Values.Any(state => state.ActiveAlert(now) != null);
        if (!anyActive) _alertTimer.Stop();
        if (removed || anyActive) Invalidate();
    }

    private void UpdateAlertTimer()
    {
        var anyActive = _previews.Values.Any(state => state.ActiveAlert(DateTime.UtcNow) != null);
        if (anyActive && !_alertTimer.Enabled) _alertTimer.Start();
        if (!anyActive && _alertTimer.Enabled) _alertTimer.Stop();
    }
```

with:

```csharp
    private void TickAlertFlashes()
    {
        var now = DateTime.UtcNow;
        var removed = false;
        foreach (var state in _previews.Values)
        {
            removed |= state.Alerts.ClearExpired(now);
        }

        var anyActive = _previews.Values.Any(state => state.Alerts.Active(now) != null);
        if (!anyActive) _alertTimer.Stop();
        if (removed || anyActive) Invalidate();
    }

    private void UpdateAlertTimer()
    {
        var anyActive = _previews.Values.Any(state => state.Alerts.Active(DateTime.UtcNow) != null);
        if (anyActive && !_alertTimer.Enabled) _alertTimer.Start();
        if (!anyActive && _alertTimer.Enabled) _alertTimer.Stop();
    }
```

This task does not touch the bare `Invalidate()` call or the timer's bounded-vs-whole-form repaint behaviour — that is Task 3's job per the spec. Only the member names change here.

- [ ] **Step 9: Update `ProcessPendingAlerts` to pass the new trailing `Persistent` argument**

In `native/TriffView/TriffViewSubsystem.cs` (around line 1192), replace:

```csharp
                    _overlay.ShowAlert(alert.CharacterName, new TriffViewPreviewAlert(
                        config.SeverityRank,
                        config.FlashColor,
                        config.FlashThickness,
                        config.FlashDurationMs,
                        config.FlashPulseCount
                    ));
```

with:

```csharp
                    _overlay.ShowAlert(alert.CharacterName, new TriffViewPreviewAlert(
                        config.SeverityRank,
                        config.FlashColor,
                        config.FlashThickness,
                        config.FlashDurationMs,
                        config.FlashPulseCount,
                        false
                    ));
```

The trailing `false` is deliberate: `Settings.Alerts.PersistUntilSelected` does not exist until Task 4, so this is the only call site that constructs a `TriffViewPreviewAlert` today, and it must keep producing non-persistent alerts until that setting lands.

- [ ] **Step 10: Build the native project**

```powershell
powershell.exe -NoProfile -Command "$env:DOTNET_CLI_HOME='C:\dev\TriffView\.dotnet-home'; $env:NUGET_PACKAGES='C:\dev\TriffView\.nuget'; $env:APPDATA='C:\dev\TriffView\.appdata'; $env:NUGET_HTTP_CACHE_PATH='C:\dev\TriffView\.nuget-cache'; $env:DOTNET_CLI_TELEMETRY_OPTOUT='1'; & 'C:\dev\TriffView\.dotnet\dotnet.exe' build 'C:\dev\TriffView\.claude\worktrees\feat+persistent-preview-alerts\native\TriffView.csproj' -c Release"
```

Expect a clean build with zero errors. If anything still references `SetAlert`, `ActiveAlert`, or `ClearExpiredAlert` on `PreviewState`, or references the deleted top-level types by the old constructor arity, the build will name the exact call site — fix it there rather than re-adding removed members.

- [ ] **Step 11: Run the full test project and confirm the new tests pass**

```powershell
powershell.exe -NoProfile -Command "$env:DOTNET_CLI_HOME='C:\dev\TriffView\.dotnet-home'; $env:NUGET_PACKAGES='C:\dev\TriffView\.nuget'; $env:APPDATA='C:\dev\TriffView\.appdata'; $env:NUGET_HTTP_CACHE_PATH='C:\dev\TriffView\.nuget-cache'; $env:DOTNET_CLI_TELEMETRY_OPTOUT='1'; & 'C:\dev\TriffView\.dotnet\dotnet.exe' test 'C:\dev\TriffView\.claude\worktrees\feat+persistent-preview-alerts\native\TriffView.Tests\TriffView.Tests.csproj' -c Release"
```

Expect all six new `PreviewAlertStateTests` facts to pass, and no pre-existing test (including `PreviewPositionMemoryTests`) to regress.

- [ ] **Step 12: Commit**

```bash
git add native/TriffView/PreviewAlertState.cs native/TriffView/TriffViewSubsystem.cs native/TriffView.Tests/PreviewAlertStateTests.cs
git commit -m "Extract preview alert state machine into PreviewAlertState (no behaviour change)"
```
### Task 2: Add the PersistUntilSelected setting

**Files:**
- Modify: `native/TriffAlerts/TriffAlertsService.cs:10-75` (TriffAlertsSettings: new property, ToState())
- Modify: `native/TriffView/TriffViewSubsystem.cs:1078-1096` (ApplyAlertsPatch switch)
- Modify: `app/src/tools/TriffViewSettings.jsx:100-112` (DEFAULT_ALERTS), `:515-532` (normalizeAlertsState), `:1727-1730` (toggle grid)
- Test: `native/TriffView.Tests/TriffAlertsSettingsPersistUntilSelectedTests.cs` (new file)

**Interfaces:**
- Consumes: nothing from Task 1 — this task only threads a bare bool through settings; `PreviewAlertState`/`selectedHandle` from Task 1 are not referenced here.
- Produces: `Settings.Alerts.PersistUntilSelected` (bool, default `false`), the web key `persistUntilSelected`, and the `triffalerts:update-settings` patch key `"persistUntilSelected"`. Task 3 (the wiring that actually makes alerts persist) reads `Settings.Alerts.PersistUntilSelected` by this exact name.

**Test project choice:** `native/TriffView.Tests`. `tests/TriffAlerts.Tests` is not an xunit project — it's a hand-rolled `Program.cs` runner (`OutputType=Exe`) that drives `TriffAlertsService` end-to-end against real log files for log-watching scenarios (existing-log replay, live tailing, multi-client fan-out). It has no facility for isolated `[Fact]`-style assertions on JSON serialization. `native/TriffView.Tests` is a proper xunit project (`net8.0-windows`, `ProjectReference` to `TriffView.csproj`, `Microsoft.NET.Test.Sdk` + `xunit` packages) and is where Task 1 already places `PreviewAlertStateTests.cs`. `TriffAlertsSettings` is `public sealed`, so no `InternalsVisibleTo` is even needed to reach it from there. This keeps all the new settings-plumbing tests in one place alongside the alert-state tests.

- [ ] **Step 1: Write the failing settings/compat tests**

Create `native/TriffView.Tests/TriffAlertsSettingsPersistUntilSelectedTests.cs`:

```csharp
using System.Text.Json;
using TriffView.Alerts;
using Xunit;

namespace TriffView.Tests;

public class TriffAlertsSettingsPersistUntilSelectedTests
{
    [Fact]
    public void DefaultsToFalse()
    {
        var settings = TriffAlertsSettings.CreateDefault();

        Assert.False(settings.PersistUntilSelected);
    }

    [Fact]
    public void RoundTripsThroughToState()
    {
        // ToState() is declared as `object` and returns an anonymous type
        // (TriffAlertsService.cs:60-75). Do NOT reach into it with `dynamic`: anonymous
        // types are internal to their assembly, cross-assembly dynamic binding on one is
        // at best fragile, and — because dynamic binding is deferred to runtime — a missing
        // member would not fail the build, so the TDD red step would not mean what it says.
        // Assert on the serialized JSON instead. That is also what actually crosses the
        // WebView2 boundary to the settings UI, so it is the more faithful test.
        var settings = TriffAlertsSettings.CreateDefault();
        settings.PersistUntilSelected = true;

        var json = JsonSerializer.Serialize(settings.ToState());

        using var document = JsonDocument.Parse(json);
        Assert.True(document.RootElement.GetProperty("persistUntilSelected").GetBoolean());
    }

    [Fact]
    public void SettingsWrittenByAnOlderBuild_KeyAbsent_DeserializesToFalse()
    {
        // No "persistUntilSelected" key at all -- simulates a settings.json
        // written before this feature existed.
        const string json = """
        {
            "profiles": [],
            "alerts": {
                "defaultsVersion": 2,
                "enabled": true,
                "pveMode": true,
                "masterVolume": 0.5,
                "events": {}
            }
        }
        """;

        var settings = TriffViewSettings.FromJson(json);

        Assert.False(settings.Alerts.PersistUntilSelected);
    }

    [Fact]
    public void UnknownKeyInAlertsJson_IsIgnoredRatherThanThrowing()
    {
        // Simulates a NEWER build's settings.json being read by this build,
        // or a hand-edited file with an extra key. System.Text.Json ignores
        // unrecognized members by default; this pins that behavior for the
        // alerts section specifically.
        const string json = """
        {
            "profiles": [],
            "alerts": {
                "defaultsVersion": 2,
                "enabled": true,
                "pveMode": true,
                "masterVolume": 0.5,
                "persistUntilSelected": true,
                "someFutureAlertsKey": "unrecognized-value",
                "events": {}
            }
        }
        """;

        var settings = TriffViewSettings.FromJson(json);

        Assert.True(settings.Alerts.PersistUntilSelected);
        Assert.True(settings.Alerts.Enabled);
    }
}
```

- [ ] **Step 2: Run the tests and confirm they fail for the right reason**

```powershell
powershell.exe -NoProfile -Command "$env:DOTNET_CLI_HOME='C:\dev\TriffView\.dotnet-home'; $env:NUGET_PACKAGES='C:\dev\TriffView\.nuget'; $env:APPDATA='C:\dev\TriffView\.appdata'; $env:NUGET_HTTP_CACHE_PATH='C:\dev\TriffView\.nuget-cache'; $env:DOTNET_CLI_TELEMETRY_OPTOUT='1'; & 'C:\dev\TriffView\.dotnet\dotnet.exe' test 'C:\dev\TriffView\.claude\worktrees\feat+persistent-preview-alerts\native\TriffView.Tests\TriffView.Tests.csproj' -c Release --filter "FullyQualifiedName~TriffAlertsSettingsPersistUntilSelectedTests""
```

Expected failure: `DefaultsToFalse` and `RoundTripsThroughToState` both fail to build with `CS1061: 'TriffAlertsSettings' does not contain a definition for 'PersistUntilSelected'` — the property assignment and the assertion both reference it directly. (The JSON assertion itself would only fail at runtime, which is exactly why the test sets the property rather than reaching into the anonymous type dynamically.) (`SettingsWrittenByAnOlderBuild...` and `UnknownKeyInAlertsJson...` would currently pass against the old shape by accident, which is why the first two tests matter — they pin the new member's existence and correct default.)

- [ ] **Step 3: Add the setting to TriffAlertsSettings**

In `native/TriffAlerts/TriffAlertsService.cs`, add the property next to `PveMode` (around line 15):

```csharp
    public bool Enabled { get; set; }
    public bool PveMode { get; set; } = true;
    public bool PersistUntilSelected { get; set; }
    public double MasterVolume { get; set; } = 0.75;
```

Do not add anything for it in `Normalize()` — it is a bool with no range to clamp.

In `ToState()`, add the field next to `pveMode` (around line 63):

```csharp
        return new
        {
            defaultsVersion = DefaultsVersion,
            enabled = Enabled,
            pveMode = PveMode,
            persistUntilSelected = PersistUntilSelected,
            masterVolume = MasterVolume,
            events = Events.ToDictionary(
                item => item.Key,
                item => item.Value.ToState(),
                StringComparer.OrdinalIgnoreCase
            ),
        };
```

- [ ] **Step 4: Wire the patch key in TriffViewSubsystem**

In `native/TriffView/TriffViewSubsystem.cs`, `ApplyAlertsPatch`, add the case next to `pveMode` (around line 1088):

```csharp
                case "pveMode":
                    alerts.PveMode = value?.GetValue<bool>() == true;
                    break;
                case "persistUntilSelected":
                    alerts.PersistUntilSelected = value?.GetValue<bool>() == true;
                    break;
                case "masterVolume":
```

- [ ] **Step 5: Run the tests again and confirm they pass**

```powershell
powershell.exe -NoProfile -Command "$env:DOTNET_CLI_HOME='C:\dev\TriffView\.dotnet-home'; $env:NUGET_PACKAGES='C:\dev\TriffView\.nuget'; $env:APPDATA='C:\dev\TriffView\.appdata'; $env:NUGET_HTTP_CACHE_PATH='C:\dev\TriffView\.nuget-cache'; $env:DOTNET_CLI_TELEMETRY_OPTOUT='1'; & 'C:\dev\TriffView\.dotnet\dotnet.exe' test 'C:\dev\TriffView\.claude\worktrees\feat+persistent-preview-alerts\native\TriffView.Tests\TriffView.Tests.csproj' -c Release --filter "FullyQualifiedName~TriffAlertsSettingsPersistUntilSelectedTests""
```

All four tests pass.

- [ ] **Step 6: Build the full native project to confirm nothing else broke**

```powershell
powershell.exe -NoProfile -Command "$env:DOTNET_CLI_HOME='C:\dev\TriffView\.dotnet-home'; $env:NUGET_PACKAGES='C:\dev\TriffView\.nuget'; $env:APPDATA='C:\dev\TriffView\.appdata'; $env:NUGET_HTTP_CACHE_PATH='C:\dev\TriffView\.nuget-cache'; $env:DOTNET_CLI_TELEMETRY_OPTOUT='1'; & 'C:\dev\TriffView\.dotnet\dotnet.exe' build 'C:\dev\TriffView\.claude\worktrees\feat+persistent-preview-alerts\native\TriffView.csproj' -c Release"
```

- [ ] **Step 7: Add the web default and normalizer coercion**

In `app/src/tools/TriffViewSettings.jsx`, `DEFAULT_ALERTS` (around line 103):

```jsx
const DEFAULT_ALERTS = {
  enabled: false,
  pveMode: true,
  persistUntilSelected: false,
  masterVolume: 0.75,
  events: DEFAULT_ALERT_EVENTS,
};
```

In `normalizeAlertsState` (around line 520), follow the exact style of the `masterVolume` coercion line:

```jsx
  return {
    ...DEFAULT_ALERTS,
    ...source,
    masterVolume: Number.isFinite(Number(source.masterVolume)) ? Number(source.masterVolume) : DEFAULT_ALERTS.masterVolume,
    persistUntilSelected: source.persistUntilSelected === true,
    events,
  };
```

- [ ] **Step 8: Add the toggle to the alerts panel**

In `app/src/tools/TriffViewSettings.jsx`, in the toggle grid next to the PvP toggle (around line 1727-1730):

```jsx
          <div className="triffview-toggle-grid">
            <Toggle label="Enable alerts" checked={alerts.enabled} onChange={(value) => patchAlerts({ enabled: value })} />
            <Toggle label="Only alert in PvP, ignore NPC's" checked={alerts.pveMode} onChange={(value) => patchAlerts({ pveMode: value })} />
            <Toggle
              label="Keep alerting until I select that character"
              checked={alerts.persistUntilSelected}
              onChange={(value) => patchAlerts({ persistUntilSelected: value })}
            />
          </div>
```

- [ ] **Step 9: Build the web UI**

The native app cannot be built from WSL and the web UI cannot be built from Windows here (no npm on Windows PATH) — build the web UI in WSL:

```bash
cd app && npm run build
```

Confirm the build succeeds with no new warnings from this file.

- [ ] **Step 10: Commit**

```bash
git add native/TriffAlerts/TriffAlertsService.cs native/TriffView/TriffViewSubsystem.cs app/src/tools/TriffViewSettings.jsx native/TriffView.Tests/TriffAlertsSettingsPersistUntilSelectedTests.cs
git commit -m "Add PersistUntilSelected alerts setting (round-trips end to end, unused by behavior yet)"
```
### Task 3: Arm and acknowledge persistent alerts

**Files:**
- Modify: `native/TriffView/TriffViewSubsystem.cs:2852` (new field), `:1192-1198` (ProcessPendingAlerts), `:3027-3062` (MarkActiveClient), `:2938-3003` (SetClients), `:3064-3097` (SyncClientStates), `:3141-3157` (ShowAlert), `:3618-3630` (TickAlertFlashes)
- Test: `native/TriffView.Tests/PreviewAlertStateTests.cs`

**Interfaces:**
- Consumes (from Task 1, `native/TriffView/PreviewAlertState.cs`):
  - `internal sealed record TriffViewPreviewAlert(int SeverityRank, string Color, int Thickness, int DurationMs, int PulseCount, bool Persistent);`
  - `internal sealed class PreviewAlertState { ActivePreviewAlert? Active(DateTime now); bool Arm(TriffViewPreviewAlert alert, DateTime now, bool targetIsSelected); bool ClearExpired(DateTime now); bool Acknowledge(); }`
  - `PreviewState.Alerts` (get-only `PreviewAlertState`, replacing the old `Alert`/`SetAlert`/`ActiveAlert`/`ClearExpiredAlert` members)
- Consumes (from Task 2, `native/TriffAlerts/TriffAlertsService.cs`): `TriffAlertsSettings.PersistUntilSelected` (bool, default `false`), surfaced on `Settings.Alerts.PersistUntilSelected` at the call site.
- Produces: `TriffViewOverlayForm._selectedHandle` (nint) — the real foreground window handle IFF it belongs to an EVE client, else `nint.Zero`. Task 4's dirty-rect invalidation in `TickAlertFlashes` and its `DrawAlertBorder` unclamped-progress branch both read `state.Alerts.Active(now)!.Persistent` and compare `state.Client.Handle == _selectedHandle`; Task 4 does not need to touch `_selectedHandle` itself, only consume it.

- [ ] **Step 1: Write failing tests for persistent-alert lifetime on `PreviewAlertState`**

Append to `native/TriffView.Tests/PreviewAlertStateTests.cs` (the file Task 1 created with the toggle-off parity tests). Keep the existing `using` block and namespace from Task 1:

```csharp
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
```

Do not implement anything yet. These six tests, plus Task 1's own tests, are the full contents expected in `PreviewAlertStateTests.cs` after this task; Task 1's toggle-off-parity tests already cover the seventh bullet in scope ("with `Persistent:false` throughout, behaviour is identical to Task 1's tests") and need no duplicate here.

- [ ] **Step 2: Run the tests and confirm they compile against Task 1's `PreviewAlertState` but fail on behaviour not yet implemented**

```powershell
powershell.exe -NoProfile -Command "$env:DOTNET_CLI_HOME='C:\dev\TriffView\.dotnet-home'; $env:NUGET_PACKAGES='C:\dev\TriffView\.nuget'; $env:APPDATA='C:\dev\TriffView\.appdata'; $env:NUGET_HTTP_CACHE_PATH='C:\dev\TriffView\.nuget-cache'; $env:DOTNET_CLI_TELEMETRY_OPTOUT='1'; & 'C:\dev\TriffView\.dotnet\dotnet.exe' test 'C:\dev\TriffView\.claude\worktrees\feat+persistent-preview-alerts\native\TriffView.Tests\TriffView.Tests.csproj' -c Release --filter \"FullyQualifiedName~PreviewAlertStateTests\""
```

Expected: if Task 1 landed exactly per contract, `PersistentAlertStaysActivePastExpiry`, `ClearExpiredNeverClearsAPersistentAlert`, and `AcknowledgeClearsAPersistentAlertAndReturnsTrue` already PASS, because `Arm`/`Active`/`ClearExpired`/`Acknowledge` are Task 1's own contract and don't depend on anything in this task. The two tests that legitimately require no further production code beyond Task 1 are expected green here — that is fine, they are regression coverage for this task, not new-behaviour proof. `ArmWithTargetSelectedProducesANonPersistentAlertEvenWhenRequested`, `LowerSeverityAlertArrivingAfterPersistentAlertsNominalExpiryDoesNotReplaceOrDowngradeIt`, and `ReplacingHigherSeverityAlertRecomputesPersistenceRatherThanInheritingIt` are also implemented purely in terms of `Arm`'s `targetIsSelected` parameter per Task 1's Interfaces block, so if Task 1 wired that parameter correctly these should also already pass. If any of the six fail, the failure means Task 1's `Arm` body deviated from the interface Task 1 publishes — fix that expectation note in this task's notes and flag it, do not patch `PreviewAlertState.cs` from this task (Task 1 owns that file).

- [ ] **Step 3: Add `_selectedHandle` to `TriffViewOverlayForm`**

Edit `native/TriffView/TriffViewSubsystem.cs` — add the field next to `_foreground` at line 2852:

```csharp
    private nint _foreground;
    private nint _selectedHandle;
```

This is deliberately not a reuse of `_activeClientHandle` (declared at subsystem-class scope, line 54) or `_foreground` (declared just above, on this class). `_activeClientHandle` latches: `ApplyClientRefresh` sets it to the real foreground only when that foreground is an EVE client (`:508-511`), and `ObserveForegroundTransition` re-latches it to the real foreground on the next EVE-owning tick (`:610`) — but neither clears it when focus leaves EVE for Discord or a browser, so `_activeClientHandle` keeps naming the last-used client while you are tabbed away. That is exactly right for "which preview should still look highlighted," and exactly wrong for "has the user actually looked at this alert," which is what acknowledgement needs: tabbing to Discord must NOT silently acknowledge a persistent alert on the client you left. `_foreground` is closer but is overloaded by `MarkActiveClient` to mean "the client the subsystem wants highlighted" (`:3030`, called with `activeHandle`, not necessarily the live foreground window) rather than "the live foreground window, and only when it is EVE." `_selectedHandle` is the third, narrower thing: `nint.Zero` unless the OS's actual foreground window right now belongs to one of `clients`. A future reader who sees three near-identical nint fields on overlapping types will be tempted to collapse `_selectedHandle` into one of the other two; if that happens, alerts will spuriously stay acknowledged while the user is in a browser (via `_activeClientHandle`) or never come out of the hidden/highlighted-client special case (via `_foreground`). Keep it separate.

- [ ] **Step 4: Set `_selectedHandle` in `MarkActiveClient`**

In `native/TriffView/TriffViewSubsystem.cs`, `MarkActiveClient` (starts at line 3027):

```csharp
    public void MarkActiveClient(nint activeHandle)
    {
        if (activeHandle == nint.Zero) return;
        _foreground = activeHandle;
        _selectedHandle = activeHandle;
        _clients = _clients
            .Select(client => client with { IsForeground = client.Handle == activeHandle })
            .ToArray();
```

Safe because `MarkActiveClient` is only ever invoked from `ObserveForegroundTransition` (`native/TriffView/TriffViewSubsystem.cs:611`), which itself only calls it after confirming `foregroundIsEve` at `:603` and returning early at `:608` when it is false — so every `activeHandle` this method receives is already known to belong to an EVE client.

- [ ] **Step 5: Set `_selectedHandle` in `SetClients` and `SyncClientStates`**

`SetClients` (line 2938), right after `_foreground = foreground;`:

```csharp
        _clients = clients;
        _profile = profile;
        _foreground = foreground;
        _selectedHandle = clients.Any(c => c.Handle == foreground) ? foreground : nint.Zero;
        SizeToVirtualDesktop();
```

`SyncClientStates` (line 3064), right after `_foreground = foreground;`:

```csharp
        _clients = clients;
        _foreground = foreground;
        _selectedHandle = clients.Any(c => c.Handle == foreground) ? foreground : nint.Zero;
```

Both mirror the "real foreground, only if it's an EVE client" rule directly, since both methods already receive `foreground` as the live OS value (not `activeHandle`, which is the highlight target and can differ from it, e.g. when `HideActivePreview` is on).

- [ ] **Step 6: Run the build to confirm the field wiring compiles**

```powershell
powershell.exe -NoProfile -Command "$env:DOTNET_CLI_HOME='C:\dev\TriffView\.dotnet-home'; $env:NUGET_PACKAGES='C:\dev\TriffView\.nuget'; $env:APPDATA='C:\dev\TriffView\.appdata'; $env:NUGET_HTTP_CACHE_PATH='C:\dev\TriffView\.nuget-cache'; $env:DOTNET_CLI_TELEMETRY_OPTOUT='1'; & 'C:\dev\TriffView\.dotnet\dotnet.exe' build 'C:\dev\TriffView\.claude\worktrees\feat+persistent-preview-alerts\native\TriffView.csproj' -c Release"
```

Expected: builds clean. `_selectedHandle` is written in three places but not yet read anywhere, so expect no behavioural change and no warning (private fields that are only written, never read, do produce CS0414 in some configurations — if the build flags it, that is expected and resolved by Step 7/8 below, which add the reads).

- [ ] **Step 7: Wire `Settings.Alerts.PersistUntilSelected` into `ProcessPendingAlerts`**

In `native/TriffView/TriffViewSubsystem.cs`, `ProcessPendingAlerts` (line 1179), the `TriffViewPreviewAlert` construction at line 1192:

```csharp
                if (config.FlashEnabled)
                {
                    _overlay.ShowAlert(alert.CharacterName, new TriffViewPreviewAlert(
                        config.SeverityRank,
                        config.FlashColor,
                        config.FlashThickness,
                        config.FlashDurationMs,
                        config.FlashPulseCount,
                        Settings.Alerts.PersistUntilSelected
                    ));
                }
```

This is a request, not a final answer — `Persistent: true` here means "the toggle is on," and `PreviewAlertState.Arm` still resolves the already-selected exception via its `targetIsSelected` parameter (Step 8). `Settings.Alerts.PersistUntilSelected` is Task 2's field on `TriffAlertsSettings`, reachable here the same way `config.FlashEnabled` etc. already are, through `Settings.Alerts`.

- [ ] **Step 8: Wire `targetIsSelected` and `Alerts.Arm` into `ShowAlert`**

In `native/TriffView/TriffViewSubsystem.cs`, `ShowAlert` (line 3141):

```csharp
    public void ShowAlert(string characterName, TriffViewPreviewAlert alert)
    {
        if (string.IsNullOrWhiteSpace(characterName)) return;
        var now = DateTime.UtcNow;
        var matched = false;

        foreach (var state in _previews.Values)
        {
            if (!MatchesAlertTarget(state, characterName)) continue;
            state.Alerts.Arm(alert, now, targetIsSelected: state.Client.Handle == _selectedHandle);
            matched = true;
        }

        if (!matched) return;
        UpdateAlertTimer();
        Invalidate();
    }
```

This replaces the old `state.SetAlert(alert, now)` call. `UpdateAlertTimer` and the `Invalidate()` calls it and `TickAlertFlashes` make are unchanged in this task — they already read alert state through whatever `PreviewState` exposes, and per Task 1's contract that is now `state.Alerts.Active(now)` in place of `state.ActiveAlert(now)`. Do not change `UpdateAlertTimer` itself in this step: Task 1's contract already retargets `ActiveAlert`/`ClearExpiredAlert` calls throughout the file onto `Alerts.Active`/`Alerts.ClearExpired`, so by the time this task runs, `UpdateAlertTimer` (line 3632) should already read `state.Alerts.Active(DateTime.UtcNow) != null`. If it still reads the old `state.ActiveAlert(...)` form when you reach this step, that is a gap in Task 1 to flag, not something to silently patch here.

- [ ] **Step 9: Acknowledge in `TickAlertFlashes` — minimal edit, no repaint logic**

In `native/TriffView/TriffViewSubsystem.cs`, `TickAlertFlashes` (line 3618):

```csharp
    private void TickAlertFlashes()
    {
        var now = DateTime.UtcNow;
        var removed = false;
        foreach (var state in _previews.Values)
        {
            removed |= state.Alerts.ClearExpired(now);
            if (state.Client.Handle == _selectedHandle)
            {
                removed |= state.Alerts.Acknowledge();
            }
        }

        var anyActive = _previews.Values.Any(state => state.Alerts.Active(now) != null);
        if (!anyActive) _alertTimer.Stop();
        if (removed || anyActive) Invalidate();
    }
```

This is intentionally the smallest change that makes acknowledgement happen: one `Acknowledge()` call gated on `_selectedHandle`, folded into the same `removed` flag the expiry check already uses, so a newly-acknowledged persistent alert still triggers a repaint through the existing `if (removed || anyActive) Invalidate();` line. The `Invalidate()` here is still the bare, whole-form call already on this line today — it is not a regression introduced by this step, it is the pre-existing behaviour this task deliberately leaves alone. Task 4 replaces this bare `Invalidate()` with the bounded dirty-rect version described in the spec's "Timer and repaint cost" section (capturing each alert's frame rect before `ClearExpired`/`Acknowledge` runs, plus the `HideOnLostFocus`/`Opacity == 0` skip). Do not attempt any of that here — this step's only job is that `Acknowledge()` gets called on the right preview at the right time; Task 4 owns making the resulting repaint cheap.

- [ ] **Step 10: Run the full alert test filter and the build again**

```powershell
powershell.exe -NoProfile -Command "$env:DOTNET_CLI_HOME='C:\dev\TriffView\.dotnet-home'; $env:NUGET_PACKAGES='C:\dev\TriffView\.nuget'; $env:APPDATA='C:\dev\TriffView\.appdata'; $env:NUGET_HTTP_CACHE_PATH='C:\dev\TriffView\.nuget-cache'; $env:DOTNET_CLI_TELEMETRY_OPTOUT='1'; & 'C:\dev\TriffView\.dotnet\dotnet.exe' test 'C:\dev\TriffView\.claude\worktrees\feat+persistent-preview-alerts\native\TriffView.Tests\TriffView.Tests.csproj' -c Release --filter \"FullyQualifiedName~PreviewAlertStateTests\""
```

```powershell
powershell.exe -NoProfile -Command "$env:DOTNET_CLI_HOME='C:\dev\TriffView\.dotnet-home'; $env:NUGET_PACKAGES='C:\dev\TriffView\.nuget'; $env:APPDATA='C:\dev\TriffView\.appdata'; $env:NUGET_HTTP_CACHE_PATH='C:\dev\TriffView\.nuget-cache'; $env:DOTNET_CLI_TELEMETRY_OPTOUT='1'; & 'C:\dev\TriffView\.dotnet\dotnet.exe' build 'C:\dev\TriffView\.claude\worktrees\feat+persistent-preview-alerts\native\TriffView.csproj' -c Release"
```

Expected: all six new tests plus Task 1's pre-existing tests pass; the build is clean. These tests exercise `PreviewAlertState` directly and do not reach `_selectedHandle`, `ShowAlert`, or `TickAlertFlashes` at all — passing here proves Task 1's state machine, not this task's wiring. This task's wiring (`_selectedHandle` propagation, `ShowAlert`'s `targetIsSelected` computation, `TickAlertFlashes`'s `Acknowledge()` call) has no unit-test seam of its own because `TriffViewOverlayForm` is a WinForms form with no extracted pure-logic surface for it; it is verified by inspection here and by manual exercise on Windows per the spec's "Verification" section (arm a persistent alert on a non-selected client, confirm it keeps blinking; switch to that client; confirm it stops).

- [ ] **Step 11: Note the accepted limitations that intersect this task's code, so a reviewer doesn't file them as bugs**

No code change in this step. Two of the spec's "Accepted limitations" touch exactly the paths this task edits, and are expected behaviour, not regressions to fix:

- With `HideActivePreview` on, `SyncHiddenActivePreview` (line 3099) removes the `PreviewState` for whichever client is currently the highlight target, disposing its `Alerts` along with it. A persistent alert on that client is gone the moment it becomes selected — which happens to be the same client `_selectedHandle` would target for `Acknowledge()`, so the outcome (no lingering alert on the selected client) is correct even though it is reached by disposal rather than by `Acknowledge()` ever running.
- A `PreviewState` is recreated whenever its `PreviewClientIdentity` changes (`SetClients`, line 2982; `SyncHiddenActivePreview`, line 3118), for example when a client goes from character-select to a named character. The new `PreviewState` gets a fresh `Alerts` with no armed alert, so any persistent alert in flight for the old identity is silently dropped. This is called out in the spec's "Accepted limitations" and is out of scope to fix here.
- **`_selectedHandle` lags the foreground by up to 700 ms when you leave EVE entirely.** `ObserveForegroundTransition` returns early for a non-EVE foreground without notifying the overlay (`TriffViewSubsystem.cs:596-611`), so `_selectedHandle` is only cleared on the next periodic refresh through `SetClients`/`SyncClientStates` — and `_timer` runs at 700 ms (`TriffViewSubsystem.cs:107-110`). An alert that lands inside that window, targeting the client you *just* switched away from, arms as non-persistent and so flashes-and-stops instead of persisting. This is a deliberate accepted limitation, not an oversight: closing it means adding a foreground-change hook that fires for every non-EVE window activation, which is a materially larger change to the activation model than this feature warrants. The window is sub-second and the alert is still shown; only its persistence is lost. Do not "fix" this by making `_selectedHandle` fall back to `_activeClientHandle` — that reintroduces exactly the latching bug this field exists to avoid.

- [ ] **Step 12: Commit**

```powershell
powershell.exe -NoProfile -Command "cd 'C:\dev\TriffView\.claude\worktrees\feat+persistent-preview-alerts'; git add native/TriffView/TriffViewSubsystem.cs native/TriffView.Tests/PreviewAlertStateTests.cs; git commit -m 'Arm and acknowledge persistent preview alerts'"
```
### Task 4: Free-running pulse and bounded repaint

**Files:**
- Modify: `native/TriffView/PreviewAlertState.cs` (add one static method; created by Task 1)
- Modify: `native/TriffView/TriffViewSubsystem.cs:3602-3616` (`DrawAlertBorder`)
- Modify: `native/TriffView/TriffViewSubsystem.cs:3618-3630` (`TickAlertFlashes`)
- Modify: `native/TriffView/TriffViewSubsystem.cs:2842` area / `TriffViewOverlayForm` field list (add `_alertPaintSuppressed`)
- Test: `native/TriffView.Tests/PreviewAlertStateTests.cs` (append; created by Task 1)

**Interfaces:**
- Consumes:
  - `ActivePreviewAlert.Persistent` (`bool`, get-only) — published by Task 1's Interfaces block.
  - `ActivePreviewAlert.StartedUtc`, `.DurationMs`, `.PulseCount`, `.Color`, `.Thickness` — unchanged existing members.
  - `PreviewState.Alerts` (`PreviewAlertState`, on the private nested `PreviewState`) — published by Task 1. Call sites use `state.Alerts.Active(now)` and `state.Alerts.ClearExpired(now)`.
  - `PreviewAlertState.Active(DateTime now) : ActivePreviewAlert?` and `PreviewAlertState.ClearExpired(DateTime now) : bool` — published by Task 1's Interfaces block.
  - **Resolved (was an open assumption during drafting):** Task 3 adds the `Acknowledge()` call, and this task's Step 5 block below already includes it, feeding its result into the same `dirty` list. Do not remove it. Background: the design doc also says `TickAlertFlashes` clears a persistent alert when its preview's client becomes `_selectedHandle` (an `Acknowledge()` call). That wiring belongs to whichever task adds `_selectedHandle` to `TriffViewOverlayForm` (Task 3, not this one). This task's `TickAlertFlashes` only performs `ClearExpired`. If that other task adds an `Acknowledge()` loop inside `TickAlertFlashes`, it must feed its cleared-rect results into the same `dirty` list built below, using the identical `cleared || stillActive` pattern — otherwise an acknowledged alert's border will ghost exactly the way this task exists to prevent. Do not silently drop that requirement; call it out in that task's own review if it lands separately.
- Produces:
  - `PreviewAlertState.AlertProgress(DateTime startedUtc, DateTime now, int durationMs, bool persistent) : double` — a pure static helper, usable by any later task that needs the same phase maths (e.g. a settings-panel live preview).
  - `TriffViewOverlayForm._alertPaintSuppressed` (private bool) — internal to the form, not consumed elsewhere.

---

- [ ] **Step 1: Write failing tests for the phase helper**

Append to `native/TriffView.Tests/PreviewAlertStateTests.cs` (it already has `using TriffView.Preview;` / `using Xunit;` / `namespace TriffView.Tests;` from Task 1 — do not duplicate those):

```csharp
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
```

Run:

```powershell
powershell.exe -NoProfile -Command "$env:DOTNET_CLI_HOME='C:\dev\TriffView\.dotnet-home'; $env:NUGET_PACKAGES='C:\dev\TriffView\.nuget'; $env:APPDATA='C:\dev\TriffView\.appdata'; $env:NUGET_HTTP_CACHE_PATH='C:\dev\TriffView\.nuget-cache'; $env:DOTNET_CLI_TELEMETRY_OPTOUT='1'; & 'C:\dev\TriffView\.dotnet\dotnet.exe' test 'C:\dev\TriffView\.claude\worktrees\feat+persistent-preview-alerts\native\TriffView.Tests\TriffView.Tests.csproj' -c Release --filter \"FullyQualifiedName~PreviewAlertStatePhaseTests\""
```

Expected failure: a build error, not a test failure — `CS0117: 'PreviewAlertState' does not contain a definition for 'AlertProgress'`. `PreviewAlertState` exists (from the task that created `PreviewAlertState.cs`) but the method does not yet.

- [ ] **Step 2: Implement `AlertProgress` on `PreviewAlertState`**

In `native/TriffView/PreviewAlertState.cs`, add this as a `public static` method on the existing `PreviewAlertState` class (alongside `Active`/`Arm`/`ClearExpired`/`Acknowledge` — do not touch those):

```csharp
    // Pure phase maths shared by DrawAlertBorder. Non-persistent alerts clamp at 1 so the
    // wave finishes exactly at DurationMs and holds there; persistent alerts free-run so the
    // sine keeps pulsing at the same configured cadence indefinitely.
    public static double AlertProgress(DateTime startedUtc, DateTime now, int durationMs, bool persistent)
    {
        var elapsed = Math.Max(0, (now - startedUtc).TotalMilliseconds);
        var raw = elapsed / Math.Max(1, durationMs);
        return persistent ? raw : Math.Min(1, raw);
    }
```

Run the same test command as Step 1. Expected: all three tests pass, `PreviewAlertStatePhaseTests` shows 3/3.

- [ ] **Step 3: Wire `DrawAlertBorder` to the helper, unclamped for persistent alerts**

Replace `native/TriffView/TriffViewSubsystem.cs:3602-3616`:

```csharp
    private void DrawAlertBorder(Graphics graphics, Rectangle frame, ActivePreviewAlert alert)
    {
        var baseColor = ColorFromString(alert.Color, Color.FromArgb(255, 59, 59));
        var progress = PreviewAlertState.AlertProgress(alert.StartedUtc, DateTime.UtcNow, alert.DurationMs, alert.Persistent);
        var wave = (Math.Sin(progress * alert.PulseCount * Math.PI * 2) + 1) / 2;
        var alpha = (int)Math.Max(90, Math.Min(255, 110 + wave * 145));
        using var path = RoundedRect(frame, 6);
        using var pen = new Pen(Color.FromArgb(alpha, baseColor), Math.Max(1, alert.Thickness))
        {
            Alignment = PenAlignment.Inset,
            LineJoin = LineJoin.Round,
        };
        graphics.DrawPath(pen, path);
    }
```

This is the only change in the method: `Math.Min(1, elapsed / Math.Max(1, alert.DurationMs))` is replaced by a call that reproduces the identical clamped value when `alert.Persistent` is `false` (proven by Step 1's `PersistentAlert_BeforeDuration_MatchesNonPersistent` and by `AlertProgress` itself clamping in that branch) — non-persistent rendering is unchanged.

Build (no test changes here, this is rendering code exercised only on Windows):

```powershell
powershell.exe -NoProfile -Command "$env:DOTNET_CLI_HOME='C:\dev\TriffView\.dotnet-home'; $env:NUGET_PACKAGES='C:\dev\TriffView\.nuget'; $env:APPDATA='C:\dev\TriffView\.appdata'; $env:NUGET_HTTP_CACHE_PATH='C:\dev\TriffView\.nuget-cache'; $env:DOTNET_CLI_TELEMETRY_OPTOUT='1'; & 'C:\dev\TriffView\.dotnet\dotnet.exe' build 'C:\dev\TriffView\.claude\worktrees\feat+persistent-preview-alerts\native\TriffView.csproj' -c Release"
```

- [ ] **Step 4: Add the paint-suppression field to `TriffViewOverlayForm`**

Add a private field next to the other overlay-form fields (near `_alertTimer`, the same class that hosts `TickAlertFlashes` and `DrawAlertBorder`):

```csharp
    private bool _alertPaintSuppressed;
    private readonly List<Rectangle> _deferredAlertRects = new();
```

`_alertPaintSuppressed` tracks whether the previous tick was painted; `_deferredAlertRects`
holds dirty rects that fell due while the overlay was invisible, so they are flushed on the
way back instead of discarded. Without the second field, an alert that expires at Opacity 0
leaves its last-painted border on screen: the tick returns before invalidating, and if that
was the last active alert the timer then stops, so no later tick exists to fix it.

No test for this step alone; it is exercised by Step 5.

> **SUPERSEDED during implementation.** The `_deferredAlertRects` field above shipped, then
> was removed. What this step originally specified: accumulate dirty rects into a list while
> the overlay is at `Opacity <= 0`, and flush that list as bounded `Invalidate(rect)` calls
> "on resume" (see Step 5's original `resuming` branch below).
>
> **Why it was rejected (found Critical during implementation):** the plan's own resume
> detection lives entirely inside `TickAlertFlashes`, which only runs when `_alertTimer` is
> ticking. But the same tick that clears the *last* active alert while hidden also stops that
> timer (this file's existing `if (!... .Any(...)) _alertTimer.Stop();` line). So the one
> case this field exists for — an alert expiring while the overlay is hidden — stops the
> timer on the very tick that would have needed a later tick to observe "opacity came back"
> and flush `_deferredAlertRects`. No later tick ever runs, so the flush is unreachable and a
> stale border can survive on screen indefinitely once the overlay becomes visible again.
>
> **What shipped instead:** a single `bool _alertRepaintOwed` (declared next to
> `_alertPaintSuppressed`, no rect list) raised whenever a tick has dirty rects but is
> suppressed, and consumed as one full `Invalidate()` by whichever of `SetClients`,
> `MarkActiveClient`, or `SyncClientStates` is the call site that actually restores `Opacity`
> from 0 — those sites, not the timer, are the ones that know visibility just returned. See
> `TriffViewSubsystem.cs` near `_alertRepaintOwed`'s declaration and its three consume sites
> for the final version; the belt-and-braces `resuming` branch inside `TickAlertFlashes`
> stays only as a fallback for the (non-primary) case where the timer is still running when
> `Opacity` comes back.

- [ ] **Step 5: Rewrite `TickAlertFlashes` — bounded invalidation, opacity suppression, resume repaint**

Replace `native/TriffView/TriffViewSubsystem.cs:3618-3630`:

```csharp
    private void TickAlertFlashes()
    {
        var now = DateTime.UtcNow;

        // HideOnLostFocus parks the whole overlay at Opacity 0 (:2998, :3073) without hiding
        // it, so DWM still composites nothing visible. Alerts stay armed and keep expiring on
        // schedule underneath, but there is nothing on screen to repaint, so skip Invalidate
        // entirely while suppressed and catch up with one full repaint the moment we return.
        var paintSuppressed = Opacity <= 0;
        var resuming = _alertPaintSuppressed && !paintSuppressed;
        _alertPaintSuppressed = paintSuppressed;

        // The tick clears expired alerts before it repaints, so a preview whose alert just
        // expired must still be invalidated this tick even though state.Alerts.Active(now) is
        // now null for it - otherwise its last-painted border is never erased. Capture each
        // preview's dirty state (cleared-this-tick OR still-active) before moving on, and
        // invalidate that rect, not the whole form.
        //
        // Bounded Invalidate(rect) is safe on THIS form specifically: TriffViewOverlayForm's
        // CreateParams (TriffViewSubsystem.cs:2883-2892) sets only WS_EX_TOOLWINDOW |
        // WS_EX_NOACTIVATE | (optionally) WS_EX_TOPMOST - no WS_EX_LAYERED, no
        // TransparencyKey - and MarkActiveClient (:3054) and SyncClientStates (:3091) already
        // rely on bounded Invalidate(rect) on this same form today. The "partial invalidation
        // leaves ghosts" comment near :3971 is about TriffViewLabelOverlayForm, a DIFFERENT,
        // layered form (WS_EX_LAYERED + TransparencyKey, :3899-3910) where the compositor
        // needs the whole surface repainted. Do not apply that reasoning here.
        var dirty = new List<Rectangle>();
        foreach (var state in _previews.Values)
        {
            var cleared = state.Alerts.ClearExpired(now);

            // Acknowledgement. This line is the whole point of the feature: it is what stops a
            // persistent alert when its client becomes the selected one. It was introduced by
            // the arm-and-acknowledge task and MUST survive any future rewrite of this method -
            // an earlier draft of this plan rewrote the tick and silently dropped it, which
            // would have shipped a persistent alert that never clears.
            if (state.Client.Handle == _selectedHandle && state.Alerts.Acknowledge())
            {
                cleared = true;
            }

            var stillActive = state.Alerts.Active(now) != null;
            if (cleared || stillActive)
            {
                dirty.Add(state.FrameRect);
            }
        }

        // Rects that fell due while the overlay was invisible must not be thrown away - the
        // border is still in the form's backing store and nothing else repaints it on the way
        // back. Carry them until we are allowed to paint again.
        _deferredAlertRects.AddRange(dirty);

        if (!_previews.Values.Any(state => state.Alerts.Active(now) != null))
        {
            _alertTimer.Stop();
        }

        if (paintSuppressed) return;

        if (resuming)
        {
            // One full repaint on return covers every deferred rect at once, including any
            // whose preview has since moved or been destroyed.
            _deferredAlertRects.Clear();
            Invalidate();
            return;
        }

        foreach (var rect in _deferredAlertRects)
        {
            Invalidate(ToClientRect(rect));
        }

        _deferredAlertRects.Clear();
    }
```

> **SUPERSEDED during implementation** (same finding as the amendment on Step 4). The
> `TickAlertFlashes` body above — accumulating into `_deferredAlertRects` at line "Rects that
> fell due..." and flushing/clearing that list in both the `resuming` branch and the final
> `foreach` — is the rejected design. It reads as internally consistent, which is exactly the
> trap: the flush code is correct in isolation, it is just unreachable in the one scenario
> that matters (the last alert clearing while `Opacity <= 0`, which stops `_alertTimer` on
> that same tick and prevents any later tick from ever reaching the `resuming` branch to
> perform the flush). The shipped method instead has no rect list: it sets a single
> `_alertRepaintOwed = true` when `paintSuppressed && dirty.Count > 0`, and returns — leaving
> the actual repaint to whichever of `SetClients` / `MarkActiveClient` / `SyncClientStates`
> next restores `Opacity` from 0. Read `TickAlertFlashes` and those three call sites in
> `TriffViewSubsystem.cs` directly for the final, shipped form rather than transcribing this
> code block.

**Ownership note:** `UpdateAlertTimer` (`TriffViewSubsystem.cs:3632-3637`) is retargeted onto `state.Alerts.Active(...)` by **Task 1**, which owns every mechanical call-site rename in this file. By the time you reach this task it should already read the new form. Verify it does; if it still reads `state.ActiveAlert(...)`, that is a Task 1 gap — flag it rather than silently patching it here. For reference, the correct final form is:

```csharp
    private void UpdateAlertTimer()
    {
        var anyActive = _previews.Values.Any(state => state.Alerts.Active(DateTime.UtcNow) != null);
        if (anyActive && !_alertTimer.Enabled) _alertTimer.Start();
        if (!anyActive && _alertTimer.Enabled) _alertTimer.Stop();
    }
```

Also update the paint call site at `TriffViewSubsystem.cs:3590` (`state.ActiveAlert(DateTime.UtcNow)` -> `state.Alerts.Active(DateTime.UtcNow)`) for the same mechanical reason.

Build:

```powershell
powershell.exe -NoProfile -Command "$env:DOTNET_CLI_HOME='C:\dev\TriffView\.dotnet-home'; $env:NUGET_PACKAGES='C:\dev\TriffView\.nuget'; $env:APPDATA='C:\dev\TriffView\.appdata'; $env:NUGET_HTTP_CACHE_PATH='C:\dev\TriffView\.nuget-cache'; $env:DOTNET_CLI_TELEMETRY_OPTOUT='1'; & 'C:\dev\TriffView\.dotnet\dotnet.exe' build 'C:\dev\TriffView\.claude\worktrees\feat+persistent-preview-alerts\native\TriffView.csproj' -c Release"
```

- [ ] **Step 6: Run the full alert-state test suite**

```powershell
powershell.exe -NoProfile -Command "$env:DOTNET_CLI_HOME='C:\dev\TriffView\.dotnet-home'; $env:NUGET_PACKAGES='C:\dev\TriffView\.nuget'; $env:APPDATA='C:\dev\TriffView\.appdata'; $env:NUGET_HTTP_CACHE_PATH='C:\dev\TriffView\.nuget-cache'; $env:DOTNET_CLI_TELEMETRY_OPTOUT='1'; & 'C:\dev\TriffView\.dotnet\dotnet.exe' test 'C:\dev\TriffView\.claude\worktrees\feat+persistent-preview-alerts\native\TriffView.Tests\TriffView.Tests.csproj' -c Release --filter \"FullyQualifiedName~PreviewAlertState\""
```

Expected: all `PreviewAlertStateTests` (from the earlier task) plus the three new `PreviewAlertStatePhaseTests` pass, none regress.

**What is NOT covered by an automated test, and why:** the `dirty`-rect union built inside `TickAlertFlashes`, the `Opacity <= 0` suppression branch, and the `resuming` full-repaint branch cannot be unit tested — `TickAlertFlashes` is a private method on `TriffViewOverlayForm`, a live `Forms.Form` whose `_previews` values hold a real `DwmThumbnail` (a disposable wrapper over a DWM handle) and whose `Opacity`/`Invalidate` interact with the real window subsystem. There is no way to construct that state in xunit without a Windows message loop and a real window handle. The maths it depends on (`ClearExpired` returning `true` exactly once when an alert expires, `Active` returning `null` immediately after) is already covered by the earlier task's `PreviewAlertStateTests` per the design doc's Testing section ("clearing an alert reports its dirty rect, so the repaint that follows erases the border rather than leaving it painted"). What is genuinely new here — the union itself, the opacity gate, and the resume repaint — will be checked by hand on Windows per Step 7.

- [ ] **Step 7: Manual verification on Windows (record results, do not skip)**

Build with the explicit-SDK command from Global Constraints and launch the resulting exe. Do **not** run `scripts/build-native.ps1` from the main checkout to "save time" — it would build `fork/main`'s code, not this branch, and every observation below would be about the wrong binary. Task 5 Step 3 also has to be done first, or the app serves the "missing overlay" page instead of the real UI. With `Alerts.PersistUntilSelected` on, check:

1. Fire a persistent alert on a client that is not selected. Confirm the border keeps pulsing past its old `FlashDurationMs` cutoff instead of freezing solid.
2. While it is pulsing, select that client. Confirm the border's last frame is fully erased within one tick (no stale border remnant at the edge of the old rect) — this is the "cleared but not repainted" bug Step 5's dirty-rect union exists to prevent.
3. With `HideOnLostFocus` on, alt-tab away from EVE while an alert is armed. Confirm CPU/redraw activity for the overlay stops (use Task Manager or the `sample.ps1` harness per CLAUDE.md) while it is hidden, then alt-tab back and confirm the border reappears immediately and correctly, with no visible tearing from the forced full `Invalidate()`.
4. Repeat step 1 and 2 on a 100%-scale monitor (`DISPLAY2`/`DISPLAY3` per CLAUDE.md's display section), not only the 200% primary — this task moves no rectangles, but confirm the pulse itself renders correctly there too.
5. Run the measured performance gate from the design doc's Verification section (idle / one persistent alert / several persistent alerts, comparing CPU, GDI/USER handles, and `dwm.exe` `% Processor Time` via `Get-Counter`) before calling this task's cost claim (bounded invalidation vs. the old full-form `Invalidate()`) verified.

- [ ] **Step 8: Commit**

```powershell
git add native/TriffView/PreviewAlertState.cs native/TriffView/TriffViewSubsystem.cs native/TriffView.Tests/PreviewAlertStateTests.cs
git commit -m "Free-run persistent alert pulse and bound its repaint cost"
```
### Task 5: Verification gate

This task writes no production code. It is the gate the spec requires before the change can be called done, and it exists as its own task because a reviewer can legitimately reject it while approving Tasks 1–4.

**Files:**
- Modify: `docs/superpowers/specs/2026-08-17-persistent-preview-alerts-design.md` (record measured numbers only)

**Interfaces:**
- Consumes: the complete feature from Tasks 1–4.
- Produces: measured evidence. Nothing depends on this task in code.

- [ ] **Step 1: Full build with warnings as errors**

Upstream CI builds with `--warnaserror`, so a nullable or unused-using warning introduced by the new file is a real defect even though this branch is fork-only.

```powershell
powershell.exe -NoProfile -Command "$env:DOTNET_CLI_HOME='C:\dev\TriffView\.dotnet-home'; $env:NUGET_PACKAGES='C:\dev\TriffView\.nuget'; $env:APPDATA='C:\dev\TriffView\.appdata'; $env:NUGET_HTTP_CACHE_PATH='C:\dev\TriffView\.nuget-cache'; $env:DOTNET_CLI_TELEMETRY_OPTOUT='1'; & 'C:\dev\TriffView\.dotnet\dotnet.exe' build 'C:\dev\TriffView\.claude\worktrees\feat+persistent-preview-alerts\native\TriffView.csproj' -c Release -warnaserror"
```

Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`. Paste the actual tail of the output into the PR description — do not assert success from memory.

- [ ] **Step 2: Full test suite, both projects**

```powershell
powershell.exe -NoProfile -Command "$env:DOTNET_CLI_HOME='C:\dev\TriffView\.dotnet-home'; $env:NUGET_PACKAGES='C:\dev\TriffView\.nuget'; $env:APPDATA='C:\dev\TriffView\.appdata'; $env:NUGET_HTTP_CACHE_PATH='C:\dev\TriffView\.nuget-cache'; $env:DOTNET_CLI_TELEMETRY_OPTOUT='1'; & 'C:\dev\TriffView\.dotnet\dotnet.exe' test 'C:\dev\TriffView\.claude\worktrees\feat+persistent-preview-alerts\native\TriffView.Tests\TriffView.Tests.csproj' -c Release"
```

```powershell
powershell.exe -NoProfile -Command "$env:DOTNET_CLI_HOME='C:\dev\TriffView\.dotnet-home'; $env:NUGET_PACKAGES='C:\dev\TriffView\.nuget'; $env:APPDATA='C:\dev\TriffView\.appdata'; $env:NUGET_HTTP_CACHE_PATH='C:\dev\TriffView\.nuget-cache'; $env:DOTNET_CLI_TELEMETRY_OPTOUT='1'; & 'C:\dev\TriffView\.dotnet\dotnet.exe' test 'C:\dev\TriffView\.claude\worktrees\feat+persistent-preview-alerts\tests\TriffView.Tests\TriffView.Tests.csproj' -c Release"
```

Expected: both pass, with no test count *lower* than before the branch. Record both counts.

- [ ] **Step 3: Get a real UI bundle into the worktree**

A worktree has no `native/Assets/overlay-dist.zip`. The native project builds fine without it because the `EmbeddedResource` is conditional, but the app then serves the "missing overlay" page instead of the real settings UI — which invalidates every runtime observation below, including the toggle itself.

**Where you put the exe matters as much as how you build it.** `FindLooseOverlayDistFolder`
(`native/MainWindow.xaml.cs:497-508`) walks **upward** from the exe's own directory looking for
`app/dist/index.html`, and a loose folder it finds **takes precedence over the embedded bundle**
(`:494`). So an exe placed anywhere at or under a checkout that has a built `app/dist` will silently
load *that* UI instead of the one you just embedded.

This failure is worse than the missing-bundle case because it is silent: a missing bundle shows an
obvious "missing overlay" page, whereas a stale one shows a perfectly working app that simply does
not have your feature in it — which reads as "the change did nothing." This actually happened during
this branch's first test build: the exe was copied to the main checkout root, whose `app/dist` was
weeks old, and the new toggle was absent from the settings panel entirely.

Run the exe from a directory with **no** `app/dist` in any ancestor — e.g. a scratch folder like
`C:\dev\triffview-alerts-test\` — and verify the ancestors first:

```bash
ls /mnt/c/dev/app/dist/index.html /mnt/c/app/dist/index.html 2>/dev/null || echo "clean"
```

Then confirm positively that the UI you loaded is the right one: open the alerts settings and check
the new toggle is visible. If it is not, stop — nothing downstream is meaningful.

Also **quit any running TriffView first, including from the system tray.** The app is single-instance
via a named mutex (`native/App.xaml.cs:27`); a second copy shows "TriffView is already running" and
exits, leaving you observing the old build.

**Do not copy the zip from the main checkout.** That zip is built from `fork/main`'s UI,
which has no persist toggle — every UI-dependent check below would then run against a build
that cannot show the feature. The bundle must be built from *this worktree's* `app/`.

Build the web UI in WSL (npm is not on the Windows PATH here), zip `app/dist` the way
`scripts/publish-release.ps1:236-245` does, and place it where the `EmbeddedResource` at
`native/TriffView.csproj:37-41` expects it:

```bash
cd /mnt/c/dev/TriffView/.claude/worktrees/feat+persistent-preview-alerts/app
npm install
npm run build
```

```bash
cd /mnt/c/dev/TriffView/.claude/worktrees/feat+persistent-preview-alerts
rm -f native/Assets/overlay-dist.zip
cd app/dist && zip -r ../../native/Assets/overlay-dist.zip . && cd ../..
```

Then rebuild native — embedding is evaluated at build time, so a zip dropped in after the
last build is not in the DLL:

```powershell
powershell.exe -NoProfile -Command "$env:DOTNET_CLI_HOME='C:\dev\TriffView\.dotnet-home'; $env:NUGET_PACKAGES='C:\dev\TriffView\.nuget'; $env:APPDATA='C:\dev\TriffView\.appdata'; $env:NUGET_HTTP_CACHE_PATH='C:\dev\TriffView\.nuget-cache'; $env:DOTNET_CLI_TELEMETRY_OPTOUT='1'; & 'C:\dev\TriffView\.dotnet\dotnet.exe' build 'C:\dev\TriffView\.claude\worktrees\feat+persistent-preview-alerts\native\TriffView.csproj' -c Release"
```

Confirm it actually embedded — the DLL should roughly double in size, and:

```powershell
powershell.exe -NoProfile -Command "[Reflection.Assembly]::LoadFrom('C:\dev\TriffView\.claude\worktrees\feat+persistent-preview-alerts\native\bin\Release\net8.0-windows\TriffView.dll').GetManifestResourceNames()"
```

Expected: the list contains `TriffView.Assets.overlay-dist.zip`, and the DLL is roughly
double its pre-embed size. Then confirm the bundle is the RIGHT one, not merely present:
launch the app, open the alerts settings, and check the new toggle is visible. If it is not,
you embedded a stale bundle and every remaining step in this task is invalid.

- [ ] **Step 4: Exercise the behaviour by hand on Windows**

Everything about the on-screen blink is a claim until this step is done. Run the app with at least two EVE clients and walk each case, recording pass/fail:

1. Toggle off (default): an alert flashes for its configured duration and stops. Unchanged from today.
2. Toggle on, alert fires for a non-foreground client: the border pulses and keeps pulsing well past `FlashDurationMs`.
3. Select that client: the pulsing stops within ~80 ms and **no border remains painted**. This is the stale-border case from the Codex review — look specifically for a frozen border, not just for the animation stopping.
4. Toggle on, alert fires for the client already in the foreground: it flashes for its duration and stops, and does not re-arm when you switch away.
5. Alert fires while you are outside EVE entirely (Discord/browser): it arms and persists; switching to a *different* client does not clear it.
6. A lower-severity alert arriving after a persistent alert's nominal duration does not change the border colour.
7. With `HideOnLostFocus` on: leave EVE, return, and confirm the persistent border is painted correctly on the first frame back rather than missing or stale.
8. Toggle on, press the settings panel's **Test** button for an event type: the test alert persists exactly like a real one. This needs no code of its own — `TestAlert` routes through `_alerts.TestAlert` into the same `OnAlertTriggered` -> `ProcessPendingAlerts` path — but the spec calls for it explicitly, so confirm it rather than assuming it. Note that `TestAlert` defaults its target to `_clients.FirstOrDefault()`, so if that happens to be the foreground client you will correctly see case 4's behaviour instead.
9. Confirm sound and tray notification fire **once** for a persistent alert, not repeatedly. Persistence is visual only; a repeating siren would mean the persistence flag leaked into the wrong path.

- [ ] **Step 5: Repeat step 4 cases 2, 3 and 7 on a 100% monitor**

Not the 200% primary. This machine is mixed-DPI and TriffView is system-DPI-aware; a prototype that produced 960 clean measurements on one monitor at one scale factor was presented as validating an approach generally, and was wrong. Rendering changes get checked on both.

- [ ] **Step 6: Measure the permanently-armed timer**

This change can leave the 80 ms overlay timer (`TriffViewSubsystem.cs:2842`) armed indefinitely, which no previous version allowed. Use the external harness at `C:\dev\triffview-perf\` (`sample.ps1`); if it is gone, rebuild it from CLAUDE.md's "Measuring performance and resource use" section.

Sample three conditions for at least 5 minutes each: idle with no alerts, one persistent alert, and several persistent alerts across different previews. Compare TriffView CPU time, private bytes, GDI and USER handle counts, and `dwm.exe` CPU against the idle baseline.

Use the counter, not the process property, for DWM — `Process.TotalProcessorTime` silently reads exactly 0 for `dwm.exe` because it runs as a different account:

```powershell
Get-Counter '\Process(dwm)\% Processor Time'
```

Also confirm the sampler itself is DPI-aware (`SetProcessDpiAwarenessContext(-4)`) before it touches any screen API, or every coordinate it reports is wrong.

Acceptance: no unbounded growth in GDI or USER handles across the sampling window, and steady-state CPU with several persistent alerts within a margin you are willing to defend in the PR. If it is not, the bounded-invalidation work in Task 4 did not do its job — investigate before shipping rather than lowering the bar.

The mixed-DPI arrangement is deliberately **not** part of this performance gate. That trap governs geometry and placement, and this change moves no rectangles. Step 5 covers the 100% monitor for rendering reasons.

- [ ] **Step 7: Record the numbers and commit**

Append the measured results to the spec's Verification section, replacing nothing — the spec states what must be measured, this records what was. State plainly which checks were run and which were not.

```bash
git add docs/superpowers/specs/2026-08-17-persistent-preview-alerts-design.md
git commit -m "Record verification results for persistent preview alerts"
```
