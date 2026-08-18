# Navigation Reorganisation: Combat Logs Tab — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Promote combat log export/upload from a buried section inside TriffView settings to its own top-level tab, and reclaim the topbar width that a fifth tab requires.

**Architecture:** Combat-log state and its native-message subscription move into a `useCombatLogs()` hook called from `App.jsx`, so the subscription stays mounted for the application's lifetime and an in-flight upload survives a tab switch. The presentational `CombatLogExport` component is reused almost unchanged behind a new `CombatLogs` tab container. The topbar sheds its stale subtitle and its 178px theme `<select>` in favour of a compact swatch button with an in-page listbox popup.

**Tech Stack:** React 18 + Vite (`app/`), C# / WPF + WinForms + WebView2 (`native/`). No JavaScript test framework exists and adding one is out of scope.

**Spec:** `docs/superpowers/specs/2026-08-17-nav-reorg-combat-logs-tab-design.md`

## Global Constraints

Every task's requirements implicitly include this section.

- **Fork-only.** Combat log export does not exist on `origin/main` or `upstream/main`. This branch is based on `fork/main` and must never be offered as an upstream PR.
- **No JavaScript test framework.** `app/package.json` has no `test` script and empty `devDependencies`. Do not add one. Automated verification is `cd app && npm run build` from the worktree, in WSL. Everything else is manual on Windows.
- **This project cannot be built or run from WSL.** Any claim about runtime behaviour is a claim until exercised on Windows. Show build output rather than asserting success, and state plainly which checks were not run.
- **Repo scripts do not work from a worktree.** `scripts/build-native.ps1:4` resolves the root exactly one level up with no upward walk. Use the explicit-SDK invocation in Task 4, Step 4.
- **The nav id is exactly `combat-logs`**, identical in `app/src/App.jsx` `NAV_ITEMS` and in `OpenTool(...)`. `standalone:navigate` is not validated native-side; a mismatch fails silently with no error anywhere.
- **The README heading `### Combat log export` must not change.** `docs/DIAGNOSTICS.md:35` links `../README.md#combat-log-export` and the anchor derives from the heading text.
- **The new tab needs both wrapper levels**: `.triffview-settings` outer (control font-size scoping, `styles.css:3916`) and `.triffview-section-content` inner (`overflow: auto`, `styles.css:2235`). `.triffview-settings` alone is `overflow: hidden` (`styles.css:1980`) and will clip silently.
- **Preserve the export `cancelled` handling.** Native posts `{type:"triffview:combat-log-export", cancelled:true}` with no `result` (`TriffViewSubsystem.cs:1696`). Keying on `message.result` being present leaves the export buttons disabled forever after a dialog cancel.
- **Do not edit anything under `docs/superpowers/specs/`.** Those are a historical record.

**Task order:** 1 → 2 → 3 → 4 → 5. Task 3 depends on Task 1's files existing. Task 2 is independent of the others and may be done at any point. Task 4 depends on Task 1's `NAV_ITEMS` id.

## Prerequisite for every Windows check

**A worktree has no `native/Assets/overlay-dist.zip`.** The `EmbeddedResource` is conditional (`native/TriffView.csproj:39-41`), so the native project builds fine without it — and then serves the "missing overlay" page (`native/MainWindow.xaml.cs:414-419`) instead of the real UI, which invalidates every manual check in this plan.

Pick one before running any Windows verification:

- **Preferred — run against a live dev server.** Build nothing: start Vite in WSL (`cd app && npm run dev`, port 5178) and launch the app with `--dev`, or set `TRIFFVIEW_DEV_URL`. This picks up web changes without a rebuild, which suits Tasks 1-3.
- **Otherwise — supply the zip.** Build the web UI (`cd app && npm run build`), zip `app/dist` to `native/Assets/overlay-dist.zip`, and rebuild. Confirm it took by checking the DLL roughly doubles in size, or with:

```powershell
[Reflection.Assembly]::LoadFrom('<path>\TriffView.dll').GetManifestResourceNames()
```

Expect `TriffView.Assets.overlay-dist.zip` in the output.

If you see the "missing overlay" page, stop — nothing observed after that point is evidence about this change.

---

### Task 1: Move combat logs to its own top-level tab

**Files:**
- Create: `app/src/tools/useCombatLogs.js`
- Create: `app/src/tools/CombatLogs.jsx`
- Modify: `app/src/App.jsx` (NAV_ITEMS at :13-18, hook call, render block)
- Modify: `app/src/tools/TriffViewSettings.jsx` (import at :5, state at :1034-1046, `lastFight` at :1052, `sectionTabs` at :1083-1092, senders at :1222-1248, receive branches at :1302-1378, render block at :1885-1901)

**Interfaces:**
- Consumes: `postNative`, `onNativeMessage` from `../nativeBridge.js`; native message types `triffview:state`, `triffview:combat-log-export`, `triffview:combat-log-webhook`, `triffview:combat-log-upload`, `triffview:error`; the existing `CombatLogExport` component (unchanged in this task).
- Produces: `useCombatLogs()` returning exactly `{ lastFight, exportState, range, setRange, exportCombatLogs, uploadCombatLogs, webhookState, saveWebhook, clearWebhook, testWebhook, uploadState }`. `CombatLogs({ combatLogs, onOpenAlerts })` default export, where `combatLogs` is that hook's return value.

- [ ] **Step 1: Create `app/src/tools/useCombatLogs.js`**

