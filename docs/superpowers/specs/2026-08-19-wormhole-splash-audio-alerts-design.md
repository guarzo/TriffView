# Wormhole Splash Audio Alerts — Design

**Date:** 2026-08-19
**Status:** Approved for implementation
**Branch:** `worktree-splash-audio-alerts` (from `fork/main`) — fork-only, not for upstream

## Goal

Alert the user when a wormhole is activated ("splashed") near one of their EVE
clients, by capturing each client's audio and detecting the wormhole activation
sound. The motivating case is sitting idle with several clients open: a splash
happens on an alt the user is not looking at, and nothing in EVE's log files
records it.

## Why audio

Neither of the two obvious sources exists. EVE writes nothing to Gamelogs or
Chatlogs when a wormhole is activated, and ESI exposes no grid contents. The
event is only ever rendered to the user as sound and as an overview change.
Reading the overview was investigated and rejected (see "Rejected: pixel
detection"). Audio is the remaining channel, and CCP deliberately exempts
wormhole sounds from the attenuation applied to unfocused clients — confirmed by
the user — which is what makes this possible at all.

## Measured results from the spike

All numbers below were measured on real recordings from the user's machine
(Windows 11 24H2, build 26100), not inferred.

| Property | Measured |
|---|---|
| Per-process capture, 6 concurrent clients | 2.73% of one core, ~0.5%/client, 12 MB |
| Packet granularity | ~10 ms |
| Detection, held-out session | **P=0.84, R=1.00, F1=0.913** at threshold 0.35 |
| Warp rejection (7 confirmed instances) | max score 0.19 |
| Session with no splashes (35 s) | 0 false positives |

The held-out session was recorded without marking and contributed nothing to the
templates. Ground truth for all sessions was established by the user listening to
extracted clips.

### Findings that shaped the design

- **The splash is a sustained sound of over a second**, not a transient. Onset
  detection finds it at most 1 time in 8; a 2-second analysis window is required.
- **Warp is the dominant confounder** and is cleanly rejected (0.19 vs 0.35
  threshold). Two earlier "signals" turned out to be warp and weapons.
- **All discriminative content is below ~6 kHz.** Accuracy is identical at 48,
  24, 16 and 12 kHz, so the pipeline runs at 16 kHz — 3× less DSP and 3× smaller
  templates than 48 kHz.
- **Normalisation must use a rolling context, not the whole recording.** Every
  early evaluation used whole-recording band statistics, which a live detector
  cannot reproduce. Switching to a trailing 30 s context both makes it
  live-computable and improves accuracy (F1 0.878 → 0.913).
- **A muted client yields digital silence** with `AUDCLNT_BUFFERFLAGS_SILENT`
  never set — perfectly healthy-looking zero-filled buffers, indistinguishable
  from "nothing happened". Observed for real: one client in a user recording was
  0% non-zero.

## Architecture

New subsystem `native/TriffAudio/`, following the existing subsystem convention
(own folder, own service, controller-owned).

```
EveWindowTracker ──(pid, characterName)──> TriffAudioService
                                                │
                          WasapiProcessCapture ─┤ one per EVE process
                          (Windows-only interop)│ 16 kHz mono, 30 s ring buffer
                                                │
                                    SplashDetector (PURE)
                                                │ score >= threshold
                                                ▼
                        TriffAlertsService.RaiseExternalAlert(...)
                                                │
                        existing cooldown / history / flash / tray path
```

### Files

| File | Responsibility | Testable |
|---|---|---|
| `native/TriffAudio/SplashDetector.cs` | Pure DSP: samples → score. No Windows deps. | `tests/TriffView.Tests` (cross-platform) |
| `native/TriffAudio/SplashTemplate.cs` | Template model + WAV/JSON load & save. | `tests/TriffView.Tests` |
| `native/TriffAudio/WasapiProcessCapture.cs` | Per-PID loopback interop. Windows-only. | manual only |
| `native/TriffAudio/TriffAudioService.cs` | Capture lifecycle, ring buffers, detection loop, health. | `native/TriffView.Tests` |
| `native/TriffAudio/Assets/splash-templates/` | 11 shipped templates (702 KB). | — |

`SplashDetector` and `SplashTemplate` must avoid `System.Windows.Forms`, WPF,
P/Invoke, and anything from `TriffViewSubsystem.cs`, so they can be linked into
the cross-platform test project with a `<Compile Include>` line.

## Detection algorithm

Fixed by measurement. Changing any value invalidates the shipped templates.

```
sample rate      16000 Hz mono          (input resampled from the capture format)
STFT             nperseg 1024, hop 85   (~5.3 ms frames, 15.6 Hz bins)
bands            32, geomspace(80 Hz, 7600 Hz)
band value       20*log10(mean magnitude in band + 1e-10)
context          trailing 30 s: per-band median and MAD, MAD floored at 1e-3
window           2.0 s = 376 frames
patch            (bands - median) / mad, flattened, mean-subtracted, L2-normalised
score            max dot product over all templates
threshold        0.35 (default, user-adjustable)
evaluation rate  4 Hz (every 250 ms)
```

The MAD floor is not optional: without it, bands containing no FFT bins produce a
zero MAD and the whole patch becomes NaN.

### Template format

A template is a 2-second sound **plus the context statistics it was captured
with** — a bare clip cannot be placed in the same feature space as a live window.

```
splash-NN.wav    16 kHz mono 16-bit PCM, 2.0 s, peak-normalised to 0.95
splash-NN.json   {"version":1,"sampleRate":16000,"gain":G,"median":[32],"mad":[32]}
```

`gain` is the scale factor applied when writing the WAV (clips are peak-normalised
so quiet ones survive 16-bit quantisation). **Readers must divide the samples by
`gain` before feature extraction.** Skipping this was measured to destroy
separation entirely — quiet negatives get amplified ~200x and read as large
deviations from their own context median, scoring as high as real splashes.

Shipped templates are embedded (`EmbeddedResource`, matching the existing
`overlay-dist.zip` pattern). User templates live in
`%APPDATA%\TriffHud\SplashTemplates\` and are loaded alongside them. A template
whose `version` is unrecognised is skipped with a diagnostics line, never a crash.

## Alert integration

Splash detections go through the existing alert path rather than a parallel one.
`TriffAlertsService` gains one public entry point:

```csharp
public void RaiseExternalAlert(string type, string characterName, string source, string message);
```

It applies the same `Enabled`/`config.Enabled` gate, the same
`characterName|type` cooldown, appends to the same history, and raises
`AlertTriggered` off-lock — reusing `EmitLocked` rather than duplicating it.

New event type `wormhole_splash`, added to `CreateDefaultEvents()` and to the
three JS constants (`ALERT_EVENT_DEFS`, `ALERT_EVENT_DEFAULTS`,
`DEFAULT_ALERT_EVENTS`). Defaults: enabled, severity `warning`, cooldown 15 s
(the detector can fire on consecutive windows of one sound), flash on, tray on.

The type string is permanent: `Normalize()` never prunes unknown `Events` keys,
so a renamed type would leave residue in every user's settings file forever.

`CombatLogExport.DetectLastFight` filters on a `FightAlertTypes` allowlist, so
splash alerts will not pollute combat log detection. No change needed.

## Character attribution

Capture is keyed by PID; alerts are keyed by character name. `EveClientWindow`
carries both, so `TriffAudioService` is given `(pid, characterName)` pairs on
every client refresh and resolves the name at detection time.

A client at the character-select screen has no character name (`StableKey` falls
back to handle hex). **Rule: clients with no character name are captured but
never alerted** — an alert keyed by an empty name would collide in the cooldown
map and cannot be matched to a preview.

## Audio health

`TriffAudioService` tracks per-client status and the controller projects it into
`triffview:state` on each client entry as `audioStatus`:

| Status | Meaning |
|---|---|
| `monitoring` | capture active, non-zero audio seen within the last 60 s |
| `silent` | capture active, only zeros for 60 s — muted in the mixer, in-game volume at zero, or the client genuinely produced nothing |
| `unavailable` | activation failed, or the process has no render session |
| `off` | the feature is disabled |

Shown in the existing Clients panel. This is not decoration: a muted client can
never alert and nothing else in the system would reveal it.

## Native alert sound

Alert sound moves from the web UI into the WPF app for **all** alert types.
Today `App.jsx` plays sound by diffing `alertHistory[0].id` out of
`triffview:state`, so nothing sounds when the settings window is closed — which
is precisely the situation this feature targets. The web-side playback is
removed to avoid double-play.

It hangs off a **new** `AlertSoundRequested` event with its own gate on
`config.Sound != "none"`, not off the existing `AlertNotificationRequested`.
That callback is raised only when `config.TrayNotification` is enabled
(`TriffViewSubsystem.cs:1216-1218`), while web playback today is independent of
tray configuration — reusing it would silently remove sound for any alert with
sound on and tray off.

Playback uses WPF's `MediaPlayer` (which exposes `Volume`, unlike
`System.Media.SoundPlayer`) against the sound assets converted from `.ogg` to
16-bit PCM WAV and added as `<Resource>`. It is fire-and-forget: the raising
path runs on the WPF dispatcher and must never block on audio.

This is a deliberate behaviour change for existing combat alerts; the user
approved it.

## Template capture UI

Users must be able to add templates without touching the filesystem. The flow
reuses the detector to do the hard part:

1. Alerts panel gains a **Splash templates** section with a client picker and a
   **Capture recent splash** button.
2. Native takes the last 30 s from that client's ring buffer, scores every 2 s
   window, and returns the **three best-separated candidates** as playable WAV
   (base64 in the message payload; 2 s of 16 kHz mono 16-bit is ~64 KB each).
3. The user auditions each and saves the correct one, or discards all three.
4. Saved templates are listed with play and delete.

Candidates are ranked but **not** thresholded, so a splash the detector currently
misses is still offered — that is the point of letting users add templates.

Saving writes the WAV plus the context statistics computed from the same ring
buffer, so a user template is exactly as valid as a shipped one.

### New web message types

Must stay unique across subsystems (first handler wins in the dispatch chain).

| Direction | Type | Payload |
|---|---|---|
| web→native | `triffaudio:capture` | `{ clientKey }` |
| native→web | `triffaudio:capture-result` | `{ clientKey, candidates: [{ id, score, wavBase64 }] }` |
| web→native | `triffaudio:save-template` | `{ candidateId, name }` |
| web→native | `triffaudio:delete-template` | `{ templateId }` |
| web→native | `triffaudio:list-templates` | `{}` |
| native→web | `triffaudio:templates` | `{ templates: [{ id, name, builtIn }] }` |

## Settings

One new section on `TriffAlertsSettings` (global, not per-profile — matching the
rest of the alerts settings):

```csharp
public bool SplashDetectionEnabled { get; set; }       // default false
public double SplashThreshold { get; set; }            // default 0.35, clamped 0.10-0.90
```

Clamping goes in `TriffAlertsSettings.Normalize()`, per the project rule that
invariants live in `Normalize()` rather than at call sites. Both need a `case` in
`ApplyAlertsPatch` — a new field with no case silently no-ops.

## Warm-up

Scores are only meaningful once the rolling context is populated. Measured: with
only 3 s of context, splash scores fall to 0.22-0.24 while non-splashes reach
0.24 — the classes stop separating entirely.

**Rule: no alert is raised for a client until its ring buffer holds at least 30 s
of audio.** Status stays `monitoring` during warm-up; the user is not told the
detector is warming up, because on a client that has just appeared there is
nothing actionable to report. A client whose buffer is reset (stream death and
re-activation) starts its warm-up again.

## Concurrency

One capture thread per client (event-driven, ~10 ms wakeups) and one detection timer
at 4 Hz. **This section originally specified a lock-free ring buffer with detection
never running on the capture thread; the Task 8 redesign deliberately reversed both
halves and this describes what was built.** Scoring a whole 30 s ring once per tick
turned out to mean ~5,600 FFTs per client per tick, so the per-hop FFT work moved
into the capture callback (`RollingBandBuffer.Append`), leaving the tick to build and
score one patch from already-computed bands. Both buffers (`AudioRingBuffer`,
`RollingBandBuffer`) lock rather than being lock-free — with a writer on the capture
thread and a reader on the timer thread, an unsynchronised race here would present as
a detection-accuracy problem rather than as the threading bug it is. Detection still
never runs on the UI thread. Detection results are queued and
drained with the `Interlocked.CompareExchange` re-entrancy idiom used three times
already in this codebase (`TriffAlertsService.cs:528-532`, `:873-893`,
`TriffViewSubsystem.cs:1187-1192`).

The service is disposed with the controller. Capture sessions are started and
stopped as clients appear and disappear, driven by the existing client refresh.

## Failure handling

- **Activation failure for one PID** must not affect other clients or the app.
  Status becomes `unavailable`; retried on the next client refresh.
- **Stream death** (device change, sleep/resume) is detected by a capture thread
  seeing no packets for 10 s; the session is torn down and re-activated. There is
  no existing suspend/resume hook in the codebase to reuse — this is the
  mechanism.
- **Windows version.** Process loopback requires Windows 10 build 20348+ in
  practice (Microsoft documents three different minimums across three pages). On
  older builds activation fails, every client reports `unavailable`, and the
  feature is inert. It must not crash or log-spam.

## Out of scope

- **Detecting a splash during combat.** The user confirmed this is not the use
  case — the concern is sitting idle. Untested and not designed for.
- **Detecting new ships in the overview.** Investigated and rejected; see below.
- **Per-client enable/disable.** One global switch, per the user's decision.
- **Template editing/trimming.** Capture, audition, save, delete only.

## Rejected: pixel detection of the overview

Investigated as the other half of the original request and rejected on three
independent grounds. Recorded here so it is not re-proposed.

1. DWM thumbnails are a compositor-side handoff with no readback; capture would
   need `Windows.Graphics.Capture` as entirely new machinery.
2. WGC draws a yellow capture border unless the app declares
   `graphicsCaptureWithoutBorder` in a **package manifest**. TriffView has no
   manifest and ships unpackaged single-file, so the border would appear on every
   EVE client and inside TriffView's own previews.
3. The overview auto-sorts by distance, so "a new row appeared" is not
   well-defined positionally; rows must be identified by OCR or template matching
   against small antialiased text on a semi-transparent panel over a moving 3D
   scene. Sanderling — the mature tool that solved reading EVE overview state —
   explicitly chose memory reading over screen scraping for exactly these reasons.

## Documentation correction

`CLAUDE.md` stated there was one test project with a `<Compile Include>`
constraint. There are in fact three targets: `tests/TriffView.Tests` (net8.0,
cross-platform, individually linked — as documented, run by
`.github/workflows/build.yml:29-30`), `native/TriffView.Tests` (net8.0-windows,
`ProjectReference` + `InternalsVisibleTo`, 27 test files, run by
`.github/workflows/ci.yml:49-55`), and the `tests/TriffAlerts.Tests` regression
harness invoked via `dotnet run` (`.github/workflows/ci.yml:58`). The documented
constraint applies only to the first. Corrected in `CLAUDE.md`, which also gained
a section on the audio subsystem's fixed detector constants and failure modes.
