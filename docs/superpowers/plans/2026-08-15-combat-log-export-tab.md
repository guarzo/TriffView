# Combat Log Export Tab Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Move the combat log export out of the Alerts section into its own TriffView section tab, reflowed to suit a full section.

**Architecture:** Five independent changes in `app/` only. The panel body is extracted to its own component while staying in place, the shared section nav is made scrollable, the component is then moved to a new section tab, the panel is reflowed with a container query, and the README is corrected. No native code, no WebView2 message contract, no settings schema.

**Scope note:** the branch carries one further commit that this plan did not cover — quick 1h/2h range buttons, designed separately after all five tasks below were complete and reviewed. See *Follow-on* at the end of this document. Tasks 1-5 below are the record of what was actually executed, in order, and are deliberately left as they were written.

**Tech Stack:** React 18, Vite 6, plain CSS. No test runner in `app/`.

**Spec:** `docs/superpowers/specs/2026-08-15-combat-log-export-tab-design.md`

## Global Constraints

- **`app/` has no test framework** — no test script, no runner, empty `devDependencies`. Do not add one; it is not in scope. Every task verifies with `npm run build` plus a named manual check.
- **This project cannot be built or run from WSL.** `npm run build` works in WSL; the app itself does not. Every manual check is a Windows check. Do not claim a manual check passed unless it was actually run on Windows.
- **Do not touch `app/package-lock.json`.** Running `npm install` under WSL strips Windows-relevant optional dependencies. If `git status` shows it modified, `git checkout -- app/package-lock.json` before committing.
- **No native changes.** If a task seems to require editing anything under `native/`, stop — that contradicts the spec.
- Section tab id is `combat-logs`; the label is exactly `Combat log export`.
- Run all commands from the repo root unless stated otherwise.

---

### Task 1: Extract the panel body into its own component

Pure refactor. The UI must look and behave identically when this task lands — the block stays inside the Alerts section. Extracting and moving in one step would make a rendering regression indistinguishable from an intended change.

**Files:**
- Create: `app/src/tools/Field.jsx`
- Create: `app/src/tools/CombatLogExport.jsx`
- Modify: `app/src/tools/TriffViewSettings.jsx` (remove `Field` at :137-144, remove `formatUtcTime`/`formatUtcWindow`/`formatBytes` at :545-570, replace the subsection at :1822-1897)
- Test: none — no runner exists

**Interfaces:**
- Consumes: nothing
- Produces:
  - `Field({ label, children })` — default export of `Field.jsx`
  - `CombatLogExport({ lastFight, exportState, range, onRangeChange, onExport })` — default export of `CombatLogExport.jsx`. `lastFight` is `{ startUtc, endUtc, characters }` or `null`; `exportState` is `{ result, error, busy }`; `range` is `{ from, to }`; `onRangeChange` takes a React updater function; `onExport` takes a `{ from, to }` object or `null`.

**Note on `Field`:** the spec said to export `Field` from `TriffViewSettings.jsx`. That creates an import cycle, because `TriffViewSettings.jsx` imports `CombatLogExport.jsx`. It moves to its own module instead. The name is unchanged, so the six existing call sites need only the new import.

**Note on the formatters:** `formatUtcWindow` and `formatBytes` are called only from the block being extracted (`:1833`, `:1883`), and `formatUtcTime` only from `formatUtcWindow`. All three move. `EveSettings.tsx:98` has its own unrelated `formatBytes`; leave it alone.

- [ ] **Step 1: Create `app/src/tools/Field.jsx`**

```jsx
import React from "react";

function Field({ label, children }) {
  return (
    <label className="triffview-field">
      <span>{label}</span>
      {children}
    </label>
  );
}

export default Field;
```

- [ ] **Step 2: Delete the `Field` definition from `TriffViewSettings.jsx`**

Remove lines 137-144 (the whole `function Field({ label, children }) { ... }` block) and add to the imports at the top of the file:

```jsx
import Field from "./Field.jsx";
```

- [ ] **Step 3: Create `app/src/tools/CombatLogExport.jsx`**

The JSX body is copied verbatim from `TriffViewSettings.jsx:1822-1897`, with `combatLogExport` renamed to `exportState`, `combatLogRange` to `range`, `setCombatLogRange` to `onRangeChange`, and `exportCombatLogs` to `onExport`.