Move the combat-log state, senders, and native-message subscription out of `TriffViewSettings.jsx`. `combatLogWebhookAction` merges into `webhookState.action` at the return, matching today's `{...combatLogWebhook, action: combatLogWebhookAction}` shape.

Note the sender parameters are named `explicitRange`, not `range`, so they do not shadow the `range` state in the same scope.

```javascript
import { useEffect, useState } from "react";
import { onNativeMessage, postNative } from "../nativeBridge.js";

// Local copy of TriffViewSettings.jsx's module-level `send` helper (:123-125),
// so this hook's native-message surface stays self-contained.
function send(type, payload = {}) {
  postNative({ type, ...payload });
}

export function useCombatLogs() {
  const [lastFight, setLastFight] = useState(null);
  const [exportState, setExportState] = useState({ result: null, error: "", busy: false });
  const [range, setRange] = useState({ from: "", to: "" });
  const [webhook, setWebhook] = useState({
    configured: false,
    description: "",
    testResult: null,
    error: "",
  });
  // 'save' | 'clear' | 'test' | null - which webhook action is in flight, so the
  // three buttons share one busy flag but CombatLogExport can still tell a
  // successful *save* apart from a successful clear or test.
  const [webhookAction, setWebhookAction] = useState(null);
  const [uploadState, setUploadState] = useState({ result: null, error: "", busy: false });

  // Omitting the range tells the native side to use the last detected fight.
  function exportCombatLogs(explicitRange) {
    setExportState({ result: null, error: "", busy: true });
    send(
      "triffview:export-combat-logs",
      explicitRange ? { fromUtc: explicitRange.from, toUtc: explicitRange.to } : {}
    );
  }

  // Same "omit the range for the last fight" convention as exportCombatLogs.
  function uploadCombatLogs(explicitRange) {
    setUploadState({ result: null, error: "", busy: true });
    send(
      "triffview:upload-combat-logs",
      explicitRange ? { fromUtc: explicitRange.from, toUtc: explicitRange.to } : {}
    );
  }

  function saveWebhook(url) {
    setWebhookAction("save");
    setWebhook((current) => ({ ...current, error: "" }));
    send("triffview:set-combat-log-webhook", { url });
  }

  function clearWebhook() {
    setWebhookAction("clear");
    setWebhook((current) => ({ ...current, error: "" }));
    send("triffview:clear-combat-log-webhook");
  }

  function testWebhook() {
    setWebhookAction("test");
    setWebhook((current) => ({ ...current, error: "" }));
    send("triffview:test-combat-log-webhook");
  }

  useEffect(() => {
    const unsubscribe = onNativeMessage((message) => {
      if (message?.type === "triffview:state") {
        setLastFight(message.lastFight || null);
        // configured/description are refreshed on every periodic state post;
        // testResult is left alone so a "Send test" outcome isn't wiped out by
        // the next routine post before the user has read it.
        const nextWebhook = message.combatLogWebhook || {};
        setWebhook((current) => ({
          ...current,
          configured: Boolean(nextWebhook.configured),
          description: nextWebhook.description || "",
        }));
      }

      if (message?.type === "triffview:combat-log-export") {
        // Cancelling the save dialog posts { cancelled: true } with no `result`
        // (TriffViewSubsystem.cs:1696). Keying this off `message.result` being
        // present (instead of the message type) would leave export buttons
        // disabled forever after a cancel.
        setExportState({ result: message.result || null, error: "", busy: false });
      }

      if (message?.type === "triffview:error" && message.action === "export-combat-logs") {
        setExportState({ result: null, error: message.message || "Export failed.", busy: false });
      }

      if (message?.type === "triffview:combat-log-webhook") {
        setWebhook({
          configured: Boolean(message.configured),
          description: message.description || "",
          testResult: message.testResult || null,
          error: "",
        });
        setWebhookAction(null);
      }

      if (
        message?.type === "triffview:error"
        && [
          "set-combat-log-webhook",
          "clear-combat-log-webhook",
          "test-combat-log-webhook",
        ].includes(message.action)
      ) {
        setWebhook((current) => ({
          ...current,
          error: message.message || "Webhook action failed.",
        }));
        setWebhookAction(null);
      }

      if (message?.type === "triffview:combat-log-upload") {
        // No cancelled branch: the upload path has no dialog and no user-facing
        // cancel, so the native side never sends one. A timeout arrives here as
        // a result with succeeded === false.
        setUploadState({
          result: message.result || null,
          error: "",
          busy: false,
        });
      }

      if (message?.type === "triffview:error" && message.action === "upload-combat-logs") {
        setUploadState({ result: null, error: message.message || "Upload failed.", busy: false });
      }
    });

    return () => {
      unsubscribe();
    };
  }, []);

  useEffect(() => {
    send("triffview:get-state");
  }, []);

  return {
    lastFight,
    exportState,
    range,
    setRange,
    exportCombatLogs,
    uploadCombatLogs,
    webhookState: { ...webhook, action: webhookAction },
    saveWebhook,
    clearWebhook,
    testWebhook,
    uploadState,
  };
}
```

The hook's own `triffview:get-state` duplicates the one `TriffViewSettings` already sends on mount (`TriffViewSettings.jsx:1380-1382`), so with the TriffView tab active at startup the app requests state twice and native posts twice. That is intentional and harmless: the hook cannot assume `TriffViewSettings` is mounted, `PostState(force: true)` is cheap, and subsequent periodic posts are deduped by payload (`TriffViewSubsystem.cs:2520`). Do not "tidy" it by deleting either call.

