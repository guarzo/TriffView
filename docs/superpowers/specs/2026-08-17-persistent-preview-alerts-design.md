# Persistent preview alerts

Date: 2026-08-17
Status: approved design, not yet implemented
Branch base: `fork/main` @ `b57e400` (fork-only feature; not an upstream PR candidate)

## Problem

A preview alert flashes for `FlashDurationMs` and stops. If you are looking at
another client, or at another application entirely, an alert can come and go
without ever being seen. There is no record afterwards that anything happened on
that character.

## Intended outcome

A single global setting makes an alert keep pulsing until you actually select
the character it fired for. Off by default, so existing installs behave exactly
as they do today.

## Behaviour

`Alerts.PersistUntilSelected` (bool, default `false`).

When enabled, a flash alert for character X keeps pulsing at its configured
cadence until X's EVE client is the real foreground window.

- **Already selected.** If X's client is the real foreground window at the
  moment the alert fires, the alert flashes for its configured duration and
  stops. It does not arm, and it does not re-arm when you later switch away.
  You were looking at it; it counts as seen.
- **Outside EVE.** When no EVE client holds the foreground, nothing counts as
  selected. Alerts arm and persist. This deliberately differs from
  `_activeClientHandle`, which keeps the last-used client marked active while
  you are in Discord or a browser (`TriffViewSubsystem.cs:508-521`).
- **Severity.** Unchanged in effect, but the rule has to be stated in terms of
  *logical* activity rather than expiry. A higher-severity alert replaces a lower
  one; a lower-severity alert arriving while an alert is still logically active
  extends its expiry rather than downgrading its colour, which is a no-op for a
  persistent alert. A persistent alert is logically active indefinitely,
  including after its nominal `ExpiresUtc` has passed. When a replacement does
  happen, the new alert recomputes `Persistent` from the toggle and the current
  `selectedHandle`; persistence is never inherited from the alert it replaced.
- **Cadence.** Unchanged, and driven by the same two settings. The existing wave
  is `sin(progress * PulseCount * 2*pi)` with `progress = elapsed /
  FlashDurationMs`, so one pulse takes `FlashDurationMs / FlashPulseCount` ms. A
  persistent alert simply keeps running that same wave. No new tuning knob.
- **Test alerts persist too.** The settings-panel Test button goes through the
  same path and produces a persistent alert when the toggle is on, so the
  setting is exercisable from the UI. Clearing it means selecting that client.
- **Sound and tray notification are unaffected.** Both remain one-shot per
  alert. Persistence is visual only; there is no repeating siren.

## Mechanism

### The acknowledgement signal

The overlay needs an unambiguous "which client is really selected" value. It
does not have one today: `_foreground` is assigned the real foreground in
`SyncClientStates` but `activeHandle` in `MarkActiveClient`
(`TriffViewSubsystem.cs:3030`).

Introduce `selectedHandle`: the real foreground window handle if it belongs to
an EVE client, otherwise `nint.Zero`. It is computed in the subsystem, where
`foregroundIsEve` is already known (`:606-610`) and where the refresh path
already holds `foreground` (`:508`), and flowed into the overlay. It is
deliberately **not** `_activeClientHandle`.

### Alert state

- `TriffViewPreviewAlert` (`:2806`) gains `bool Persistent`, set in
  `ProcessPendingAlerts` (`:1190`) from `Settings.Alerts.PersistUntilSelected`.
- `PreviewState.SetAlert` (`:3824`) resolves
  `Persistent = requested && Client.Handle != selectedHandle`. The
  already-selected exception lives next to the state that knows the answer.
- `ActivePreviewAlert` (`:2814`) gains `Persistent`. `ActiveAlert(now)` returns
  the alert when `Persistent || ExpiresUtc > now`; `ClearExpiredAlert` must not
  clear a persistent alert.
