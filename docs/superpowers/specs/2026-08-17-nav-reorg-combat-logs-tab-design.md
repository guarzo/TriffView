# Navigation reorganisation: Combat Logs as a top-level tab

**Date:** 2026-08-17
**Branch:** `feat/nav-reorg-combat-logs-tab` (based on `fork/main`)
**Status:** Design approved in brainstorm; pending implementation plan

## Problem

Combat log export and upload is two clicks deep: a `Combat log export` entry in
`TriffViewSettings`' section rail, behind a top-level tab called `TriffView`
inside an application called TriffView. Users cannot find it.

Promoting it exposes a second problem. The topbar already carries brand text, a
tagline, a 178px theme `<select>`, an update pill, and four 112px nav buttons.
The window minimum is 860px (`native/MainWindow.xaml:9`) and the existing
content is roughly 950px wide. A fifth tab adds ~118px to a row that does not
fit today.

## Scope

Fork-only. Combat log export does not exist on `origin/main` or
`upstream/main` — verified with `git cat-file -e <ref>:app/src/tools/CombatLogExport.jsx`.
This must not be offered as an upstream PR.

## Decisions

Four options were considered for the shell (single left rail; two levels with a
slimmer topbar; sections promoted to the top row; full IA rewrite). **The chosen
approach keeps the existing two-level structure and reclaims topbar width.**

A single left rail merging both nav levels was the alternative with the best
long-term properties — horizontal space stops constraining the feature set
entirely — and was rejected as disproportionate to the problem.

### 1. Top-level navigation

`NAV_ITEMS` (`app/src/App.jsx:13`) becomes five entries:

| id | label |
| --- | --- |
| `triffview` | TriffView |
| `combat-logs` | Combat Logs |
| `eve-settings` | EVE Settings |
| `fleet-manager` | Fleet Manager |
| `skill-planner` | Skill Planner |

`combat-logs` is placed second, adjacent to TriffView, because it depends on
TriffAlerts state that lives under that tab.

### 2. Topbar width recovery

Two changes free an estimated ~340px, against the ~118px a fifth tab costs:

- **Delete the brand subtitle** `"Previews, fleets, and EVE settings"`
  (`App.jsx:220`), ~185px including its gap. It is a tagline that goes stale the
  moment combat logs moves out, and updating it merely defers the problem.
- **Replace the theme `<select>`** with a compact swatch-strip button: the three
  theme swatches plus a `▾` caret, opening a popup listing every theme by
  swatches *and* name. Today's picker is ~227px (43px swatches + 6px gap + 178px
  select, `styles.css:549-577`); the replacement is ~74px. Saves ~153px.

All figures are computed from the stylesheet, not measured in a browser.

#### The update pill is the binding case

The budget above ignores the update pill, which is a conditional topbar sibling
(`App.jsx:241-250`) with `max-width: 390px` (`styles.css:609`). With an update
available: five tabs (584px including gaps) + brand (~90px) + theme control
(~74px) + pill (up to 390px) + padding and sibling gaps (~56px) ≈ **1170px**,
which overflows even the 1120px default width, not merely the 860px minimum.

This squeeze exists today at four tabs; the change makes it worse rather than
introducing it. The topbar is `display: flex` with no `flex-wrap`
(`styles.css:508-516`) and nav buttons cannot shrink below `min-width: 112px`
(`:593`), so overflow is silent clipping, not reflow.

Deciding this needs a measurement, not more arithmetic. The manual test
therefore covers the pill-visible state explicitly. If it overflows, the
preferred remedy in order: reduce nav button `min-width`; allow the pill to
shrink below 390px; wrap the pill to a second row.

**The compact control must out-specify the topbar's button rule.**
`.triffview-standalone-topbar button` sets `min-width: 112px` (`styles.css:593`)
at specificity (0,1,1), which beats a bare `.triffview-theme-button` (0,1,0).
Without an explicit `.triffview-standalone-topbar .triffview-theme-button`
override the "compact" control renders at 112px and the entire width saving
above evaporates — silently, since nothing errors.