- [ ] **Step 2: Create `app/src/tools/CombatLogs.jsx`**

Both wrapper levels matter: `.triffview-settings` (`styles.css:1980`) is `overflow: hidden` and only supplies control font-size scoping, while `.triffview-section-content` (`styles.css:2235`) is the one that is `overflow: auto` and actually scrolls. Dropping either clips the tab's content with no scrollbar.

`onOpenAlerts` is threaded through to `CombatLogExport` here even though that component does not read the prop until Task 3. An unused prop is harmless, and passing it now avoids a second edit to this file.

```jsx
import React from "react";
import CombatLogExport from "./CombatLogExport.jsx";

function CombatLogs({ combatLogs, onOpenAlerts }) {
  const {
    lastFight,
    exportState,
    range,
    setRange,
    exportCombatLogs,
    uploadCombatLogs,
    webhookState,
    saveWebhook,
    clearWebhook,
    testWebhook,
    uploadState,
  } = combatLogs;

  return (
    <div className="triffview-settings">
      <div className="triffview-section-content" data-hud-scroll>
        <header className="triffview-section-header">
          <h2>Combat logs</h2>
        </header>
        <div className="triffview-panel">
          <CombatLogExport
            lastFight={lastFight}
            exportState={exportState}
            range={range}
            onRangeChange={setRange}
            onExport={exportCombatLogs}
            webhookState={webhookState}
            onSaveWebhook={saveWebhook}
            onClearWebhook={clearWebhook}
            onTestWebhook={testWebhook}
            uploadState={uploadState}
            onUpload={uploadCombatLogs}
            onOpenAlerts={onOpenAlerts}
          />
        </div>
      </div>
    </div>
  );
}

export default CombatLogs;
```

- [ ] **Step 3: Wire `CombatLogs` into `app/src/App.jsx`**

`CombatLogs` is a **static** import, unlike `EveSettings`/`TriffFleets`/`TriffSkills` — its hook must be mounted for the app's lifetime, which rules out lazy-loading the tab that owns it.

```javascript
import TriffViewSettings from "./tools/TriffViewSettings.jsx";
import CombatLogs from "./tools/CombatLogs.jsx";
import { useCombatLogs } from "./tools/useCombatLogs.js";
```

```javascript
const NAV_ITEMS = [
  { id: "triffview", label: "TriffView" },
  { id: "combat-logs", label: "Combat Logs" },
  { id: "eve-settings", label: "EVE Settings" },
  { id: "fleet-manager", label: "Fleet Manager" },
  { id: "skill-planner", label: "Skill Planner" },
];
```

Add the hook call at the top of `App`, alongside the other top-level state:

```javascript
  const [activeTool, setActiveTool] = useState("triffview");
  const combatLogs = useCombatLogs();
```

Add the render branch alongside the other tabs:

```jsx
        {activeTool === "triffview" ? <TriffViewSettings open /> : null}
        {activeTool === "combat-logs" ? <CombatLogs combatLogs={combatLogs} /> : null}
```

`onOpenAlerts` is deliberately omitted from this call site — Task 3 supplies it.

- [ ] **Step 4: Strip the moved code out of `app/src/tools/TriffViewSettings.jsx`**

Delete the now-unused import:

```diff
-import CombatLogExport from "./CombatLogExport.jsx";
```

Delete the five `useState` declarations at :1034-1046 (`combatLogExport`, `combatLogRange`, `combatLogWebhook`, `combatLogWebhookAction`, `combatLogUpload`) and the comment above `combatLogWebhookAction`.

Delete the derived constant at :1052 (`const lastFight = state.lastFight || null;`). Leave `lastFight: null` in `EMPTY_STATE` (:19) untouched — harmless, and still part of the state shape the native side posts.

Delete the `combat-logs` entry from `sectionTabs`:

```diff
   const sectionTabs = [
     ["profile", "Profile settings"],
     ["layout", "Preview layout"],
     ["colors", "Color settings"],
     ["alerts", "Alerts"],
-    ["combat-logs", "Combat log export"],
     ["hotkeys", "Character Hotkeys"],
     ["cycles", "Cycle Groups and Hotkeys"],
     ["clients", "Client management"],
   ];
```

Delete the five sender functions at :1222-1248 (`exportCombatLogs`, `uploadCombatLogs`, `saveCombatLogWebhook`, `clearCombatLogWebhook`, `testCombatLogWebhook`) and the `// Omitting the range...` comment above them.

Inside the `onNativeMessage` callback (:1302-1378), delete: the `combatLogWebhook` slice of the `triffview:state` branch (:1318-1323 — **leave the `setState(...)` call intact**), and the whole `if` blocks for `triffview:combat-log-export`, `triffview:combat-log-webhook`, `triffview:combat-log-upload`, and the three `triffview:error` branches keyed on `export-combat-logs`, the three webhook actions, and `upload-combat-logs`.

Delete the render block at :1885-1901:

```diff
-        {activeSection === "combat-logs" ? (
-        <div className="triffview-panel">
-          <CombatLogExport
-            lastFight={lastFight}
-            exportState={combatLogExport}
-            range={combatLogRange}
-            onRangeChange={setCombatLogRange}
-            onExport={exportCombatLogs}
-            webhookState={{ ...combatLogWebhook, action: combatLogWebhookAction }}
-            onSaveWebhook={saveCombatLogWebhook}
-            onClearWebhook={clearCombatLogWebhook}
-            onTestWebhook={testCombatLogWebhook}
-            uploadState={combatLogUpload}
-            onUpload={uploadCombatLogs}
-          />
-        </div>
-        ) : null}
```