- **The severity guard must become persistence-aware as well.** It currently
  reads `Alert.ExpiresUtc > now && Alert.SeverityRank > alert.SeverityRank`
  (`:3824`). Making only `ActiveAlert` and `ClearExpiredAlert` persistence-aware
  would leave a persistent alert past its nominal `ExpiresUtc` failing that first
  clause, so a *lower*-severity event would replace and downgrade it. All three
  call sites must share one "is this alert logically active" predicate.

### Rendering

`DrawAlertBorder` (`:3607`) skips the `Math.Min(1, ...)` clamp on `progress`
for persistent alerts. Because the wave is a sine of `progress`, unclamped
progress free-runs at exactly the configured pulse period. Non-persistent
rendering is unchanged.

### Timer and repaint cost

`TickAlertFlashes` (`:3618`) today calls a bare `Invalidate()`, repainting the
entire desktop-spanning overlay form. Today that is bounded by
`FlashDurationMs`; an indefinite blink would make it permanent at 12.5 Hz.

Two changes:

1. Invalidate a bounded set of rects instead of the whole form, as `:3054` and
   `:3091` already do. **That set is the union of (a) previews whose alert was
   cleared during this tick and (b) previews that still hold an alert.**
   `TickAlertFlashes` clears expired alerts *before* it repaints (`:3620-3625`),
   so invalidating only the previews that *currently* hold an alert would skip
   exactly the preview that just cleared and leave its last-painted border on
   screen. Capture each alert's dirty rect before clearing it. The existing bare
   `Invalidate()` is load-bearing for correctness here, not merely lazy.
   Bounded invalidation is safe **on this form**, but not because it is
   unlayered - it may in fact be layered: WinForms' `Opacity` setter turns on
   `AllowTransparency` for any value below 1, and `CreateParams` (`:2883`) then
   adds `WS_EX_LAYERED` on top of the `WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE |
   WS_EX_TOPMOST` it always sets. The profile's `Opacity` is user-configurable
   from 0.2-1.0 and `HideOnLostFocus` drives it to 0, so below 1 this form *is*
   `WS_EX_LAYERED` - just alpha-layered (`LWA_ALPHA`), not colour-keyed. The
   "partial invalidation leaves ghosts" finding recorded at `:3971` concerns a
   *different* layering mechanism: the label overlay form is layered via
   `WS_EX_LAYERED` + `TransparencyKey` (`LWA_COLORKEY`, `:3899-3907`). That
   failure mode has not been observed on the alpha-layered case, and
   `MarkActiveClient` (`:3054`) and `SyncClientStates` (`:3091`) already rely on
   bounded `Invalidate(rect)` on this same form today at whatever opacity the
   user has configured, without reported ghosting. Not yet verified on hardware
   specifically for alpha layering below 1.0.
2. Skip invalidation entirely while `HideOnLostFocus` has the form at
   `Opacity = 0` (`:2998`, `:3073`), and repaint once on return. The alert stays
   armed throughout; only the painting pauses.

`TickAlertFlashes` also clears any persistent alert on the preview matching
`selectedHandle`, which gives an acknowledgement latency of at most 80 ms.

## Settings and the native/web boundary

Native (`native/TriffAlerts/TriffAlertsService.cs`):

- `TriffAlertsSettings.PersistUntilSelected` (bool, default `false`), threaded
  through `Normalize()` and `ToState()`.
- A `case "persistUntilSelected"` in the `triffalerts:update-settings` switch,
  beside `pveMode` (`TriffViewSubsystem.cs:~1089`).

Web (`app/src/tools/TriffViewSettings.jsx`):

- A default in `DEFAULT_ALERTS` (`:105`), a coercion in the alerts normalizer
  (`:~526`), and a `Toggle` in the alerts panel next to the PvP toggle (`:1729`).

No new message type, so nothing new can collide in the first-handler-wins
dispatch chain.

### Compatibility

No migration. `DefaultsVersion` and `MigrateFromLegacyDefaults` are per-event
concerns and are untouched. A settings file written by an older build simply
lacks the key and deserializes to `false`, which is the existing behaviour. A
file written by this build and read by an older build has its unknown key
ignored by `System.Text.Json`, so downgrade is safe. The web patch protocol is
per-key on both sides, so unknown keys are ignored in both directions.