```jsx
import React from "react";
import Field from "./Field.jsx";

// EVE writes its logs in EVE time, which is UTC, and the export window is
// matched against those timestamps. Showing it in local time would invite the
// user to type a local range into a field that is read as UTC.
function formatUtcTime(value) {
  if (!value) return "";
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return "";
  const hours = String(date.getUTCHours()).padStart(2, "0");
  const minutes = String(date.getUTCMinutes()).padStart(2, "0");
  return `${hours}:${minutes}`;
}

function formatUtcWindow(startUtc, endUtc) {
  const start = formatUtcTime(startUtc);
  const end = formatUtcTime(endUtc);
  if (!start || !end) return "";
  return start === end ? `${start}Z` : `${start}-${end}Z`;
}

function formatBytes(value) {
  const bytes = Number(value);
  if (!Number.isFinite(bytes) || bytes < 0) return "";
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(0)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}

function CombatLogExport({ lastFight, exportState, range, onRangeChange, onExport }) {
  return (
    <div className="triffview-subsection">
      <div className="triff-alert-history-head">
        <h4>Combat log export</h4>
      </div>
      <p className="triffview-muted">
        Packages the EVE game logs covering a fight into a zip you can upload to Discord for
        eve-intel. Game logs only, copied as-is, plus a small manifest listing your characters.
        Chat logs are never included.
      </p>
      {lastFight ? (
        <p className="triff-combat-export-window">
          Last fight <strong>{formatUtcWindow(lastFight.startUtc, lastFight.endUtc)}</strong>
          {lastFight.characters?.length ? ` - ${lastFight.characters.join(", ")}` : ""}
        </p>
      ) : (
        <p className="triffview-muted">
          No fight detected yet. Alerts must be enabled, and only fights seen while TriffView
          has been running are detected - use the time range below for anything older.
        </p>
      )}
      <div className="triff-combat-export-actions">
        <button
          type="button"
          disabled={!lastFight || exportState.busy}
          onClick={() => onExport(null)}
        >
          Export last fight
        </button>
      </div>
      <div className="triff-combat-export-range">
        <Field label="From (UTC)">
          <input
            type="text"
            placeholder="2026-08-14 20:10"
            value={range.from}
            onChange={(event) => onRangeChange((current) => ({ ...current, from: event.target.value }))}
          />
        </Field>
        <Field label="To (UTC)">
          <input
            type="text"
            placeholder="2026-08-14 20:35"
            value={range.to}
            onChange={(event) => onRangeChange((current) => ({ ...current, to: event.target.value }))}
          />
        </Field>
        <button
          type="button"
          disabled={!range.from || !range.to || exportState.busy}
          onClick={() => onExport(range)}
        >
          Export range
        </button>
      </div>
      {exportState.result ? (
        <p className="triff-combat-export-result">
          Exported {exportState.result.fileCount} log
          {exportState.result.fileCount === 1 ? "" : "s"}
          {exportState.result.characters?.length
            ? ` (${exportState.result.characters.join(", ")})`
            : ""}{" "}
          to {exportState.result.path} - {formatBytes(exportState.result.zipBytes)} zipped.
          {exportState.result.droppedFileCount
            ? ` ${exportState.result.droppedFileCount} further matching log${
                exportState.result.droppedFileCount === 1 ? " was" : "s were"
              } left out at the file limit - narrow the time range to cover them.`
            : ""}
          {exportState.result.exceedsDiscordLimit
            ? " This is over Discord's 10 MB upload limit, so a narrower time range may be needed."
            : ""}
        </p>
      ) : null}
      {exportState.error ? (
        <p className="triff-combat-export-error">{exportState.error}</p>
      ) : null}
    </div>
  );
}

export default CombatLogExport;
```

- [ ] **Step 4: Delete the three formatters from `TriffViewSettings.jsx`**

Remove lines 545-570 — the `formatUtcTime` comment and function, `formatUtcWindow`, and `formatBytes`. Leave `severityLabel` (:572) in place.

- [ ] **Step 5: Replace the inline block with the component**

In `TriffViewSettings.jsx`, replace the whole subsection at :1822-1897 with:

