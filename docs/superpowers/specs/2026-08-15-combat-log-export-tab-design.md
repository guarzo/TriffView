# Combat log export as its own section tab

Date: 2026-08-15
Status: **designed, approved — not yet implemented**

## Outcome

Combat log export moves out of the Alerts section into its own TriffView
section tab, named **Combat log export**, sitting directly after Alerts. The
block is reflowed to suit a full section rather than a subsection, and is
extracted into its own component file.

Nothing about what the feature *does* changes. No native code, no message
contract, no settings schema.

## Why

The export is currently the third `triffview-subsection` inside the Alerts
section (`app/src/tools/TriffViewSettings.jsx:1822`), below the alert event
grid and the alert history list, above the master volume slider. A user who
knows the feature exists does not find it, which is the report that started
this: it was shipped in `f90f087` and read as missing.

Being under Alerts is not arbitrary — `lastFight` exists only because
TriffAlerts is watching for incoming damage and warp scrambles. But that is an
argument about where the *data* comes from, not about where the *control*
belongs, and the nav is the only thing a user scans.

## Scope decisions, recorded

Two were live during design and are settled:

- **Section tab, not a top-level tool.** A top-level tool (beside Fleet Manager
  and Skill Planner) would be more discoverable still, but it crosses into
  native — the tray menu at `native/MainWindow.xaml.cs:262` has an entry per
  tool, and `standalone:navigate` (`MainWindow.xaml.cs:807`) carries tool ids.
  It would also need its own `triffview:state` subscription to reach
  `lastFight`. Rejected as disproportionate: the three existing top-level tools
  are each an entire subsystem, and this is two buttons.
- **Nothing left behind in Alerts.** No "this has moved" pointer. The new tab is
  one position away in the same nav, so the cost of a user looking in the old
  place is a glance; a permanent redirect line is clutter that never expires.
  This depends on the nav actually being visible — see *Sidebar capacity*, which
  is a precondition of this decision rather than a detail.

Explicitly **not** in scope: the free-text UTC range inputs. They are
`type="text"` with a `2026-08-14 20:10` placeholder and no client-side
validation, so a malformed entry is caught at the native end
(`native/TriffView/TriffViewSubsystem.cs:1248`), which returns a specific
message carrying an example: "Enter both a start and an end time in UTC, for
example 2026-08-14 20:10." The React handler surfaces that text verbatim
(`TriffViewSettings.jsx:1319`).

The gap is therefore narrower than it first looks: the failure is legible, just
late. Moving validation client-side is a behavior change and stays a separate
decision rather than riding along inside a move.

## Structure

Add to `sectionTabs` (`TriffViewSettings.jsx:1106`), after `["alerts", "Alerts"]`:

```jsx
["combat-logs", "Combat log export"],
```

Add a render block after the alerts block closes (line 1908), following the
established `{activeSection === … ? … : null}` shape, and delete the
subsection at 1822–1897.

The name is deliberately the longer one. "Combat logs" reads like a viewer; the
tab packages logs, it does not display them, and the nav label is the one place
a user decides whether to click.

### State stays where it is

`combatLogExport` and `combatLogRange` (1068–1069), `exportCombatLogs` (1244),
the `triffview:combat-log-export` and `triffview:error` handlers (1315, 1319),
and `lastFight` (1075) all remain in `TriffViewSettings`. Because this is a
section swap inside one component, there is **no new state subscription and no
native change** — the thing that could have made this risky does not arise.

### Extraction

The panel body is extracted to `app/src/tools/CombatLogExport.jsx` as a
presentational component:

```jsx
<CombatLogExport
  lastFight={lastFight}
  exportState={combatLogExport}
  range={combatLogRange}
  onRangeChange={setCombatLogRange}
  onExport={exportCombatLogs}
/>
```

It owns no state and sends no messages; the parent keeps both. This lifts ~90
lines out of a 2147-line file and lets the tab be read on its own.

The body also depends on three module-private helpers in the host file, none of
which are exported (`TriffViewSettings.jsx:2147` is the only export): the `Field`
component (`:137`), `formatUtcWindow` (`:557`), and `formatBytes` (`:564`).
Implementing the prop interface above without addressing them does not compile.
They are handled as follows:

- `Field` — exported from `TriffViewSettings.jsx` and imported by the new
  component. It is a generic label/children wrapper used by ten call sites
  across the file; duplicating it would be worse than exporting it.
- `formatUtcWindow` and `formatBytes` — **moved** into `CombatLogExport.jsx`.
  Both are used only by the block being extracted; verify this before moving,
  and if another section has picked up a call site, export from the host
  instead of moving.

Moving a helper that turns out to have a second caller is the one way this step
breaks the build, so the check is part of the step, not an afterthought.

This is a deliberate departure from the file's convention — every other section
is inline. The trade was weighed and the size of the host file won. It is not a
licence to extract the other sections as drive-by work.

## Sidebar capacity

The section nav is a fixed 214px column with no scrolling
(`app/src/styles.css:2142`), inside a shell that clips overflow
(`app/src/styles.css:2127`). Adding an eighth tab adds roughly 43px (38px
minimum button height at `styles.css:3877`, plus a 5px gap) to a column that is
already near its limit at the window's minimum height.

Rough accounting against `MinHeight="600"` (`native/MainWindow.xaml:10`), less
the 44px top bar (`styles.css:501`): about 540px of shell, against 30px padding
+ ~45px brand block + 24px gaps + seven buttons (296px) + four action controls
(~159px) ≈ 554px of content. The sidebar is therefore plausibly clipping
**already** at minimum height, before this change.

