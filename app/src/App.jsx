import React, { Suspense, useEffect, useLayoutEffect, useRef, useState } from "react";
import { onNativeMessage, openExternalUrl, postNative } from "./nativeBridge.js";
import TriffViewSettings from "./tools/TriffViewSettings.jsx";
import CombatLogs from "./tools/CombatLogs.jsx";
import { useCombatLogs } from "./tools/useCombatLogs.js";

const EveSettings = React.lazy(() => import("./tools/EveSettings.tsx"));
const TriffFleets = React.lazy(() => import("./tools/TriffFleets.tsx"));
const TriffSkills = React.lazy(() => import("./tools/TriffSkills.tsx"));

const NAV_ITEMS = [
  { id: "triffview", label: "TriffView" },
  { id: "combat-logs", label: "Combat Logs" },
  { id: "eve-settings", label: "EVE Settings" },
  { id: "fleet-manager", label: "Fleet Manager" },
  { id: "skill-planner", label: "Skill Planner" },
];
const THEME_STORAGE_KEY = "triffview.guiTheme";
const GUI_THEMES = [
  { id: "triff-tools", name: "Triff.Tools", swatches: ["#53B6FF", "#2EE6A6", "#0B111D"] },
  { id: "amarr", name: "Amarr", swatches: ["#C8A86A", "#F2E58B", "#F28C57"] },
  { id: "caldari", name: "Caldari", swatches: ["#8FD8E8", "#41B9B2", "#D88B24"] },
  { id: "carbon", name: "Carbon", swatches: ["#D6D6D6", "#8E9297", "#F28C57"] },
  { id: "gallente", name: "Gallente", swatches: ["#55CFA1", "#81BFAE", "#F28C57"] },
  { id: "minmatar", name: "Minmatar", swatches: ["#E65A37", "#9C3F2E", "#F28C57"] },
  { id: "ore", name: "ORE", swatches: ["#F0C515", "#62B2B3", "#D88475"] },
  { id: "servant-sisters", name: "Servant Sisters of EVE", swatches: ["#F15B64", "#A8D8D8", "#BDBB2B"] },
];
function readSavedTheme() {
  try {
    const saved = window.localStorage.getItem(THEME_STORAGE_KEY);
    return GUI_THEMES.some((theme) => theme.id === saved) ? saved : GUI_THEMES[0].id;
  } catch {
    return GUI_THEMES[0].id;
  }
}

function cssVar(style, name) {
  return style.getPropertyValue(name).trim();
}

function currentNativeTheme(theme) {
  const style = window.getComputedStyle(document.documentElement);
  return {
    id: theme.id,
    name: theme.name,
    background: cssVar(style, "--tv-bg"),
    text: cssVar(style, "--tv-text"),
    accent: cssVar(style, "--tv-accent"),
    border: cssVar(style, "--tv-border"),
    panelBorder: cssVar(style, "--tv-border-panel"),
    panelDark: cssVar(style, "--tv-panel-dark"),
    trayImageMargin: cssVar(style, "--tv-topbar-bg"),
    traySelected: cssVar(style, "--tv-accent-bg"),
    trayPressed: cssVar(style, "--tv-accent-selected-bg"),
    caption: cssVar(style, "--tv-topbar-bg"),
    windowBorder: cssVar(style, "--tv-border-topbar"),
    titleText: cssVar(style, "--tv-text"),
  };
}

function publishInputRegions() {
  const regions = Array.from(document.querySelectorAll("[data-hud-input-region]"))
    .map((element) => {
      const rect = element.getBoundingClientRect();
      return {
        x: rect.left,
        y: rect.top,
        width: rect.width,
        height: rect.height,
      };
    })
    .filter((region) => region.width > 0 && region.height > 0);

  postNative({ type: "input-regions", regions });
}

function UpdateNotice({ update, onDismiss, onIgnore }) {
  if (!update?.updateAvailable || update.ignored) return null;

  const versionLabel = update.latestTag || `v${update.latestVersion}`;
  const openUpdate = () => {
    if (postNative({ type: "update:open" })) return;
    // No hardcoded upstream fallback: a fork build must never link users to the
    // upstream repo's releases. Prefer the specific release page (already scoped
    // to whichever repo the exe was built from) and fall back to the generic
    // releases index; if neither is supplied there's nothing safe to open.
    const target = update.releaseUrl || update.releasesPageUrl;
    if (target) {
      openExternalUrl(target);
    }
  };

  return (
    <div className="triffview-update-pill" data-hud-input-region="update">
      <span className="triffview-update-copy">
        <strong>Update available</strong>
        <em>{versionLabel}</em>
      </span>
      <button type="button" onClick={openUpdate}>
        Open
      </button>
      <button type="button" onClick={onDismiss}>
        Later
      </button>
      <button type="button" onClick={onIgnore}>
        Ignore
      </button>
    </div>
  );
}

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
    if (open) listRef.current?.focus();
  }, [open]);

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
          ref={listRef}
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