```jsx
<CombatLogExport
  lastFight={lastFight}
  exportState={combatLogExport}
  range={combatLogRange}
  onRangeChange={setCombatLogRange}
  onExport={exportCombatLogs}
/>
```

and add to the imports at the top:

```jsx
import CombatLogExport from "./CombatLogExport.jsx";
```

- [ ] **Step 6: Verify the build**

Run: `cd app && npm run build`
Expected: `✓ built in …`, no errors. A `formatUtcWindow is not defined` or `Field is not defined` error here means a call site was missed in Step 2 or Step 4.

- [ ] **Step 7: Confirm no stale references remain**

Run: `cd app && grep -n "formatUtcWindow\|formatUtcTime" src/tools/TriffViewSettings.jsx`
Expected: no output.

Run: `cd app && grep -c "Field" src/tools/TriffViewSettings.jsx`
Expected: a non-zero count — the six call sites remain and are now satisfied by the import.

- [ ] **Step 8: Commit**

```bash
git add app/src/tools/Field.jsx app/src/tools/CombatLogExport.jsx app/src/tools/TriffViewSettings.jsx
git commit -m "Extract the combat log export panel into its own component"
```

---

### Task 2: Make the section nav scrollable

Lands before the eighth tab exists, so the nav is never in a clipping state in the committed tree.

**Files:**
- Modify: `app/src/styles.css:2142` (`.triffview-side-nav`)
- Test: none

**Interfaces:**
- Consumes: nothing
- Produces: nothing consumed by later tasks

- [ ] **Step 1: Add overflow to the side nav**

In `app/src/styles.css`, the `.triffview-side-nav` rule currently reads:

```css
.triffview-side-nav {
  display: flex;
  align-self: stretch;
  height: 100%;
  min-height: 0;
  flex-direction: column;
  gap: 12px;
  border-right: 1px solid var(--tv-border-soft);
  background: rgba(var(--tv-bg-rgb), 0.72);
  padding: 15px 12px;
}
```

Add `overflow-y: auto;` after `min-height: 0;`. `min-height: 0` is already present and is what lets the flex child shrink enough to scroll; do not remove it.

```css
.triffview-side-nav {
  display: flex;
  align-self: stretch;
  height: 100%;
  min-height: 0;
  overflow-y: auto;
  flex-direction: column;
  gap: 12px;
  border-right: 1px solid var(--tv-border-soft);
  background: rgba(var(--tv-bg-rgb), 0.72);
  padding: 15px 12px;
}
```

- [ ] **Step 2: Verify the build**

Run: `cd app && npm run build`
Expected: `✓ built in …`, no errors.

- [ ] **Step 3: Manual check on Windows**

Build and run the app, then drag the window down to its minimum height (600px). Confirm the section nav scrolls and every action control (Lock previews, Enable/Disable, Suspend hotkeys, Guide setup) is reachable. At the default 1120x780 nothing will look different — that is expected, and is not evidence the change works.

- [ ] **Step 4: Commit**

```bash
git add app/src/styles.css
git commit -m "Let the settings section nav scroll instead of clipping"
```

---

### Task 3: Add the section tab and move the panel into it

**Files:**
- Modify: `app/src/tools/TriffViewSettings.jsx` (`sectionTabs` at :1106, the Alerts section, and a new render branch)
- Test: none

**Interfaces:**
- Consumes: `CombatLogExport` from Task 1
- Produces: section id `combat-logs`

- [ ] **Step 1: Add the tab to `sectionTabs`**

In `TriffViewSettings.jsx`, `sectionTabs` currently reads:

```jsx
  const sectionTabs = [
    ["profile", "Profile settings"],
    ["layout", "Preview layout"],
    ["colors", "Color settings"],
    ["alerts", "Alerts"],
    ["hotkeys", "Character Hotkeys"],
    ["cycles", "Cycle Groups and Hotkeys"],
    ["clients", "Client management"],
  ];
```

Insert `["combat-logs", "Combat log export"],` directly after the `alerts` entry:

```jsx
  const sectionTabs = [
    ["profile", "Profile settings"],
    ["layout", "Preview layout"],
    ["colors", "Color settings"],
    ["alerts", "Alerts"],
    ["combat-logs", "Combat log export"],
    ["hotkeys", "Character Hotkeys"],
    ["cycles", "Cycle Groups and Hotkeys"],
    ["clients", "Client management"],
  ];
```