That is not a defect this change introduces, but it is one this change makes
worse, and the "nothing left behind" decision above assumes the nav is
reachable. So it is in scope:

```css
.triffview-side-nav {
  overflow-y: auto;
  min-height: 0;
}
```

`min-height: 0` is required alongside `overflow-y` because the element is a
flex child; without it the flex floor prevents it from shrinking enough to
scroll. The numbers above are estimates from CSS, not measurements — the
verification step is to check it on Windows at the minimum window size, not to
trust this arithmetic.

## Layout

Top to bottom in the new panel:

1. **Intro** — the existing muted paragraph, wording unchanged. It is accurate
   and carries the two things a user needs: what the zip is for (Discord,
   eve-intel) and the guarantee that chat logs are never included.
2. **Last-fight status strip** — reusing the `.triff-alert-summary` pattern
   already at the top of the Alerts section, so it reads as native to this UI
   rather than invented. Persistent state line: fight window and characters, or
   the "no fight detected yet" explanation.
3. **Two export paths at equal weight** — a two-column grid: *Last fight*
   (button) and *Time range* (From/To plus button). Today the range path sits
   below the button and reads as an afterthought; it is the only path that
   works for a fight predating the current session, so it should not look
   secondary. Its collapse to one column is governed by the panel width, not
   the viewport — see *Responsive behavior* below.
4. **Result/error slot** — a container with a reserved minimum height below
   both paths. Both are conditionally rendered today, so a result appearing
   shifts the buttons under the user's cursor.

### Responsive behavior

The existing `@media (max-width: 620px)` collapse on `.triff-combat-export-range`
(`styles.css:3084`) **never fires in the native app**, and the new layout must
not repeat the mistake. The media query keys off the viewport, but the window
cannot go below `MinWidth="860"` (`native/MainWindow.xaml:9`), so the viewport
is never under 620px in production. The rule is reachable only from a dev server
at a narrow browser width.

Meanwhile the panel itself is much narrower than the window: 860 − 214 (sidebar,
`styles.css:2129`) − 1 (border) − 44 (content padding, `styles.css:2237`) ≈
**601px** at minimum width. So the content box is already below the breakpoint
that was meant to protect it, while the query that would collapse it cannot
trigger.

The two-column grid therefore uses a **container query** on the panel, not a
viewport media query:

```css
.triff-combat-export-paths {
  container-type: inline-size;
}

@container (max-width: 620px) {
  .triff-combat-export-paths {
    grid-template-columns: 1fr;
  }
}
```

Container queries are supported in the WebView2 runtime this app targets
(Chromium 105+); confirm against the runtime actually installed before relying
on it. If that check fails, the fallback is a viewport media query at a
breakpoint derived from the real panel width — `max-width: 1180px`, i.e. 620px
of panel plus the 259px of chrome and 301px of margin — which is uglier but
works. Do not silently keep the 620px viewport figure; it is the bug.

The existing dead rule on `.triff-combat-export-range` is replaced as part of
this work rather than left behind.

CSS: the existing `.triff-combat-export-*` rules (`styles.css:3065–3107`)
largely survive. `-actions` and `-range` are regrouped into the two-column
layout; `-result` and `-error` keep their styling and gain the reserved
container.

## Behavior

Unchanged, and specifically preserved:

- Both buttons disable while `exportState.busy`.
- "Export last fight" disables without a `lastFight`.
- "Export range" disables until both fields are non-empty.
- Result and error remain mutually exclusive — each setter already clears the
  other, so the reserved slot renders whichever is set.
- The dropped-file and over-10MB warnings keep their current wording.

## Documentation

`README.md` describes the feature as living in the alerts panel and becomes
wrong on merge:

- Line 68: "The alerts panel can package the game logs covering a fight…"
- Line 22, the feature bullet, is location-neutral and needs only a check.

## Verification

`app/` has no test tooling — no test script, no runner, no test files, empty
`devDependencies`. Adding a framework is not in scope, so there are no
automated tests for this change. Verification is:

- `npm run build` in WSL. Catches syntax and import errors, nothing more.
- Manual check against a dev server or a rebuilt exe: the tab appears after
  Alerts, both export paths run, result and error render in the reserved slot
  without shifting the buttons, and the block is gone from Alerts.
- **At the window's minimum size (860x600), specifically** — resize the window
  down and confirm: the section nav scrolls rather than clipping, every one of
  the eight tabs plus the four action controls is reachable, and the two export
  paths have collapsed to one column. This is where the two layout findings
  land, and the default 1120x780 window will not show either of them.
- Container query support in the installed WebView2 runtime, before relying on
  it.

**The manual half cannot be run from WSL** — this project does not build or run
there. Any claim about appearance or runtime behavior is unverified until
exercised on Windows.

## Risks

Low, with the layout work carrying what risk there is. One component moves
between two render branches in one file; the message contract, the settings
schema, and all native code are untouched. Preview geometry — this repo's usual
trap — is not involved.

Two things could regress beyond this feature's own surface:

- The `overflow-y` change touches the shared section nav, so it affects every
  tab, not just this one. It is one property on one selector, and the current
  behavior at minimum height is clipping, but it is the only edit here with
  blast radius outside the new panel.
- Moving `formatUtcWindow` or `formatBytes` breaks the build if either has a
  caller outside the extracted block.

Both are caught by the verification above.
