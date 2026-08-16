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
- **Nothing left behind in Alerts.** No "this has moved" pointer. The nav is
  always visible and the new tab is one position away, so the cost of a user
  looking in the old place is a glance; a permanent redirect line is clutter
  that never expires.

Explicitly **not** in scope: the free-text UTC range inputs. They are
`type="text"` with a `2026-08-14 20:10` placeholder and no visible validation,
so a malformed entry fails in the native parser and returns a generic export
error. That is a real weakness and a behavior change, so it stays a separate
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

This is a deliberate departure from the file's convention — every other section
is inline. The trade was weighed and the size of the host file won. It is not a
licence to extract the other sections as drive-by work.

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
   secondary. Collapses to one column under 620px, matching the breakpoint
   already on `.triff-combat-export-range` (`styles.css:3084`).
4. **Result/error slot** — a container with a reserved minimum height below
   both paths. Both are conditionally rendered today, so a result appearing
   shifts the buttons under the user's cursor.

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
  without shifting the buttons, the 620px collapse behaves, and the block is
  gone from Alerts.

**The manual half cannot be run from WSL** — this project does not build or run
there. Any claim about appearance or runtime behavior is unverified until
exercised on Windows.

## Risks

Low. One component moves between two render branches in one file; the message
contract, the settings schema, and all native code are untouched. Preview
geometry — this repo's usual trap — is not involved.

The reflow is the only part that can regress anything, and it regresses
appearance rather than function.