No change is needed to `activeSectionLabel` (:1115) — it derives the heading from `sectionTabs`, so the section header will read "Combat log export" automatically.

- [ ] **Step 2: Remove the component from the Alerts section**

Delete the `<CombatLogExport ... />` element added in Task 1, Step 5. The master volume `SliderControl` that followed it stays where it is.

- [ ] **Step 3: Add the new render branch**

After the Alerts section's closing `) : null}` and before the `{activeSection === "hotkeys" ?` branch, add:

```jsx
{activeSection === "combat-logs" ? (
  <div className="triffview-panel">
    <CombatLogExport
      lastFight={lastFight}
      exportState={combatLogExport}
      range={combatLogRange}
      onRangeChange={setCombatLogRange}
      onExport={exportCombatLogs}
    />
  </div>
) : null}
```

- [ ] **Step 4: Verify the build**

Run: `cd app && npm run build`
Expected: `✓ built in …`, no errors.

- [ ] **Step 5: Manual check on Windows**

Build and run the app. Confirm: a "Combat log export" tab appears between Alerts and Character Hotkeys; clicking it shows the export panel with the section heading "Combat log export"; the Alerts section no longer contains the export block but still ends with the master volume slider; both export buttons still work.

- [ ] **Step 6: Commit**

```bash
git add app/src/tools/TriffViewSettings.jsx
git commit -m "Give combat log export its own settings section tab"
```

---

### Task 4: Reflow the panel for a full section

**Files:**
- Modify: `app/src/tools/CombatLogExport.jsx`
- Modify: `app/src/styles.css:3065-3107` (the `.triff-combat-export-*` rules)
- Test: none

**Interfaces:**
- Consumes: `CombatLogExport` from Task 1, section id from Task 3
- Produces: nothing consumed by later tasks

**Note on the breakpoint:** the existing `@media (max-width: 620px)` rule at `styles.css:3084` is dead — the window cannot go below `MinWidth="860"` (`native/MainWindow.xaml:9`), so the viewport never reaches 620px, while the panel content box is only ~601px wide at that minimum. It is replaced by a container query, not kept. Container queries need Chromium 105+; the WebView2 SDK is pinned at `1.0.2792.45` (`native/TriffView.csproj:27`), Chromium ~127, so this is settled and needs no runtime probe.

- [ ] **Step 1: Replace the component's JSX**

In `CombatLogExport.jsx`, replace the returned JSX (keep the three formatters and the imports exactly as they are) with:

```jsx
  return (
    <div className="triff-combat-export">
      <p className="triffview-muted">
        Packages the EVE game logs covering a fight into a zip you can upload to Discord for
        eve-intel. Game logs only, copied as-is, plus a small manifest listing your characters.
        Chat logs are never included.
      </p>

      <div className="triff-alert-summary">
        <div>
          <strong>Last fight</strong>
          <span>
            {lastFight
              ? `${formatUtcWindow(lastFight.startUtc, lastFight.endUtc)}${
                  lastFight.characters?.length ? ` - ${lastFight.characters.join(", ")}` : ""
                }`
              : "No fight detected yet. Alerts must be enabled, and only fights seen while TriffView has been running are detected - use a time range for anything older."}
          </span>
        </div>
        <span className={lastFight ? "triff-alert-status is-on" : "triff-alert-status"}>
          {lastFight ? "Ready" : "None"}
        </span>
      </div>

      <div className="triff-combat-export-paths">
        <div className="triff-combat-export-path">
          <h4>Last fight</h4>
          <p className="triffview-muted">Export the fight TriffAlerts most recently detected.</p>
          <button
            type="button"
            disabled={!lastFight || exportState.busy}
            onClick={() => onExport(null)}
          >
            Export last fight
          </button>
        </div>

        <div className="triff-combat-export-path">
          <h4>Time range</h4>
          <p className="triffview-muted">For a fight from before TriffView was started.</p>
          <div className="triff-combat-export-range">
            <Field label="From (UTC)">
              <input
                type="text"
                placeholder="2026-08-14 20:10"
                value={range.from}
                onChange={(event) => onRangeChange((current) => ({ ...current, from: event.target.value }))}
              />
            </Field>
            <Field label="To (UTC)">
              <input
                type="text"
                placeholder="2026-08-14 20:35"
                value={range.to}
                onChange={(event) => onRangeChange((current) => ({ ...current, to: event.target.value }))}
              />
            </Field>
          </div>
          <button
            type="button"
            disabled={!range.from || !range.to || exportState.busy}
            onClick={() => onExport(range)}
          >
            Export range
          </button>
        </div>
      </div>

      <div className="triff-combat-export-status">
        {exportState.result ? (
          <p className="triff-combat-export-result">
            Exported {exportState.result.fileCount} log
            {exportState.result.fileCount === 1 ? "" : "s"}
            {exportState.result.characters?.length
              ? ` (${exportState.result.characters.join(", ")})`
              : ""}{" "}
            to {exportState.result.path} - {formatBytes(exportState.result.zipBytes)} zipped.
            {exportState.result.droppedFileCount
              ? ` ${exportState.result.droppedFileCount} further matching log${
                  exportState.result.droppedFileCount === 1 ? " was" : "s were"
                } left out at the file limit - narrow the time range to cover them.`
              : ""}
            {exportState.result.exceedsDiscordLimit
              ? " This is over Discord's 10 MB upload limit, so a narrower time range may be needed."
              : ""}
          </p>
        ) : null}
        {exportState.error ? (
          <p className="triff-combat-export-error">{exportState.error}</p>
        ) : null}
      </div>
    </div>
  );
```

The `<h4>Combat log export</h4>` header is dropped — the section header above the panel already renders that text from `sectionTabs`, so keeping it would print the title twice.

- [ ] **Step 2: Replace the CSS block**

In `app/src/styles.css`, replace lines 3065-3107 (from `.triff-combat-export-window {` through the closing brace of `.triff-combat-export-error`) with:

```css
.triff-combat-export {
  container-type: inline-size;
  display: flex;
  flex-direction: column;
  gap: 14px;
}

.triff-combat-export-paths {
  display: grid;
  grid-template-columns: minmax(0, 1fr) minmax(0, 1fr);
  gap: 12px;
  align-items: start;
}

/* The container is `.triff-combat-export`, the query target is its descendant
   `.triff-combat-export-paths`. An element cannot respond to a container query
   on itself, so these must be two different elements. */
@container (max-width: 620px) {
  .triff-combat-export-paths {
    grid-template-columns: minmax(0, 1fr);
  }
}

.triff-combat-export-path {
  display: flex;
  flex-direction: column;
  gap: 8px;
  border: 1px solid var(--tv-border-soft);
  padding: 12px;
}

.triff-combat-export-path h4 {
  margin: 0;
  color: var(--tv-text-strong);
  font-size: 13px;
  font-weight: 700;
}

.triff-combat-export-range {
  display: grid;
  grid-template-columns: minmax(0, 1fr) minmax(0, 1fr);
  gap: 8px;
  align-items: end;
}

.triff-combat-export-status {
  min-height: 46px;
}

.triff-combat-export-result,
.triff-combat-export-error {
  margin: 0;
  border-left: 2px solid var(--tv-accent);
  background: rgba(var(--tv-bg-rgb), 0.78);
  padding: 7px 9px;
  font-size: 11px;
  overflow-wrap: anywhere;
}

.triff-combat-export-result {
  color: var(--tv-text);
}

.triff-combat-export-error {
  border-left-color: var(--tv-danger);
  color: var(--tv-danger);
}
```

`.triff-combat-export-window` is deleted — the last-fight line is now inside `.triff-alert-summary`, which brings its own styling. The `@media (max-width: 620px)` block is deleted along with it; the container query replaces it.

- [ ] **Step 3: Verify the build**

Run: `cd app && npm run build`
Expected: `✓ built in …`, no errors.

- [ ] **Step 4: Confirm the dead breakpoint is gone**

Run: `cd app && grep -n "@media (max-width: 620px)" src/styles.css`
Expected: no output. A hit means the viewport media query survived and the container query was added alongside it rather than replacing it.