A swatches-only control (~60px) was rejected: three coloured squares do not read
as a control. Swatches-plus-name (~150px) was rejected: it saves too little to
make five tabs fit at the 860px minimum.

### 3. TriffView section rail

`sectionTabs` (`TriffViewSettings.jsx:1083-1092`) loses the `combat-logs` entry,
leaving seven: Profile settings, Preview layout, Color settings, Alerts,
Character Hotkeys, Cycle Groups and Hotkeys, Client management.

**Alerts stays where it is.** Combat logs depends on it — `lastFight` is
produced by TriffAlerts, and the empty state tells the user alerts must be
enabled. Rather than move Alerts too, the Combat Logs empty state gains a
control that navigates to `triffview` → Alerts. That control needs plumbing
that does not exist today; see *Cross-tab navigation to Alerts* below.

### 4. Tray menu

`native/MainWindow.xaml.cs:261-264` gains an `Open Combat Logs` item calling
`OpenTool("combat-logs")`. The tray is where users already right-click, so this
is plausibly a larger discoverability gain than the tab itself.

## Architecture

### Combat-log state hoists to a permanently-mounted subscriber

The state and its native subscription live in a `useCombatLogs()` hook called
from `App.jsx`, mounted for the application's lifetime, passing props down to
the Combat Logs tab. It is **not** a self-contained container that subscribes on
mount.

This is the single most important decision in the design, and the first draft
got it wrong. The reasoning:

`UploadCombatLogs` is `async void` with no dialog and can run long
(`native/TriffView/TriffViewSubsystem.cs:1462`). If the subscriber unmounts when
the user switches tabs, the terminal `triffview:combat-log-upload` message is
dropped — there is no "get last result" message and `triffview:state` carries no
results. The native concurrency guard then compounds it (`:1466-1474`):

```csharp
// Deliberately no reply for the rejected run -- the in-flight one always
// ends in a terminal combat-log-upload or error message, and a second
// reply arriving first would tell the UI an upload had finished while
// one was still going.
if (Interlocked.CompareExchange(ref _combatLogUploadInProgress, 1, 0) != 0) return;
```

A remounted component would show `busy: false` and enabled buttons; a second
click would be silently swallowed; and the UI would then report the *first*
upload's outcome as though it belonged to the second click. That comment records
a deliberate invariant in which the native side relies on the web UI holding its
own state across the operation. Promoting combat logs to a top-level tab is
exactly what makes "go look at the previews while it uploads" a natural motion,
so the promotion creates the exposure.

Hoisting is free: `onNativeMessage` registers a listener into a module-level
`Set` (`app/src/nativeBridge.js:16-18`) which is iterated on every inbound
message (`:1064`), so a permanent additional subscriber costs nothing and steals
nothing.

#### What hoisting does not fix

Hoisting bounds the exposure to tab switches. It does **not** survive the tray's
`Reload UI` item, which calls `AppWebView.Reload()`
(`native/MainWindow.xaml.cs:269`) and destroys all React state while the native
upload continues. On reload the UI shows `busy: false`, a second click is
swallowed by the same guard, and the first run's result is attributed to it —
the exact misattribution described above.

This is accepted rather than fixed. Closing it needs either a "get last result"
message (no such contract entry exists) or a reply for the rejected run, which
`:1466-1474` deliberately forbids. Both are larger than this change, and
reloading the UI mid-upload is rare where switching tabs mid-upload is not. The
guarantee this design buys is therefore **"an upload survives a tab switch"**,
not "an upload survives anything", and the manual test covers the reload case so
the behaviour is observed rather than assumed.

#### How the state stays fresh

`PostState` is **not** an unconditional heartbeat, and an early draft of this
spec was wrong to say so:

- the 700ms timer runs only while `Settings.Enabled` (`:470`)
- posts fire only when `topologyChanged || stateChanged` (`:551`)
- byte-identical payloads are suppressed (`:2520`)