- [ ] **Step 5: Build**

```bash
cd app && npm run build
```

Expected: build succeeds. `npm run build` is `vite build` only — there is no lint or type tooling (`app/package.json:7-18`), so it catches unresolved imports and syntax errors but **cannot** tell you an unused variable remains. Check the removals explicitly:

```bash
grep -n "CombatLogExport\|combatLogExport\|combatLogRange\|combatLogWebhook\|combatLogUpload\|lastFight" app/src/tools/TriffViewSettings.jsx
```

Expected: exactly one hit — `lastFight: null` in `EMPTY_STATE` (:19). Any other hit is a leftover.

- [ ] **Step 6: Manual verification on Windows**

- TriffView's rail shows seven sections, with no "Combat log export" entry.
- A "Combat Logs" tab appears second in the top nav; opening it shows the last detected fight (or the empty state) and the webhook status.
- Switch away from Combat Logs and back — `lastFight` and webhook status are still populated.
- **Start an upload, switch to the TriffView tab, switch back — the result still arrives and the buttons re-enable.** This is the case the hoisted hook exists for; if it regresses the failure is silent.
- **Start an upload, then tray → Reload UI.** Expected: the result is lost and a second upload is swallowed. This confirms the *known* bound documented in the spec; it is not a bug to fix here.
- Run an export and cancel the native save dialog — the export button re-enables rather than staying disabled.
- Run an export and a Discord upload to completion from the new tab.
- Shrink the window vertically until the content overflows — the tab scrolls, and controls render at 13px rather than the browser default.

- [ ] **Step 7: Commit**

```bash
git add app/src/tools/useCombatLogs.js app/src/tools/CombatLogs.jsx app/src/App.jsx app/src/tools/TriffViewSettings.jsx
git commit -m "feat(app): promote combat logs to a top-level tab"
```

---

### Task 2: Reclaim topbar width

**Files:**
- Modify: `app/src/App.jsx` (brand subtitle at :220, theme picker block at :221-239, new `ThemePicker` component)
- Modify: `app/src/styles.css` (`.triffview-theme-picker` :542-547, `.triffview-theme-select` :564-583)

**Interfaces:**
- Consumes: `GUI_THEMES` (`App.jsx:20-29`), `setThemeId`, `activeTheme` — all already in `App`.
- Produces: `function ThemePicker({ themes, activeTheme, onChange })` in `App.jsx`, above `App`. `onChange(themeId)` replaces the current `setThemeId(event.target.value)` call. No new native message types; theme persistence (`localStorage` under `THEME_STORAGE_KEY`) is untouched.

- [ ] **Step 1: Delete the brand subtitle**

Do **not** delete the CSS rule `.triffview-standalone-topbar span` (`styles.css:537`) — it also styles `.triffview-update-copy`.

```diff
   <div className="triffview-brand-block">
     <strong>TriffView</strong>
-    <span>Previews, fleets, and EVE settings</span>
```

- [ ] **Step 2: Add the `ThemePicker` component above `App`**

Insert after the `UpdateNotice` component, before `export default function App()`:

```jsx
function ThemePicker({ themes, activeTheme, onChange }) {
  const [open, setOpen] = useState(false);
  const [highlightedId, setHighlightedId] = useState(activeTheme.id);
  const buttonRef = useRef(null);
  const listRef = useRef(null);
  const rootRef = useRef(null);

  useEffect(() => {
    if (!open) return undefined;
    setHighlightedId(activeTheme.id);

    function onDocPointerDown(event) {
      if (rootRef.current && !rootRef.current.contains(event.target)) {
        setOpen(false);
      }
    }
    document.addEventListener("pointerdown", onDocPointerDown, true);
    return () => document.removeEventListener("pointerdown", onDocPointerDown, true);
  }, [open, activeTheme.id]);

  useEffect(() => {
    if (!open) return;
    const node = listRef.current?.querySelector(`[data-theme-id="${highlightedId}"]`);
    node?.scrollIntoView({ block: "nearest" });
  }, [open, highlightedId]);

  function closeAndReturnFocus() {
    setOpen(false);
    buttonRef.current?.focus();
  }

  function moveHighlight(delta) {
    const ids = themes.map((theme) => theme.id);
    const index = ids.indexOf(highlightedId);
    const next = (index + delta + ids.length) % ids.length;
    setHighlightedId(ids[next]);
  }

  function onButtonKeyDown(event) {
    if (event.key === "ArrowDown" || event.key === "Enter" || event.key === " ") {
      event.preventDefault();
      setOpen(true);
    }
  }

  function onListRef(node) {
    listRef.current = node;
    node?.focus();
  }

  function onListKeyDown(event) {
    if (event.key === "ArrowDown") {
      event.preventDefault();
      moveHighlight(1);
    } else if (event.key === "ArrowUp") {
      event.preventDefault();
      moveHighlight(-1);
    } else if (event.key === "Home") {
      event.preventDefault();
      setHighlightedId(themes[0].id);
    } else if (event.key === "End") {
      event.preventDefault();
      setHighlightedId(themes[themes.length - 1].id);
    } else if (event.key === "Enter" || event.key === " ") {
      event.preventDefault();
      onChange(highlightedId);
      closeAndReturnFocus();
    } else if (event.key === "Escape") {
      event.preventDefault();
      closeAndReturnFocus();
    } else if (event.key === "Tab") {
      setOpen(false);
    }
  }

  return (
    <div className="triffview-theme-picker" ref={rootRef}>
      <button
        type="button"
        ref={buttonRef}
        className="triffview-theme-button"
        aria-haspopup="listbox"
        aria-expanded={open}
        aria-label={`GUI theme: ${activeTheme.name}`}
        onClick={() => setOpen((value) => !value)}
        onKeyDown={onButtonKeyDown}
      >
        <span className="triffview-theme-swatches" aria-hidden="true">
          {activeTheme.swatches.map((color) => (
            <i key={color} style={{ backgroundColor: color }} />
          ))}
        </span>
        <span className="triffview-theme-caret" aria-hidden="true">
          ▾
        </span>
      </button>
      {open ? (
        <ul
          className="triffview-theme-listbox"
          role="listbox"
          ref={onListRef}
          aria-label="GUI theme"
          aria-activedescendant={`triffview-theme-option-${highlightedId}`}
          tabIndex={-1}
          onKeyDown={onListKeyDown}
        >
          {themes.map((theme) => (
            <li
              key={theme.id}
              id={`triffview-theme-option-${theme.id}`}
              data-theme-id={theme.id}
              role="option"
              aria-selected={theme.id === activeTheme.id}
              className={theme.id === highlightedId ? "is-highlighted" : ""}
              onMouseEnter={() => setHighlightedId(theme.id)}
              onClick={() => {
                onChange(theme.id);
                closeAndReturnFocus();
              }}
            >
              <span className="triffview-theme-swatches" aria-hidden="true">
                {theme.swatches.map((color) => (
                  <i key={color} style={{ backgroundColor: color }} />
                ))}
              </span>
              <span>{theme.name}</span>
            </li>
          ))}
        </ul>
      ) : null}
    </div>
  );
}
```

