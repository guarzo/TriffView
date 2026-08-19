# Diagnostics log

Builds from this fork write a plain-text log that records what the app sees, so intermittent
preview problems can be diagnosed from evidence instead of guesswork.

## Where it is

```
%APPDATA%\TriffHud\triffview-diagnostics.log
```

Paste that path into Explorer's address bar to open the folder. The file is capped at 2 MB; once
it fills, the previous contents move to `triffview-diagnostics.log.1`.

## What it records

| Entry | When | What it tells us |
|---|---|---|
| `startup` | app launch | Your monitor rectangles and every saved preview position |
| `display-changed` | a display is added, removed, or rearranged | The monitor set *at that moment* — including transient states that vanish before anyone can look |
| `layout-write` | a preview position is saved | The new rectangle, what triggered the save, and the call path that did it |
| `activation-failed` | clicking a preview fails to switch clients | That Windows refused the foreground change, rather than the click being missed |
| `audio-capture` | per-client audio capture starts, stops, or fails | Which client (by process ID) and what WASAPI reported; the usual read is "this client's capture died and why" |
| `audio-detection` | a splash-detection tick throws | The detection loop hit an exception; splash alerts may have stopped firing for one or more clients until the next successful tick |
| `audio-alert-sound` | the alert sound fails to play | The sound file or playback device had a problem; the splash was still detected, only the audio cue failed |
| `splash-templates` | a template (built-in or user-added) fails to load or parse | One template is missing from the match set — detection still runs against whatever else loaded |
| `splash-templates-critical` | **no** built-in templates loaded at all | Splash detection is silently disabled — nothing will ever alert until this is fixed. This is the one line in the log worth searching for first if splash alerts seem to not be firing at all |

Display changes are **observed only** — the log records them and nothing else happens differently.

## What it does *not* record

No keystrokes, no chat, no screenshots, no game data, no network activity. It never reads EVE's
logs or memory. Nothing is transmitted anywhere; the file sits on your disk until you choose to
send it. It does not record any audio itself — only capture/detection status lines, never
waveform data.

That is a claim about this log specifically, not about TriffView as a whole. The Combat Logs
tab can upload a zip of your actual game logs to a Discord webhook you configure yourself — that
is real network activity, and it lives entirely outside this file. See the [Combat log
export](../README.md#combat-log-export) section of the README for what that upload sends and where
it goes.

## Privacy note before you send it

**The log contains your EVE character names.** They are how preview positions are stored, so they
appear throughout — this matters more now that the log covers more of what's running, not less.
(Audio capture and detection lines identify clients by process ID rather than character name.) It
also contains your monitor layout.

If that matters to you, send the file privately rather than posting it publicly, or search and
replace the character names first — the numbers are what's diagnostically useful, so renaming
`Amelio Pellion` to `Char1` throughout costs nothing. Keep the replacement consistent so entries
can still be matched to each other.

## Reporting a problem

Include:

1. **What you saw**, in your own words — "the preview for X vanished", "clicking Y didn't switch".
2. **Roughly when**, so the timestamps can be located.
3. **Which monitor** the preview was supposed to be on, and where on it.
4. The log file.

If a preview moved somewhere unexpected, the most useful detail is **where you had put it** versus
where it ended up. The log shows the coordinates; only you can say which of them was intended.

If you can make it happen on demand, say how. A reproducible case is worth far more than a large
log.

## A caution on reading it yourself

The numbers are raw device coordinates, and they do **not** always line up with the monitor
rectangles in an obvious way. On mixed monitor arrangements a preview position that looks outside
every monitor listed can still be perfectly visible on screen.

That is a real, confirmed discrepancy, not a display bug — an earlier version of this log tried to
label positions "on screen" or "off screen" and got it wrong for exactly that reason, which is why
it now reports raw numbers and leaves the conclusions to whoever reads it. Don't assume a position
is wrong just because the arithmetic looks off.