It nonetheless works, for a different reason: `triffview:get-state` forces a
post (`:240-242`) for the initial paint, and thereafter `lastFight` is
recomputed on every post from `_alerts.History` (`:2504`) and reaches the UI via
the alert path's 100ms switch-state timer, which is gated on the *alerts*
settings (`native/TriffAlerts/TriffAlertsService.cs:411`) rather than on
`Settings.Enabled`. So `lastFight` arrives even with previews disabled.
`combatLogWebhook` is additionally pushed directly on every set/clear/test
(`PostCombatLogWebhookState`, `:1383-1392`).

Moved out of `TriffViewSettings.jsx`:

- state: `combatLogExport`, `combatLogRange`, `combatLogWebhook`,
  `combatLogWebhookAction`, `combatLogUpload` (`:1034-1045`); `lastFight`
  derived from `triffview:state` (`:1053`)
- senders: `exportCombatLogs`, `uploadCombatLogs`, `saveCombatLogWebhook`,
  `clearCombatLogWebhook`, `testCombatLogWebhook` (`:1222-1244`)
- receive branches for `triffview:combat-log-export` (`:1326`),
  `triffview:combat-log-webhook` (`:1333`), `triffview:combat-log-upload`
  (`:1359`), the `combatLogWebhook` slice of `triffview:state` (`:1318`), and
  four `triffview:error` branches keyed on `action`: `export-combat-logs`
  (`:1330`), `upload-combat-logs` (`:1369`), and the three webhook actions
  handled together (`:1344-1356`)
- the `<CombatLogExport>` render block (`:1885-1901`)

`lastFight` (`:1053`) becomes unused in `TriffViewSettings.jsx` and should be
deleted rather than left dead.

**`CombatLogExport.jsx` gains exactly one prop.** It is otherwise unchanged —
already presentational, taking eleven props and holding only local form state.
The twelfth is `onOpenAlerts`, used by the "no fight detected yet" empty state;
see below. An earlier draft claimed the file was unchanged *and* that its empty
state gained a navigation control. Those cannot both be true.

### Cross-tab navigation to Alerts

No mechanism for this exists today, and the first draft assumed one did.
`App` owns only `activeTool` (`App.jsx:121-122,251-258`), while
`TriffViewSettings` initialises `activeSection` to `"profile"` privately and
mutates it only from its own rail (`TriffViewSettings.jsx:1029,1413-1421`).

Minimal plumbing, three small changes:

1. `App` gains `triffViewSection` state, defaulting to `null`.
2. `TriffViewSettings` accepts two optional props: `initialSection`, synced into
   `activeSection` by an effect guarded to apply only when non-null, and
   `onInitialSectionApplied`, called once the nudge has been consumed so the
   parent can reset `triffViewSection` back to `null`. Its own rail continues to
   own the section thereafter — this is a nudge on entry, not a controlled prop.
   `activeSection` is deliberately excluded from the effect's dependencies so
   the user's own later rail clicks are never fought.
3. `CombatLogExport` calls `onOpenAlerts()`; `App` sets
   `triffViewSection = "alerts"` and `activeTool = "triffview"`.

**`onInitialSectionApplied` is load-bearing, not ceremony.** Without it a second
click of the control would be inert: `initialSection` would already be
`"alerts"` from the first click, so re-setting it to the same value would not
change the effect's dependencies and the effect would not re-run.

**Interaction with guide mode, and the race underneath it.** The nudge effect
requires *two* guards, not one. Gating only on `state.guideCompleted` does not
work: `EMPTY_STATE.guideCompleted` is `true` (`TriffViewSettings.jsx:11`) and
real state arrives asynchronously (`:1302-1314`), so the nudge would fire and be
consumed on the first render, before the component learns onboarding is
incomplete — after which the guide opens with nothing left to apply. An earlier
draft of this spec asserted the opposite and was wrong.

The effect therefore also requires a `stateReceived` flag set when the first
`triffview:state` arrives. Guide mode returns early and hides the rail
(`:1097-1104`), so applying a nudge underneath it would yank the user out of
onboarding; while the guide is showing the nudge stays unconsumed, and when the
guide completes, `guideCompleted` flips and the pending nudge applies then.