- [ ] **Step 3: Replace the `<label>` block with `<ThemePicker>`**

Replace the whole `<label className="triffview-theme-picker">…</label>` block (`App.jsx:221-239`) with:

```jsx
<ThemePicker themes={GUI_THEMES} activeTheme={activeTheme} onChange={setThemeId} />
```

- [ ] **Step 4: Replace the theme-select CSS**

Keep `.triffview-theme-swatches` (`:549-556`) and `.triffview-theme-swatches i` (`:557-562`) untouched — both the button and the list rows reuse them. Update `.triffview-theme-picker` to be the positioning root and replace `.triffview-theme-select` and its `:hover/:focus`:

```css
.triffview-theme-picker {
  position: relative;
  display: inline-flex;
  min-width: 0;
  align-items: center;
}

.triffview-theme-button {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  height: 28px;
  border: 1px solid var(--tv-border);
  border-radius: 0;
  background: var(--tv-control-bg-alt);
  color: var(--tv-text);
  padding: 0 8px;
  font: inherit;
  cursor: pointer;
}

/* MUST out-specify `.triffview-standalone-topbar button { min-width: 112px }`
   (styles.css:592-600), which is a (0,1,1) selector and would otherwise force
   this "compact" control to 112px and wipe out the width saving this whole
   task exists for. This selector is (0,2,1). */
.triffview-standalone-topbar .triffview-theme-button {
  min-width: 0;
}

.triffview-theme-button:hover,
.triffview-theme-button[aria-expanded="true"] {
  border-color: var(--tv-accent);
  background: var(--tv-control-active-bg);
  color: var(--tv-text-active);
}

.triffview-theme-button:focus-visible {
  outline: 2px solid var(--tv-accent);
  outline-offset: 1px;
}

.triffview-theme-caret {
  font-size: 10px;
  color: var(--tv-muted);
}

.triffview-theme-listbox {
  position: absolute;
  top: calc(100% + 4px);
  left: 0;
  z-index: 100000;
  margin: 0;
  padding: 4px 0;
  list-style: none;
  min-width: 200px;
  max-height: 320px;
  overflow: auto;
  border: 1px solid var(--tv-accent);
  background: var(--tv-bg);
  box-shadow:
    0 0 0 1px rgba(var(--tv-accent-rgb), 0.2),
    0 10px 28px rgba(var(--tv-shadow-rgb), 0.72);
}

.triffview-theme-listbox li {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 6px 10px;
  font-size: 13px;
  color: var(--tv-text);
  cursor: pointer;
}

.triffview-theme-listbox li.is-highlighted,
.triffview-theme-listbox li:hover {
  background: var(--tv-accent-bg);
  color: var(--tv-text-active);
}
```

Conventions followed: `border-radius: 0`, `var(--tv-*)` custom properties, no external dependencies, and the same `z-index`/box-shadow vocabulary as `.hud-select-popup` (`styles.css:751+`). The popup renders inside the WebView document, so it must be positioned in-page rather than relying on an OS-level popup that can escape the window.

Both `--tv-accent-rgb` and `--tv-shadow-rgb` are confirmed present in `styles.css` (45 and 8 existing uses respectively), so the `rgba(var(--…), …)` calls above resolve.

- [ ] **Step 5: Note the dormant select-popup path (no code change)**

`openSelectPopup` (`nativeBridge.js:327`) requires `select.closest(".hud-panel")` (`:332`) and returns early otherwise. Nothing in `App.jsx`'s tree has a `.hud-panel` ancestor, so this path was already unreachable for the old `<select>` — removing it removes a latent trap rather than creating one. The new `<button>` is already covered by `HUD_INTERACTIVE_SELECTOR` (`nativeBridge.js:91-102`, first entry `"button"`), so no change is needed there.