export default function App() {
  const [activeTool, setActiveTool] = useState("triffview");
  const [triffViewSection, setTriffViewSection] = useState(null);
  const combatLogs = useCombatLogs();
  const [themeId, setThemeId] = useState(readSavedTheme);
  const [updateInfo, setUpdateInfo] = useState(null);
  const [dismissedUpdateVersion, setDismissedUpdateVersion] = useState("");
  const rootRef = useRef(null);
  const activeTheme = GUI_THEMES.find((theme) => theme.id === themeId) || GUI_THEMES[0];
  const showUpdateNotice =
    updateInfo?.updateAvailable &&
    !updateInfo?.ignored &&
    updateInfo.latestVersion &&
    dismissedUpdateVersion !== updateInfo.latestVersion;

  useEffect(() => {
    if (activeTheme.id !== themeId) {
      setThemeId(activeTheme.id);
    }
  }, [activeTheme.id, themeId]);

  useEffect(() => {
    const unsubscribe = onNativeMessage((message) => {
      if (message?.type === "standalone:navigate") {
        const tool = String(message.tool || "");
        if (NAV_ITEMS.some((item) => item.id === tool)) {
          setActiveTool(tool);
        }
      } else if (message?.type === "update-state") {
        setUpdateInfo(message.update || null);
      }
    });

    postNative({ type: "standalone:ready" });
    return unsubscribe;
  }, []);

  useEffect(() => {
    const frame = window.requestAnimationFrame(publishInputRegions);
    return () => window.cancelAnimationFrame(frame);
  }, [activeTool, themeId]);

  useLayoutEffect(() => {
    document.documentElement.dataset.theme = activeTheme.id;
    document.documentElement.dataset.themeName = activeTheme.name;
    postNative({ type: "standalone:set-theme", theme: currentNativeTheme(activeTheme) });
    try {
      window.localStorage.setItem(THEME_STORAGE_KEY, activeTheme.id);
    } catch {
      // Theme persistence is best-effort; the selected theme still applies for this session.
    }
  }, [activeTheme]);

  useEffect(() => {
    publishInputRegions();
    window.addEventListener("resize", publishInputRegions);
    const observer = new ResizeObserver(() => publishInputRegions());
    if (rootRef.current) observer.observe(rootRef.current);
    return () => {
      window.removeEventListener("resize", publishInputRegions);
      observer.disconnect();
    };
  }, []);

  function openAlertsInTriffView() {
    setTriffViewSection("alerts");
    setActiveTool("triffview");
  }

  return (
    <main className="triffview-standalone-root hud-root" data-theme={activeTheme.id} data-theme-name={activeTheme.name} ref={rootRef}>
      <header className="triffview-standalone-topbar" data-hud-input-region="topbar">
        <div className="triffview-brand-block">
          <strong>TriffView</strong>
          <ThemePicker themes={GUI_THEMES} activeTheme={activeTheme} onChange={setThemeId} />
        </div>
        {showUpdateNotice ? (
          <UpdateNotice
            update={updateInfo}
            onDismiss={() => setDismissedUpdateVersion(updateInfo.latestVersion)}
            onIgnore={() => {
              postNative({ type: "update:ignore-version", version: updateInfo.latestVersion });
              setDismissedUpdateVersion(updateInfo.latestVersion);
            }}
          />
        ) : null}
        <nav aria-label="Standalone tool navigation">
          {NAV_ITEMS.map((item) => (
            <button
              key={item.id}
              type="button"
              className={activeTool === item.id ? "is-active" : ""}
              onClick={() => setActiveTool(item.id)}
            >
              {item.label}
            </button>
          ))}
        </nav>
      </header>

      <section className="triffview-standalone-panel" data-hud-input-region="panel">
        {activeTool === "triffview" ? (
          <TriffViewSettings
            open
            initialSection={triffViewSection}
            onInitialSectionApplied={() => setTriffViewSection(null)}
          />
        ) : null}
        {activeTool === "combat-logs" ? <CombatLogs combatLogs={combatLogs} onOpenAlerts={openAlertsInTriffView} /> : null}
        {activeTool === "eve-settings" ? (
          <Suspense fallback={<div className="triffview-standalone-loading">Loading EVE Settings...</div>}>
            <EveSettings />
          </Suspense>
        ) : null}
        {activeTool === "fleet-manager" ? (
          <Suspense fallback={<div className="triffview-standalone-loading">Loading Fleet Manager...</div>}>
            <TriffFleets />
          </Suspense>
        ) : null}
        {activeTool === "skill-planner" ? (
          <Suspense fallback={<div className="triffview-standalone-loading">Loading Skill Planner...</div>}>
            <TriffSkills />
          </Suspense>
        ) : null}
      </section>
    </main>
  );
}
