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
- **Severity.** Unchanged. A higher-severity alert replaces a lower one; a
  lower-severity alert arriving during an active alert extends its expiry rather
  than downgrading its colour, which is a no-op for a persistent alert.
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

1. Invalidate only the rects of previews that currently hold an alert, as
   `:3054` and `:3091` already do. This is safe **on this form specifically**:
   the preview overlay is not layered (`CreateParams` at `:2883` sets only
   `WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST`, with no
   `TransparencyKey`). The "partial invalidation leaves ghosts" finding recorded
   at `:3971` applies to the *label* overlay form, which is layered
   (`:3899-3907`). Do not generalise that comment to this form.
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

Tests go in `native/TriffView.Tests` (`net8.0-windows`, `ProjectReference` to
`TriffView.csproj`, with `<InternalsVisibleTo Include="TriffView.Tests" />` at
`native/TriffView.csproj:31`). `ActivePreviewAlert` and `TriffViewPreviewAlert`
are already top-level `internal` types and are reachable from there directly.

No source file is extracted and no `Compile Include` line is added. **CLAUDE.md's
Testing section is out of date on this point** — it describes only the
linked-source `tests/TriffView.Tests` project and states that logic must be
Windows-free to be testable. That is no longer the whole picture, and the doc
should be corrected separately from this change.

Cases:

- a persistent alert survives past `FlashDurationMs`;
- it clears when its client becomes the selected handle;
- it does not arm when the target is already the selected handle;
- `selectedHandle == nint.Zero` (outside EVE) does not clear anything;
- a severity upgrade preserves persistence;
- with the toggle off, arming and expiry are byte-for-byte today's behaviour.

`PreviewState` is a private nested class inside `TriffViewOverlayForm` and stays
untestable; the logic worth covering lives on the two internal alert types.

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