**A review raised, and this plan rejects, the claim that the picker's keyboard model cannot work.** The argument was that `triffHudDispatchKey` (`nativeBridge.js:1073`) discards non-text-control keys, so arrows/Enter/Escape would never reach a focused button. That path is dormant: `triffHudDispatchKey` is only invoked by `ForwardKeyToHud` (`MainWindow.xaml.cs:627-640`), which is called only from `InputOverlayWindow.xaml.cs:224-252`; that window is constructed (`MainWindow.xaml.cs:383`), given regions (`:778`), and closed (`:1175`), but `Show()` is never called on it and no `Show`/`Visibility` appears anywhere in `InputOverlayWindow.xaml.cs`. A window that is never displayed never takes focus, so keys reach WebView2 directly and React's `onKeyDown` fires normally.

If the input overlay is ever made visible, this reasoning expires and the keyboard model must be revisited.

- [ ] **Step 6: Build**

```bash
cd app && npm run build
```

- [ ] **Step 7: Manual verification on Windows**

- The button shows the current theme's three swatches plus the caret. Click, or Enter/Space/ArrowDown while focused, opens the popup listing all 8 themes by swatches and name.
- Up/Down moves the highlight (wrapping), Home/End jump to first/last, Enter/Space applies the theme (topbar and native window chrome recolor), Escape closes without changing it.
- Clicking outside or pressing Escape closes the popup and returns focus to the button; `aria-expanded` toggles; a visible focus ring appears.
- The brand block no longer shows the subtitle, and the update pill's text still renders correctly — proving `styles.css:537` survived Step 1.
- **Five tabs fit at the 1120px default and at the 860px minimum.**
- **The same, with an update pill visible.** The spec's arithmetic puts this at ~1170px, over the 1120px default. If anything clips, apply the spec's remedy order: reduce `.triffview-standalone-topbar button`'s `min-width: 112px` (`styles.css:593`); then let `.triffview-update-pill`'s `max-width: 390px` shrink; only last, wrap the pill to a second row. **Record whether this was needed** — the ~340px saving is arithmetic, not a measurement.

- [ ] **Step 8: Commit**

```bash
git add app/src/App.jsx app/src/styles.css
git commit -m "refactor(topbar): replace theme select with compact popup picker"
```

---

### Task 3: Cross-tab navigation to Alerts

**Files:**
- Modify: `app/src/tools/CombatLogExport.jsx` (prop list at :56-68, empty state in the `triff-alert-summary` block)
- Modify: `app/src/tools/TriffViewSettings.jsx` (component signature, new effect after the guide effect at :1102-1107)
- Modify: `app/src/App.jsx` (`triffViewSection` state, `openAlertsInTriffView`, render block)

**Interfaces:**
- Consumes: `CombatLogs({ combatLogs, onOpenAlerts })` from Task 1 (already threads `onOpenAlerts` through to `CombatLogExport`).
- Produces: `TriffViewSettings({ open, initialSection = null, onInitialSectionApplied })`; `CombatLogExport` gains an optional 12th prop `onOpenAlerts`; `App.openAlertsInTriffView()` sets `triffViewSection = "alerts"` then `activeTool = "triffview"`.

- [ ] **Step 1: Add the empty-state button in `CombatLogExport`**

Add the 12th prop and render the button only when there is no last fight *and* a handler was supplied, so the component stays usable standalone:

```jsx
function CombatLogExport({
  lastFight,
  exportState,
  range,
  onRangeChange,
  onExport,
  webhookState,
  onSaveWebhook,
  onClearWebhook,
  onTestWebhook,
  uploadState,
  onUpload,
  onOpenAlerts,
}) {
```

```jsx
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
        {!lastFight && onOpenAlerts ? (
          <button type="button" onClick={onOpenAlerts}>
            Open Alerts settings
          </button>
        ) : null}
      </div>
      <span className={lastFight ? "triff-alert-status is-on" : "triff-alert-status"}>
        {lastFight ? "Ready" : "None"}
      </span>
    </div>
```

No new CSS: `.triff-alert-summary > div` is already a `flex-direction: column` stack (`styles.css:2848`), and the button inherits its appearance from the section-scoped rule `.triffview-settings :is(button, …)` (`styles.css:3916`) that every other button in this component relies on.

- [ ] **Step 2: Confirm `CombatLogs` already forwards the prop**

Task 1 created `CombatLogs.jsx` with `onOpenAlerts` destructured and passed to `<CombatLogExport>`. Verify it is there; no edit expected.

```bash
grep -n "onOpenAlerts" app/src/tools/CombatLogs.jsx
```

Expected: two matches (the destructured prop and the JSX attribute).

- [ ] **Step 3: Add the nudge plumbing to `TriffViewSettings`**

```jsx
function TriffViewSettings({ open = true, initialSection = null, onInitialSectionApplied }) {
```

**The nudge must wait for native state to arrive, not just for `guideCompleted`.** `EMPTY_STATE.guideCompleted` is `true` (`TriffViewSettings.jsx:11`), and real state arrives asynchronously (`:1302-1314`). Gating on `state.guideCompleted` alone would let the nudge fire and be consumed on the very first render — before the component learns onboarding is incomplete — after which the guide opens over the top with nothing left to apply.