## Testing

The alert lifetime logic is **not** testable where it currently lives.
`ActivePreviewAlert` (`:2814-2834`) is a pure data holder — a constructor and
seven properties, no methods — and every behaviour worth covering (`SetAlert`,
`ActiveAlert`, `ClearExpiredAlert`, and the severity guard) sits on
`PreviewState`, a **private nested class** inside `TriffViewOverlayForm`
(`:3808`, `:3824`, `:3843`). Internals being reachable from the test project is
not the same thing as this logic being reachable.

So the state machine moves onto an internal top-level type: arm, expire,
acknowledge, and severity-replace become methods there, and `PreviewState`
keeps only a reference to it alongside its own window and geometry concerns.
This is the smallest change that makes the behaviour testable at all, and it
shrinks a file that is already oversized.

Tests go in `native/TriffView.Tests` (`net8.0-windows`, `ProjectReference` to
`TriffView.csproj`, with `<InternalsVisibleTo Include="TriffView.Tests" />` at
`native/TriffView.csproj:31`), so the extracted type carries no Windows-free
constraint and needs no `Compile Include` line. **CLAUDE.md's Testing section is
out of date on this point** — it describes only the
linked-source `tests/TriffView.Tests` project and states that logic must be
Windows-free to be testable. That is no longer the whole picture, and the doc
should be corrected separately from this change.

Cases:

- a persistent alert survives past `FlashDurationMs`;
- it clears when its client becomes the selected handle;
- it does not arm when the target is already the selected handle;
- `selectedHandle == nint.Zero` (outside EVE) does not clear anything;
- a lower-severity alert arriving *after* a persistent alert's nominal
  `ExpiresUtc` neither replaces nor downgrades it;
- a replacing higher-severity alert recomputes persistence from the toggle and
  the current `selectedHandle` rather than inheriting it;
- clearing an alert reports its dirty rect, so the repaint that follows erases
  the border rather than leaving it painted;
- with the toggle off, arming and expiry are byte-for-byte today's behaviour.

`PreviewState` itself stays a private nested class and stays untestable. It
keeps only window and geometry concerns; the alert behaviour under test lives
entirely on the extracted type.

## Accepted limitations

- With `HideActivePreview` on, the selected client has no preview at all, so its
  alert state is disposed on selection. Correct outcome by a different route.
- A `PreviewState` is recreated when its `Identity` changes (`:2981`), for
  example character-select to named client. A persistent alert is lost then.
- With `HideOnLostFocus` on, a persistent blink is not visible until you return
  to EVE.

## Out of scope

Per-event-type control over persistence, a severity threshold, a maximum
persistence cap, an alternate slow cadence, and any repeating sound. The setting
is one global boolean.

## Verification

Windows-only, via the explicit-SDK invocation documented in CLAUDE.md for
worktrees. Build plus the `native/TriffView.Tests` suite. Anything about the
on-screen blink is a claim until it has been exercised on Windows, including on
a 100% monitor rather than only the 200% primary.

**A measured performance gate is required, not optional.** This change can leave
the 80 ms overlay timer (`:2842`) armed indefinitely, which no previous version
of this code allowed. Before the change is called done, sample TriffView with
the external harness at `C:\dev\triffview-perf\` (`sample.ps1`) under three
conditions — idle, one persistent alert, and several persistent alerts — and
compare CPU time, GDI/USER handles, and `dwm.exe` CPU against the idle baseline.
Use `Get-Counter '\Process(dwm)\% Processor Time'` for DWM; `TotalProcessorTime`
silently reads zero for it. If the harness is gone, rebuild it from CLAUDE.md's
"Measuring performance and resource use" section.

The mixed-DPI arrangement is deliberately *not* part of the performance gate.
That trap governs geometry and placement, and this change moves no rectangles.
Testing on a 100% monitor is still required, for rendering rather than cost.