The pattern must include `@media`. Grepping for `max-width: 620px` alone also matches the *new* `@container (max-width: 620px)` rule, which is correct and expected to be there — so the bare pattern reports a false failure.

- [ ] **Step 5: Manual check on Windows**

Build and run the app, open the Combat log export tab, and confirm at the default 1120x780: the last-fight strip shows the fight window and characters (or the "no fight detected" copy with a "None" pill); the two export paths sit side by side in equal boxes; the section title appears once, not twice.

Then drag the window to its minimum size (860x600) and confirm the two paths have stacked into one column. This is the check that the container query actually fires — the old viewport query never did, and at the default size both look identical.

Then run an export and confirm the result text appears in the reserved slot without the buttons above it jumping.

- [ ] **Step 6: Commit**

```bash
git add app/src/tools/CombatLogExport.jsx app/src/styles.css
git commit -m "Reflow the combat log export panel for a full section"
```

---

### Task 5: Correct the README

**Files:**
- Modify: `README.md:68`
- Test: none

**Interfaces:**
- Consumes: the tab name from Task 3
- Produces: nothing

- [ ] **Step 1: Update the location sentence**

`README.md:68` currently reads:

```markdown
The alerts panel can package the game logs covering a fight into a single zip, ready to upload to Discord for [eve-intel](https://github.com/guarzo/eve-intel) to build an after-action report from.
```

Replace with:

```markdown
The **Combat log export** panel can package the game logs covering a fight into a single zip, ready to upload to Discord for [eve-intel](https://github.com/guarzo/eve-intel) to build an after-action report from.
```

Leave line 22 alone — that feature bullet does not name a location and is still accurate.

- [ ] **Step 2: Confirm no other stale references**

Run: `grep -n -i "alerts panel" README.md docs/*.md`
Expected: no output. Any hit is another sentence pointing at the old location; update it the same way.

- [ ] **Step 3: Commit**

```bash
git add README.md
git commit -m "Point the README at the combat log export tab"
```

---

## Final verification

After all five tasks, on Windows:

- [ ] Build the release exe and launch it. Confirm the embedded UI loads (the real settings panel, not the "missing overlay" page).
- [ ] Walk every section tab once — Task 1 moved `Field` out of the host file, and `Field` is used by Profile settings and Preview layout as well, so a mistake there shows up outside this feature.
- [ ] At 860x600: the nav scrolls, all eight tabs reachable, export paths stacked.
- [ ] Export a real fight and confirm the zip is written and the result line reports a plausible size.

Run: `git status --short`
Expected: clean. If `app/package-lock.json` appears, revert it — see Global Constraints.

---

## Follow-on: quick 1h/2h range buttons

Not part of Tasks 1-5. Added afterwards, once all five had landed and been
reviewed, under its own bounded design. Recorded here so the branch and this
document agree; Task 4's steps above are left exactly as executed rather than
back-filled, because a reader replaying Task 4 should get the commit that
actually exists.

**Delivered** in `ec5869f`, touching `app/src/tools/CombatLogExport.jsx` and
`app/src/styles.css` only:

- A "Quick range:" row in the Time range card with *Last 1 hour* and *Last 2
  hours*. Both call `onRangeChange` to populate the two UTC fields and **start
  no export** — the existing Export range button stays the only way to run one.
  Both disable while `exportState.busy`.
- `formatUtcInput(date)` emits `YYYY-MM-DD HH:MM` from `getUTC*` accessors,
  matching the field placeholder and the native
  `DateTime.TryParse(InvariantCulture, AssumeUniversal)` parser — so no native
  change was needed.
- `quickRange(hours)` rounds the window end up to the next minute, because the
  fields are minute-granular and truncating "now" would drop the tail of a
  fight that just ended.
- The two path headings moved from `<h4>` to `<h3>` (they sit under the section
  `<h2>`), with `.triff-combat-export-path h4` renamed to match.

**Verification actually performed:** `npm run build` passed, and the window
arithmetic was exercised directly through node — mid-minute round-up,
exact-minute boundary, and rollover across midnight, month, and year all
produced correct UTC windows. The Windows checks above still apply, plus:
click each quick button and confirm the fields fill with correct UTC values,
then run one export over a quick range to prove the format parses end to end.