Add a "have we heard from native yet" flag alongside the existing state:

```jsx
  const [stateReceived, setStateReceived] = useState(false);
```

Set it inside the existing `triffview:state` branch of the `onNativeMessage` callback (`:1304`), next to the `setState({...})` call:

```jsx
      if (message?.type === "triffview:state") {
        setStateReceived(true);
        setState({
          // ...unchanged
```

Then add this effect immediately after the existing guide-prompt effect (`:1102-1107`):

```jsx
  // Nudges the rail to a specific section on request (e.g. "Open Alerts
  // settings" from the Combat Logs tab). Two guards, both load-bearing:
  //
  //   stateReceived - EMPTY_STATE.guideCompleted is `true`, so without this the
  //     nudge would fire and be consumed on the first render, before native
  //     state says whether onboarding is actually complete.
  //   state.guideCompleted - applying a nudge underneath the guide would yank
  //     the user out of onboarding before they chose to skip or finish.
  //
  // `activeSection` is deliberately absent from the dependency array: this must
  // react only to a *new* nudge from the parent, never to the user's own later
  // rail clicks.
  useEffect(() => {
    if (!initialSection || !stateReceived || !state.guideCompleted) return;
    setActiveSection(initialSection);
    onInitialSectionApplied?.();
  }, [initialSection, stateReceived, state.guideCompleted]);
```

`onInitialSectionApplied` is load-bearing, not ceremony: it lets the parent reset `triffViewSection` to `null` once consumed, which is what makes a *second* click of the control work. Without it, `initialSection` would already be `"alerts"`, the dependency would not change, and the effect would not re-run.

Entering the tab normally leaves `activeSection` alone, because `initialSection` is `null` and the effect returns immediately.

If the guide is showing when the nudge arrives, the effect is a no-op and the nudge stays unconsumed. When the guide completes, `state.guideCompleted` flips, the dependency changes, and the nudge applies then — `completeGuide()` sets the section to `"profile"` first (`:1211-1214`), and this effect moves it to Alerts once the resulting state post lands.

- [ ] **Step 4: Wire `App.jsx`**

```jsx
  const [activeTool, setActiveTool] = useState("triffview");
  const [triffViewSection, setTriffViewSection] = useState(null);
```

```jsx
  function openAlertsInTriffView() {
    setTriffViewSection("alerts");
    setActiveTool("triffview");
  }
```

Update both render branches:

```jsx
      {activeTool === "triffview" ? (
        <TriffViewSettings
          open
          initialSection={triffViewSection}
          onInitialSectionApplied={() => setTriffViewSection(null)}
        />
      ) : null}
      {activeTool === "combat-logs" ? (
        <CombatLogs combatLogs={combatLogs} onOpenAlerts={openAlertsInTriffView} />
      ) : null}
```

- [ ] **Step 5: Build**

```bash
cd app && npm run build
```

- [ ] **Step 6: Manual verification on Windows**

- With no fight detected, "Open Alerts settings" appears in the Combat Logs empty state. With a fight detected, it does not.
- Clicking it switches to TriffView and lands on the Alerts section.
- From Alerts, click any other rail section — it switches freely with no snap-back.
- Return to Combat Logs and click the control **again** — it re-nudges to Alerts. (This is the case `onInitialSectionApplied` exists for.)
- With an incomplete guide, click the control: the guide shows first, and on finishing or skipping it, the rail lands on Alerts.

- [ ] **Step 7: Commit**

```bash
git add app/src/tools/CombatLogExport.jsx app/src/tools/TriffViewSettings.jsx app/src/App.jsx
git commit -m "feat(app): add Open Alerts shortcut from the Combat Logs tab"
```

---

### Task 4: Add the tray menu entry

**Files:**
- Modify: `native/MainWindow.xaml.cs` (`InitializeTray`, item declarations at :261-264, menu assembly at :279-284)

**Interfaces:**
- Consumes: `OpenTool(string tool)` at `:804-808`, which posts `{ type: "standalone:navigate", tool }` with no validation of `tool`.
- Produces: nothing new — reuses the existing `standalone:navigate` message type. The string `"combat-logs"` must exactly match the `NAV_ITEMS` id from Task 1.

- [ ] **Step 1: Confirm the tray menu is built once, not rebuilt per theme**

`InitializeTray()` is called once from the constructor and is the only place items are added (`:279-294`). `CreateTrayMenu(_guiTheme)` (`:309-322`) only constructs an empty, theme-coloured `ContextMenuStrip` — it adds no items. On a theme change the code calls `ApplyTrayMenuTheme`, which recolours in place and does not touch `menu.Items`.

So the new item is added exactly once, in `InitializeTray`. There is no "discarded on theme rebuild" risk — confirm this by reading the code, and add no theme-rebuild handling.

- [ ] **Step 2: Add the `Open Combat Logs` item**

Add the declaration alongside the existing four (`:261-264`):

```csharp
var openCombatLogsItem = new Forms.ToolStripMenuItem("Open Combat Logs", null, (_, _) => OpenTool("combat-logs"));
```

Add it to the menu after the other Open items, before the first separator:

```csharp
menu.Items.Add(_showHideItem);
menu.Items.Add(openTriffViewItem);
menu.Items.Add(openEveSettingsItem);
menu.Items.Add(openFleetManagerItem);
menu.Items.Add(openSkillPlannerItem);
menu.Items.Add(openCombatLogsItem);
menu.Items.Add(new Forms.ToolStripSeparator());
```