`onOpenAlerts` is optional. When absent, the empty state renders its existing
text with no control, so `CombatLogExport` remains usable standalone.

This deliberately does not introduce a general cross-tab routing mechanism. One
call site does not justify one, and the top-level tab set is small enough that a
second call site can extend this rather than replace it.

**Preserve the `cancelled` handling exactly.** The native side posts
`{ type: "triffview:combat-log-export", cancelled = true }` with no `result`
when the save dialog is cancelled (`:1696`). The current handler clears `busy`
correctly via `message.result || null` (`TriffViewSettings.jsx:1326-1328`). A
rewrite keyed on `message.result` being present leaves the export buttons
disabled forever after a cancel.

Net removal from `TriffViewSettings.jsx`: roughly 60 of 2140 lines. This does
not fix that file's size; it stops it growing further.

### Markup wrapper for the new tab

The tab's root is:

```jsx
<div className="triffview-settings">
  <div className="triffview-section-content" data-hud-scroll>
    …
  </div>
</div>
```

**Both levels are required, and an earlier draft of this spec was wrong to say
`.triffview-settings` alone would do.** Verified:

- `.triffview-settings` is `overflow: hidden` (`styles.css:1980`), not `auto`.
  It supplies the control font-size scoping (`styles.css:3916-3919`) — buttons
  and inputs do not inherit font-size, so without it every control renders at
  the UA default — but it does **not** scroll.
- `.triffview-section-content` is what supplies `overflow: auto`
  (`styles.css:2235`).
- `.triffview-standalone-panel` is `overflow: hidden` (`styles.css:661`), so the
  panel above provides no scrolling either.
- `data-hud-scroll` does not rescue a non-scrolling element: the HUD's
  `hasScrollableOverflow` requires a *computed* overflow of `auto`, `scroll`, or
  `overlay` (`nativeBridge.js:633`).

Get this wrong and the tab clips its content with no scrollbar, silently.

The combat-log CSS itself (`.triff-combat-export*`, `styles.css:3066-3178`) is
top-level and needs no changes.

### Message contract

No new message types, and no changes to existing payloads. The only contract
change is that `standalone:navigate` gains a valid `tool` value, `combat-logs`.
`App.jsx:172` already validates incoming ids against `NAV_ITEMS` and ignores
unknown ones, so an older UI bundle paired with a newer native binary
degrades to "nothing happens" rather than breaking.

### Theme control

The current `<select>` supplies its accessible name via `aria-label="GUI theme"`
plus keyboard operation and popup rendering from the browser. The replacement
must reimplement all of it:

- a `<button>` with `aria-haspopup="listbox"`, `aria-expanded`, and an
  accessible name including the current theme name
- a keyboard-operable listbox (arrow keys, Enter, Escape), focus returned to the
  button on close
- dismissal on outside click and on Escape

Theme persistence is unchanged: `localStorage` under `triffview.guiTheme`
(`App.jsx:17`), *not* `triffview-settings.json`. This is why the control is not
placed inside Profile settings, which would imply it is per-profile.

## Risks

**The topbar arithmetic is unverified.** Five 112px tabs plus brand plus the
~74px theme control is approximately 700px. This fits 1120px comfortably and is
expected to fit 860px, but no measurement has been taken on Windows. If it does
not fit, the fallback is reducing the nav button `min-width` below 112px
(`styles.css:593`), not another IA change.

**The theme popup is not a `<select>`.** `nativeBridge.js` contains dedicated
handling for native select popups (`activeSelectPopup`) as part of the HUD's
custom pointer input layer. A custom popup does not receive that handling. Its
behaviour under HUD input routing is unknown and must be exercised on Windows.

**Two components subscribing to native messages.** The hoisted combat-log
subscriber is mounted for the app's lifetime alongside `TriffViewSettings`' own.
`onNativeMessage` broadcasts to a `Set`, so this is safe, but the pattern is new
to this codebase.

**`standalone:navigate` is not validated native-side.** `OpenTool`
(`MainWindow.xaml.cs:804-808`) posts an arbitrary string; the only check is
membership in `NAV_ITEMS` (`App.jsx:172-176`). A typo in the new tray item shows
the settings window on whatever tab was already active, with no error anywhere.
The id must be exactly `combat-logs` on both sides.

## Documentation to update

- `README.md:66` — the `### Combat log export` section describes it as a panel
  inside TriffView settings. **The heading text must not change**:
  `docs/DIAGNOSTICS.md:35` links `../README.md#combat-log-export`, and the
  anchor is derived from the heading.
- `docs/superpowers/specs/2026-08-15-combat-log-export-tab-design.md` describes
  it as a section tab and is now historical. Leave it as a record; do not
  rewrite history.
- `GUIDE_STEPS` (`TriffViewSettings.jsx:367-398`) does not mention combat logs.
  No change needed.

## Intentional behaviour changes

**Combat logs becomes reachable before the guide is completed.** Guide mode
returns early and hides `sectionTabs` entirely (`TriffViewSettings.jsx:1097-1104`),
so combat log export is currently unreachable until the guide is completed or
skipped. As a top-level tab it is reachable immediately. This is an improvement,
recorded here so it is not mistaken for a regression.

## Verification

There is no JavaScript test framework: `app/package.json` has no `test` script
and empty `devDependencies`. The C# suite links pure-logic files
(`tests/TriffView.Tests`) and cannot see React. Adding a JS test framework is
explicitly out of scope for this change.

Automated, runnable from WSL:

- `cd app && npm run build` — catches syntax and import errors only
- native build via the explicit-SDK invocation (see CLAUDE.md), for the tray
  change

Manual, on Windows, accepted by the user as sufficient for this change:

1. Five tabs fit at the 1120px default and at the 860px minimum
2. **The same, with an update pill visible** — the binding case; see the topbar
   budget above
3. Combat Logs shows `lastFight` and webhook status with `TriffViewSettings`
   unmounted
4. **Start an upload, switch to the TriffView tab, switch back — the result
   still arrives and the buttons re-enable.** This is the case the hoisted
   subscriber exists for; if it regresses, the failure is silent.
5. **Start an upload, then tray → Reload UI.** Expected: the result is lost and
   a second upload is swallowed. Confirming the *known* bound, not a fix.
6. An export and a Discord upload both succeed from the new tab
7. Cancelling the export save dialog re-enables the export buttons
8. The Combat Logs empty state's Alerts control lands on TriffView → Alerts
9. Theme popup opens, selects, closes, and is keyboard-operable under the HUD
   input layer
10. Tray → Open Combat Logs lands on the Combat Logs tab
11. TriffView's rail shows seven sections and no combat-log entry
12. Controls in the new tab render at 13px, **and the tab scrolls when the
    content overflows** — shrink the window vertically to force it

## Out of scope

- Merging the two navigation levels into a single rail
- Moving Alerts out of the TriffView rail
- Any JavaScript test framework
- Splitting `TriffViewSettings.jsx` beyond the combat-log extraction
- Any upstream PR (this feature is fork-only)

## Noted but not addressed

Three findings from the blind-spot pass are real and out of scope here:

- `triffview:set-settings-open` is effectively a no-op. `ApplyTopmostPolicy`
  (`TriffViewSubsystem.cs:444-451`) never reads `_settingsPanelOpen`, and the
  public `SettingsPanelOpen` property (`:67`) has no consumers.
- `InputOverlayWindow` is constructed (`MainWindow.xaml.cs:383`) but `Show()` is
  never called, so `publishInputRegions` and every `data-hud-input-region`
  attribute currently feed a window that is never displayed.
- `nativeBridge`'s select-popup handling requires a `.hud-panel` ancestor, which
  nothing in the `App.jsx` tree has — so that path is dormant for the theme
  select today. Replacing the `<select>` with buttons removes a latent trap
  rather than creating one.