- [ ] **Step 3: Verify both sides agree on the exact id**

`OpenTool` posts whatever string it is given, with no whitelist. The only validation is `NAV_ITEMS` membership in `App.jsx:172-176`, and a mismatch does nothing visible: the settings window still comes forward (because `ShowSettings()` already ran), the tab simply does not change, and there is no error, log line, or toast anywhere.

```bash
grep -n 'OpenTool("combat-logs")' native/MainWindow.xaml.cs
grep -n '"combat-logs"' app/src/App.jsx
```

Both must match the identical literal `combat-logs`. If they differ, fix the native side — `App.jsx` defines the id set and is the source of truth.

- [ ] **Step 4: Build the native project from this worktree**

The repo scripts do not work from a worktree. Invoke the pinned SDK explicitly, with the csproj pointed at this worktree and every environment variable at the main checkout's caches:

```bash
powershell.exe -NoProfile -Command '
$env:DOTNET_CLI_HOME="C:\dev\TriffView\.dotnet-home"
$env:NUGET_PACKAGES="C:\dev\TriffView\.nuget"
$env:APPDATA="C:\dev\TriffView\.appdata"
$env:NUGET_HTTP_CACHE_PATH="C:\dev\TriffView\.nuget-cache"
$env:NUGET_PLUGINS_CACHE_PATH="C:\dev\TriffView\.nuget-plugin-cache"
$env:DOTNET_CLI_TELEMETRY_OPTOUT="1"
& "C:\dev\TriffView\.dotnet\dotnet.exe" build "C:\dev\TriffView\.claude\worktrees\nav-reorg-combat-logs-tab\native\TriffView.csproj" -c Release
'
```

Two deviations from the form documented in CLAUDE.md, both deliberate:

- **Single quotes around the `-Command` argument.** CLAUDE.md wraps it in double quotes, which lets the invoking shell expand `$env:…` *before* PowerShell ever sees it, so none of the assignments take effect. Single quotes pass the script through literally.
- **`NUGET_PLUGINS_CACHE_PATH` added.** `scripts/build-native.ps1:20` pins it to `.nuget-plugin-cache` alongside the other four caches; CLAUDE.md's worktree snippet omits it, so a build run that way can still write to the user profile.

Expected: `0 Error(s)`. Report the actual output — this cannot be verified from WSL.

Note: overriding `APPDATA` in that shell redirects anything else in the same invocation. Do runtime file work in a separate call.

- [ ] **Step 5: Manual verification on Windows**

Tray → "Open Combat Logs" opens the settings window **and** lands on the Combat Logs tab. If the window appears but the tab does not change, the ids do not match — return to Step 3.

- [ ] **Step 6: Commit**

```bash
git add native/MainWindow.xaml.cs
git commit -m "feat(tray): add Open Combat Logs tray menu item"
```

---

### Task 5: Update documentation

**Files:**
- Modify: `README.md` (:66-74)
- Verify only: `docs/DIAGNOSTICS.md:35` (the anchor), and modify `:32-35` (the wording)

**Interfaces:**
- Consumes: nothing / Produces: nothing

- [ ] **Step 1: Update the README body prose**

Change "panel" to "tab" in the three places that describe the feature's location. The heading stays exactly as it is (Step 3).

`README.md:68`:

```diff
-The **Combat log export** panel can package the game logs covering a fight into a single zip,
+The **Combat Logs** tab can package the game logs covering a fight into a single zip,
```

`README.md:70`:

```diff
-the newest are kept and the panel reports how many were left out.
+the newest are kept and the tab reports how many were left out.
```

`README.md:74`:

```diff
-The panel can also send that zip straight to Discord instead of saving it to disk:
+The tab can also send that zip straight to Discord instead of saving it to disk:
```

- [ ] **Step 2: Leave the Highlights bullet alone**

`README.md:22` describes the feature without calling it a panel, so it needs no change. Confirm via Step 4's grep before concluding that.

- [ ] **Step 3: Do NOT change the `### Combat log export` heading**

`docs/DIAGNOSTICS.md:35` links `../README.md#combat-log-export`. The anchor is derived from the heading text, and nothing checks the link at build time — changing the heading breaks it silently.

```bash
grep -n '^### Combat log export$' README.md
grep -n 'combat-log-export' docs/DIAGNOSTICS.md
```

Both must still match after editing.

- [ ] **Step 4: Update the stale "panel" wording in `docs/DIAGNOSTICS.md`**

`docs/DIAGNOSTICS.md:32-35` describes the feature by its old location. The **link target must not change** — only the wording around it:

```diff
-panel can upload a zip of your actual game logs to a Discord webhook you configure yourself — that
+tab can upload a zip of your actual game logs to a Discord webhook you configure yourself — that
```

Read the full sentence first and adjust the preceding words so it still reads correctly; the diff above shows the noun that is wrong, not necessarily the whole edit. Leave `[Combat log export](../README.md#combat-log-export)` exactly as it is.

- [ ] **Step 5: Grep for other stale references**

```bash
grep -n -i "panel\|tray\|tab" README.md
```

`README.md:32` mentions tray controls generically without enumerating the `Open …` items, so it needs no update for the new tray entry. If the grep turns up anything else tied to combat logs, the tab list, or the theme picker, update it with the same "tab" wording rather than inventing new phrasing.

- [ ] **Step 6: Commit**

```bash
git add README.md docs/DIAGNOSTICS.md
git commit -m "docs: describe combat log export as its own tab"
```
