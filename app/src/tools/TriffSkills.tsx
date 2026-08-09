import React, { useEffect, useMemo, useState } from "react";
import { onNativeMessage, postNative } from "../nativeBridge.js";

type Readiness = "Ready" | "Training" | "Missing";

type SkillRequirement = {
  skillName: string;
  level: number;
};

type SkillCharacter = {
  characterId: number;
  characterName: string;
  fetchedUtc: string;
  error: string;
  needsReauth: boolean;
};

type SkillPlanSummary = {
  name: string;
  requirementCount: number;
};

type MatrixEntry = {
  characterId: number;
  planName: string;
  readiness: Readiness;
  estimatedFinishUtc: string | null;
  missingSkills: SkillRequirement[];
  unknownSkills: string[];
};

type TriffSkillsState = {
  authConfigured: boolean;
  characters: SkillCharacter[];
  plans: SkillPlanSummary[];
  matrix: MatrixEntry[];
  refreshInFlight: boolean;
  authInProgress: boolean;
  plansUpdatedUtc: string;
};

// One selection drives the detail panel, whichever of the three affordances set
// it - a cell, a plan row header, or a character column header.
type Selection =
  | { kind: "cell"; characterId: number; planName: string }
  | { kind: "plan"; planName: string }
  | { kind: "character"; characterId: number }
  | null;

// Selecting what is already selected clears it, so every affordance that opens
// the detail panel also closes it. Without this the panel is a one-way door:
// there is no "no selection" target to click once one is set.
function sameSelection(a: Selection, b: Selection) {
  if (!a || !b || a.kind !== b.kind) return false;
  if (a.kind === "cell" && b.kind === "cell") {
    return a.characterId === b.characterId && a.planName === b.planName;
  }
  if (a.kind === "plan" && b.kind === "plan") return a.planName === b.planName;
  if (a.kind === "character" && b.kind === "character") return a.characterId === b.characterId;
  return false;
}

type DetailRow = {
  key: string;
  label: string;
  entry: MatrixEntry | null;
};

// A client-side, preview-only echo of SkillPlanParser.Parse (SkillPlanParser.cs).
// "Preview-only" describes what a parsed result is used for - the import modal's
// count and sample lines - not what a failed parse does: a null return is what
// gates the modal from opening at all, and the clipboard read reports a plain
// error instead (see the pendingClipboardImport effect below). That gate is
// deliberate and matches the brief; only the wording here was misleading.
// TriffSkillsController re-parses nothing - it writes the clipboard text
// verbatim - so a difference between this and the real parser affects only the
// preview, never the saved file.
type ImportPreview = { count: number; lines: string[] };

const PLAN_ROMAN_LEVELS: Record<string, number> = { I: 1, II: 2, III: 3, IV: 4, V: 5 };

function parsePlanPreview(text: string): ImportPreview | null {
  const order: string[] = [];
  const levels = new Map<string, number>();

  for (const rawLine of (text || "").split("\n")) {
    const line = rawLine.trim();
    if (!line) continue;

    const lastSpace = line.lastIndexOf(" ");
    if (lastSpace < 0) continue;

    const skillName = line.slice(0, lastSpace);
    const token = line.slice(lastSpace + 1);
    let level: number | null = null;
    if (Object.prototype.hasOwnProperty.call(PLAN_ROMAN_LEVELS, token)) {
      level = PLAN_ROMAN_LEVELS[token];
    } else if (/^\d+$/.test(token)) {
      level = Number(token);
    }
    if (level === null) continue;

    const existing = levels.get(skillName);
    if (existing === undefined) order.push(skillName);
    if (existing === undefined || level > existing) levels.set(skillName, level);
  }

  if (!order.length) return null;
  return {
    count: order.length,
    lines: order.slice(0, 5).map((skillName) => `${skillName} ${levels.get(skillName)}`),
  };
}

const WINDOWS_RESERVED_PLAN_NAMES = new Set([
  "CON",
  "PRN",
  "AUX",
  "NUL",
  "COM1",
  "COM2",
  "COM3",
  "COM4",
  "COM5",
  "COM6",
  "COM7",
  "COM8",
  "COM9",
  "LPT1",
  "LPT2",
  "LPT3",
  "LPT4",
  "LPT5",
  "LPT6",
  "LPT7",
  "LPT8",
  "LPT9",
]);

const MAX_PLAN_NAME_LENGTH = 120;

// Advisory only, same reasoning as parsePlanPreview above: a friendly message as the
// user types, not the boundary. TriffSkillsController.TryValidatePlanName runs the
// authoritative version of these same rules against the actual web message, because
// a renderer-side check can't be trusted to have run at all.
function planNameHint(name: string): string {
  if (!name) return "";
  if (name.length > MAX_PLAN_NAME_LENGTH) return `Name is too long (max ${MAX_PLAN_NAME_LENGTH} characters).`;
  if (name !== name.trim()) return "Name can't start or end with a space.";
  if (name.endsWith(".")) return "Name can't end with a period.";
  if (/[\\/:*?"<>|]/.test(name) || name.includes("..")) {
    return `Name can't contain \\ / : * ? " < > | or "..".`;
  }
  const stem = name.split(".")[0];
  if (WINDOWS_RESERVED_PLAN_NAMES.has(stem.toUpperCase())) {
    return `"${stem}" is a reserved Windows device name.`;
  }
  return "";
}

const EMPTY_STATE: TriffSkillsState = {
  authConfigured: false,
  characters: [],
  plans: [],
  matrix: [],
  refreshInFlight: false,
  authInProgress: false,
  plansUpdatedUtc: "",
};

const READINESS_META: Record<Readiness, { glyph: string; label: string; className: string }> = {
  Ready: { glyph: "●", label: "Ready", className: "is-ready" },
  Training: { glyph: "◐", label: "Training", className: "is-training" },
  Missing: { glyph: "!", label: "Missing", className: "is-missing" },
};

const READINESS_ORDER: Readiness[] = ["Ready", "Training", "Missing"];

// No entry for a character x plan pair at all - the pair has not been scored.
const UNSCORED_META = { glyph: "?", label: "Not scored", className: "is-unscored" };

// An entry whose readiness is outside the three known strings. Distinct wording
// from UNSCORED_META because the causes differ, but the same muted vocabulary.
const UNKNOWN_META = { glyph: "?", label: "Unknown", className: "is-unscored" };

const REAUTH_HINT =
  "Needs re-authentication for esi-skills.read_skills.v1 and esi-skills.read_skillqueue.v1. Use Add character to reauthorize.";

// Returns postNative's own true/false (bridge present or not) so callers that
// track "waiting for a reply" state - startClipboardImport is the one that
// currently matters - can clear it immediately on a false return instead of
// waiting forever for a reply that was never going to arrive.
function send(type: string, payload: Record<string, unknown> = {}) {
  return postNative({ type, ...payload });
}

function formatUtc(value?: string | null) {
  if (!value) return "";
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return "";
  return date.toLocaleString(undefined, {
    year: "numeric",
    month: "short",
    day: "2-digit",
    hour: "2-digit",
    minute: "2-digit",
  });
}

function matrixKey(characterId: number, planName: string) {
  return `${characterId}\u0000${planName}`;
}

function indexMatrix(entries: MatrixEntry[]) {
  const index = new Map<string, MatrixEntry>();
  for (const entry of entries || []) {
    index.set(matrixKey(entry.characterId, entry.planName), entry);
  }
  return index;
}

function isDegraded(character: SkillCharacter) {
  return Boolean(character.error) || Boolean(character.needsReauth);
}

// A readiness value outside the three known strings is an anomaly, not a
// confident "Missing" - route it to the same unscored/unknown vocabulary the
// null-entry branch uses, rather than silently reading as Missing.
function bucketOf(entry: MatrixEntry | null): Readiness | null {
  if (!entry) return null;
  return READINESS_META[entry.readiness] ? entry.readiness : null;
}

function metaFor(entry: MatrixEntry | null) {
  if (!entry) return UNSCORED_META;
  return READINESS_META[entry.readiness] ?? UNKNOWN_META;
}

// One-line gist of an entry, used by the grouped lists in the detail panel.
function summarize(entry: MatrixEntry | null) {
  const bucket = bucketOf(entry);
  if (!entry) return "No result yet - use Refresh characters";
  if (bucket === "Ready") return "All requirements met";
  if (bucket === "Training") {
    const eta = formatUtc(entry.estimatedFinishUtc);
    return eta ? `Done ${eta}` : "ETA unknown (queue paused)";
  }
  if (bucket === "Missing") {
    const count = (entry.missingSkills || []).length;
    return count === 1 ? "1 skill missing" : `${count} skills missing`;
  }
  return "Unrecognised readiness value";
}

// Buckets in READINESS_ORDER, then anything unscored. Empty groups are dropped.
function groupRows(rows: DetailRow[]) {
  const groups: { key: string; meta: { glyph: string; label: string; className: string }; rows: DetailRow[] }[] = [];
  for (const readiness of READINESS_ORDER) {
    const members = rows.filter((row) => bucketOf(row.entry) === readiness);
    if (members.length) groups.push({ key: readiness, meta: READINESS_META[readiness], rows: members });
  }
  const unscored = rows.filter((row) => bucketOf(row.entry) === null);
  if (unscored.length) groups.push({ key: "Unscored", meta: UNSCORED_META, rows: unscored });
  return groups;
}

export default function TriffSkills() {
  const [state, setState] = useState<TriffSkillsState>(EMPTY_STATE);
  const [error, setError] = useState("");
  const [confirmForgetId, setConfirmForgetId] = useState(0);
  const [selection, setSelection] = useState<Selection>(null);

  // Set when the user presses "Import from clipboard" and cleared the moment a
  // "clipboard" reply is consumed. read-clipboard/clipboard is a generic pair every
  // tool shares (MainWindow.xaml.cs:661,853), so without this flag a clipboard
  // event some other tool caused would open this dialog too.
  const [pendingClipboardImport, setPendingClipboardImport] = useState(false);
  const [importDraft, setImportDraft] = useState<{ contents: string; preview: ImportPreview } | null>(null);
  const [importName, setImportName] = useState("");
  const [importCollision, setImportCollision] = useState(false);
  const [importSubmitError, setImportSubmitError] = useState("");
  const [importBusy, setImportBusy] = useState(false);

  useEffect(() => {
    const unsubscribe = onNativeMessage((message) => {
      if (message?.type === "triffskills:state") {
        // Built field by field rather than spread. A spread carries the transport-only
        // "type" key into component state, and - worse - a present-but-null field wins
        // over the EMPTY_STATE default it is supposed to fall back to, so one null
        // characters array turns every .map/.length below into a crash. Every field the
        // renderer reads is normalized here, once, instead of guarded at each use.
        setState({
          authConfigured: message.authConfigured === true,
          characters: Array.isArray(message.characters) ? message.characters : [],
          plans: Array.isArray(message.plans) ? message.plans : [],
          matrix: Array.isArray(message.matrix) ? message.matrix : [],
          refreshInFlight: message.refreshInFlight === true,
          authInProgress: message.authInProgress === true,
          plansUpdatedUtc: typeof message.plansUpdatedUtc === "string" ? message.plansUpdatedUtc : "",
        });
      }
      if (message?.type === "triffskills:error") {
        // PostState/PostError (TriffSkillsController.cs) posts the error's
        // category-ish field as "action", not "category" - the brief's original
        // wording was aspirational, not what's on the wire.
        setError(`${message.action || "TriffSkills"}: ${message.message || "Unknown error"}`);
      }
    });

    send("triffskills:get-state");
    return unsubscribe;
  }, []);

  const cells = useMemo(() => indexMatrix(state.matrix), [state.matrix]);
  const charactersById = useMemo(
    () => new Map(state.characters.map((character) => [character.characterId, character])),
    [state.characters],
  );
  const plansByName = useMemo(() => new Map(state.plans.map((plan) => [plan.name, plan])), [state.plans]);
  const hasMatrix = state.characters.length > 0 && state.plans.length > 0;

  // The selection is resolved against the live state on every render rather
  // than pruned by an effect, so a character forgotten or a plan file deleted
  // elsewhere simply stops resolving instead of leaving a dangling panel.
  const selectedCharacter =
    selection && selection.kind !== "plan" ? charactersById.get(selection.characterId) || null : null;
  const selectedPlan = selection && selection.kind !== "character" ? plansByName.get(selection.planName) || null : null;

  function select(next: Selection) {
    setSelection((current) => (sameSelection(current, next) ? null : next));
    setConfirmForgetId(0);
  }

  function clearSelection() {
    setSelection(null);
    setConfirmForgetId(0);
  }

  // Escape is the third way out, and the one keyboard users reach for. Cells are
  // buttons reached by tab, so a mouse-only dismiss would leave them in the same
  // dead end. Bound only while something is selected, so it never swallows an
  // Escape the rest of the app might want - and skipped entirely while the import
  // modal is open, so its own Escape handler below is the only one that fires
  // instead of both firing off the same keydown and closing the modal *and*
  // clearing the grid selection behind it.
  useEffect(() => {
    if (!selection || importDraft) return;
    function onKeyDown(event: KeyboardEvent) {
      if (event.key !== "Escape") return;
      event.stopPropagation();
      setSelection(null);
      setConfirmForgetId(0);
    }
    // Escape pressed while the EVE client has focus is forwarded here as a
    // triff:hud-keydown CustomEvent, not a keydown (nativeBridge.js's own
    // Escape handling just blurs the focused control otherwise) - same
    // dual-listener pattern as TriffViewSettings.jsx's hotkey recorder.
    function onHudKeyDown(event: Event) {
      const detail = (event as CustomEvent).detail;
      if (detail?.key !== "Escape") return;
      event.preventDefault();
      setSelection(null);
      setConfirmForgetId(0);
    }
    window.addEventListener("keydown", onKeyDown);
    window.addEventListener("triff:hud-keydown", onHudKeyDown);
    return () => {
      window.removeEventListener("keydown", onKeyDown);
      window.removeEventListener("triff:hud-keydown", onHudKeyDown);
    };
  }, [selection, importDraft]);

  function confirmForget(characterId: number) {
    send("triffskills:forget-character", { characterId });
    setConfirmForgetId(0);
    setSelection(null);
  }

  function startClipboardImport() {
    setPendingClipboardImport(true);
    // No bridge (dev/browser today) means no "clipboard" reply is ever coming,
    // so the button would otherwise sit on "Reading clipboard..." forever with
    // no way to retry. In-app this is unreachable - ReadClipboard always replies.
    if (!send("read-clipboard")) setPendingClipboardImport(false);
  }

  function closeImportModal() {
    setImportDraft(null);
    setImportName("");
    setImportCollision(false);
    setImportSubmitError("");
    setImportBusy(false);
  }

  // Wiring setImportName straight through as onNameChange was the Critical
  // defect from review: importCollision described whichever name triggered it
  // and then just outlived every subsequent edit, so retyping to a second,
  // different, also-colliding name still showed Replace - sourced from the
  // stale flag, not the name on screen - and Replace sends whatever name is
  // currently in the box. Clearing the flag here means the footer (which
  // switches Replace back in only while collision is true) reverts to plain
  // Import the moment the name changes, and a fresh submit is what decides
  // whether the new name collides, never a leftover answer to a question
  // about a different name.
  function changeImportName(value: string) {
    setImportName(value);
    setImportCollision(false);
    setImportSubmitError("");
  }

  function submitImport(replace: boolean) {
    if (!importDraft) return;
    const trimmedName = importName.trim();
    if (!trimmedName) return;
    setImportBusy(true);
    setImportSubmitError("");
    setImportCollision(false);
    send("triffskills:import-plan", { name: trimmedName, contents: importDraft.contents, replace });
  }

  // read-clipboard is a generic request every tool can make (MainWindow.xaml.cs:661),
  // and its "clipboard" reply is broadcast to all of them (PostAppEvent). Listening
  // only while pendingClipboardImport is set - and clearing it the moment a reply
  // arrives - is what stops a clipboard read some other tool triggered from opening
  // this dialog.
  useEffect(() => {
    if (!pendingClipboardImport) return;
    const unsubscribe = onNativeMessage((message) => {
      if (message?.type !== "clipboard") return;
      setPendingClipboardImport(false);
      const text = typeof message.text === "string" ? message.text : "";
      const preview = parsePlanPreview(text);
      if (!preview) {
        setError(
          'TriffSkills: Clipboard did not look like a skill plan. Expected one skill per line, name then level (e.g. "Caldari Frigate III").',
        );
        return;
      }
      setImportDraft({ contents: text, preview });
      setImportName("");
      setImportCollision(false);
      setImportSubmitError("");
      setImportBusy(false);
    });
    return unsubscribe;
  }, [pendingClipboardImport]);

  // triffskills:import-collision, triffskills:import-done, and the "import-plan"
  // triffskills:error all only ever arrive in response to the triffskills:import-plan
  // message this modal itself sends, so - same reasoning as the clipboard listener
  // above - this only listens while there is a draft open to react to.
  useEffect(() => {
    if (!importDraft) return;
    const unsubscribe = onNativeMessage((message) => {
      if (message?.type === "triffskills:import-collision") {
        setImportBusy(false);
        setImportCollision(true);
        return;
      }
      if (message?.type === "triffskills:import-done") {
        closeImportModal();
        return;
      }
      if (message?.type === "triffskills:error" && message.action === "import-plan") {
        setImportBusy(false);
        setImportSubmitError(message.message || "Could not import the plan.");
      }
    });
    return unsubscribe;
  }, [importDraft]);

  // Bound only while the import modal is open, so Escape closes the modal instead
  // of falling through to the grid-selection handler above (which is itself
  // skipped for exactly this reason while importDraft is set).
  useEffect(() => {
    if (!importDraft) return;
    function onKeyDown(event: KeyboardEvent) {
      if (event.key !== "Escape") return;
      event.stopPropagation();
      closeImportModal();
    }
    // Same triff:hud-keydown gap as the selection handler above, and the same
    // fix: without this, Escape forwarded from the EVE client never reaches a
    // plain keydown listener at all - nativeBridge.js just blurs the input.
    function onHudKeyDown(event: Event) {
      const detail = (event as CustomEvent).detail;
      if (detail?.key !== "Escape") return;
      event.preventDefault();
      closeImportModal();
    }
    window.addEventListener("keydown", onKeyDown);
    window.addEventListener("triff:hud-keydown", onHudKeyDown);
    return () => {
      window.removeEventListener("keydown", onKeyDown);
      window.removeEventListener("triff:hud-keydown", onHudKeyDown);
    };
  }, [importDraft]);

  return (
    <div className="triffview-settings triffskills" data-hud-scroll data-hud-select-text-controls="true">
      <section className="triffview-settings-shell">
        <aside className="triffview-side-nav">
          <div className="triffview-nav-brand">
            <h2>TriffSkills</h2>
            <p>
              {state.characters.length} characters / {state.plans.length} plans
            </p>
          </div>
          <div className="triffview-nav-actions">
            <button
              type="button"
              className="primary-action"
              onClick={() => send("triffskills:auth")}
              disabled={state.authInProgress}
            >
              {state.authInProgress ? "Waiting for EVE SSO..." : "Add character"}
            </button>
            <button
              type="button"
              onClick={() => send("triffskills:refresh-characters")}
              disabled={state.refreshInFlight || !state.characters.length}
            >
              {state.refreshInFlight ? "Refreshing..." : "Refresh characters"}
            </button>
            <button type="button" onClick={() => send("triffskills:open-plans-folder")}>
              Open plans folder
            </button>
            <button type="button" onClick={() => send("triffskills:refresh-plans")}>
              Reload plans
            </button>
            <button type="button" onClick={startClipboardImport} disabled={pendingClipboardImport}>
              {pendingClipboardImport ? "Reading clipboard..." : "Import from clipboard"}
            </button>
          </div>
          {!state.authConfigured ? (
            <div className="triffview-warning">
              <strong>SSO client ID missing.</strong>
              <span>Set the built-in TriffView EVE SSO client ID before authenticating a character.</span>
            </div>
          ) : null}
          <div className="triffskills-legend">
            {READINESS_ORDER.map((key) => (
              <span key={key} className={READINESS_META[key].className}>
                <em aria-hidden="true">{READINESS_META[key].glyph}</em>
                {READINESS_META[key].label}
              </span>
            ))}
            <span className={UNSCORED_META.className}>
              <em aria-hidden="true">{UNSCORED_META.glyph}</em>
              {UNSCORED_META.label}
            </span>
          </div>
        </aside>

        <div className="triffview-section-content" data-hud-scroll>
          <header className="triffview-section-header">
            <div>
              <h2>Skill plan readiness</h2>
              <p>
                Rows are plans, columns are characters. Pick a cell, a plan or a character for the detail below.
              </p>
            </div>
            <span className="triffskills-plans-stamp">
              {state.plansUpdatedUtc ? `Plans updated ${formatUtc(state.plansUpdatedUtc)}` : "No plans yet"}
            </span>
          </header>

          {error ? (
            <div className="triffview-warning triffskills-error">
              <strong>TriffSkills</strong>
              <span>{error}</span>
              <button type="button" onClick={() => setError("")}>
                Clear
              </button>
            </div>
          ) : null}

          {!state.characters.length ? (
            <div className="eve-settings-empty">
              No characters yet. Use <strong>Add character</strong> to authorize one through EVE SSO. TriffSkills
              requests its own skill scopes and stores its own refresh token; it never reads Fleet Manager&apos;s.
            </div>
          ) : null}

          {!state.plans.length ? (
            <div className="eve-settings-empty">
              No plans yet. TriffSkills scores your characters against the plan files in{" "}
              <code>%APPDATA%\TriffHud\TriffSkills\plans</code>. Use <strong>Open plans folder</strong> to
              get there, drop in one <code>.txt</code> per plan (one skill per line, name then level,
              as in <em>Navigation V</em>), then use <strong>Reload plans</strong>.
            </div>
          ) : null}

          {hasMatrix ? (
            <>
              <div className="triffskills-grid-scroll" data-hud-scroll>
                <table className="triffskills-grid">
                  <colgroup>
                    <col className="triffskills-grid-label-col" />
                    {state.characters.map((character) => (
                      <col key={character.characterId} className="triffskills-grid-cell-col" />
                    ))}
                  </colgroup>
                  <thead>
                    <tr>
                      <th scope="col" className="triffskills-grid-corner">
                        Plan
                      </th>
                      {state.characters.map((character) => (
                        <th
                          scope="col"
                          key={character.characterId}
                          className={isDegraded(character) ? "triffskills-grid-head is-degraded" : "triffskills-grid-head"}
                        >
                          <button
                            type="button"
                            className={
                              selection?.kind === "character" && selection.characterId === character.characterId
                                ? "triffskills-head-button is-selected"
                                : "triffskills-head-button"
                            }
                            title={character.characterName}
                            aria-label={`Character ${character.characterName}${isDegraded(character) ? ", degraded" : ""}`}
                            onClick={() => select({ kind: "character", characterId: character.characterId })}
                          >
                            <span>{character.characterName}</span>
                          </button>
                        </th>
                      ))}
                    </tr>
                  </thead>
                  <tbody>
                    {state.plans.map((plan) => (
                      <tr key={plan.name}>
                        <th scope="row" className="triffskills-grid-label">
                          <button
                            type="button"
                            className={
                              selection?.kind === "plan" && selection.planName === plan.name
                                ? "triffskills-plan-button is-selected"
                                : "triffskills-plan-button"
                            }
                            title={plan.name}
                            aria-label={`Plan ${plan.name}, ${plan.requirementCount} skills`}
                            onClick={() => select({ kind: "plan", planName: plan.name })}
                          >
                            <strong>{plan.name}</strong>
                            <small>{plan.requirementCount} skills</small>
                          </button>
                        </th>
                        {state.characters.map((character) => {
                          const entry = cells.get(matrixKey(character.characterId, plan.name)) || null;
                          const meta = metaFor(entry);
                          const stale = isDegraded(character);
                          const selected =
                            selection?.kind === "cell" &&
                            selection.characterId === character.characterId &&
                            selection.planName === plan.name;

                          return (
                            <td key={character.characterId} className={stale ? "is-degraded" : ""}>
                              <button
                                type="button"
                                className={[
                                  "triffskills-glyph",
                                  meta.className,
                                  stale ? "is-stale" : "",
                                  selected ? "is-selected" : "",
                                ]
                                  .filter(Boolean)
                                  .join(" ")}
                                aria-label={`${character.characterName}, ${plan.name}: ${meta.label}${stale ? ", stale" : ""}`}
                                onClick={() =>
                                  select({ kind: "cell", characterId: character.characterId, planName: plan.name })
                                }
                              >
                                <em aria-hidden="true">{meta.glyph}</em>
                              </button>
                            </td>
                          );
                        })}
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>

              <DetailPanel
                selection={selection}
                character={selectedCharacter}
                plan={selectedPlan}
                characters={state.characters}
                plans={state.plans}
                cells={cells}
                confirming={Boolean(selectedCharacter) && confirmForgetId === selectedCharacter?.characterId}
                onAskForget={() => selectedCharacter && setConfirmForgetId(selectedCharacter.characterId)}
                onCancelForget={() => setConfirmForgetId(0)}
                onConfirmForget={() => selectedCharacter && confirmForget(selectedCharacter.characterId)}
                onClear={clearSelection}
              />
            </>
          ) : null}
        </div>
      </section>

      {importDraft ? (
        <ImportPlanModal
          draft={importDraft}
          name={importName}
          onNameChange={changeImportName}
          collision={importCollision}
          submitError={importSubmitError}
          busy={importBusy}
          onCancel={closeImportModal}
          onConfirm={() => submitImport(false)}
          onReplace={() => submitImport(true)}
        />
      ) : null}
    </div>
  );
}

function DetailPanel({
  selection,
  character,
  plan,
  characters,
  plans,
  cells,
  confirming,
  onAskForget,
  onCancelForget,
  onConfirmForget,
  onClear,
}: {
  selection: Selection;
  character: SkillCharacter | null;
  plan: SkillPlanSummary | null;
  characters: SkillCharacter[];
  plans: SkillPlanSummary[];
  cells: Map<string, MatrixEntry>;
  confirming: boolean;
  onAskForget: () => void;
  onCancelForget: () => void;
  onConfirmForget: () => void;
  onClear: () => void;
}) {
  if (!selection) {
    return (
      <div className="triffskills-detail is-empty">
        Nothing selected. Pick a cell for one character against one plan, a plan name for every character, or a
        character name for every plan and that character&apos;s controls.
      </div>
    );
  }

  if (selection.kind === "cell") {
    if (!character || !plan) return <StaleSelection onClear={onClear} />;
    return (
      <div className="triffskills-detail">
        <header className="triffskills-detail-head">
          <h3>
            {character.characterName} / {plan.name}
          </h3>
          <small>{plan.requirementCount} skills in this plan</small>
          <DetailDismiss onClear={onClear} />
        </header>
        <CellDetail entry={cells.get(matrixKey(character.characterId, plan.name)) || null} stale={isDegraded(character)} />
      </div>
    );
  }

  if (selection.kind === "plan") {
    if (!plan) return <StaleSelection onClear={onClear} />;
    const rows: DetailRow[] = characters.map((item) => ({
      key: String(item.characterId),
      label: item.characterName,
      entry: cells.get(matrixKey(item.characterId, plan.name)) || null,
    }));

    return (
      <div className="triffskills-detail">
        <header className="triffskills-detail-head">
          <h3>{plan.name}</h3>
          <small>
            {plan.requirementCount} skills / {characters.length} characters
          </small>
          <DetailDismiss onClear={onClear} />
        </header>
        <DetailGroups rows={rows} />
      </div>
    );
  }

  if (!character) return <StaleSelection onClear={onClear} />;
  const degraded = isDegraded(character);
  const stamp = formatUtc(character.fetchedUtc);
  const rows: DetailRow[] = plans.map((item) => ({
    key: item.name,
    label: item.name,
    entry: cells.get(matrixKey(character.characterId, item.name)) || null,
  }));

  return (
    <div className="triffskills-detail">
      <header className="triffskills-detail-head">
        <h3>{character.characterName}</h3>
        <small>{stamp ? `${degraded ? "Last good" : "Updated"} ${stamp}` : "Never fetched"}</small>
        <DetailDismiss onClear={onClear} />
      </header>

      {character.needsReauth ? (
        <span className="triffskills-flag">
          <em aria-hidden="true">!</em>
          {REAUTH_HINT}
        </span>
      ) : null}

      {/* Shown even when needsReauth is set. The hint above says what to do; this says why
          the character is in that state - expired sign-in, missing scopes, a 401, or the
          text of whatever the token refresh threw. Suppressing it on re-auth hid exactly
          the line a first-run user needs, since a misconfigured client ID reaches the UI
          only through this string. */}
      {character.error ? (
        <span className="triffskills-flag">
          <em aria-hidden="true">!</em>
          {character.error}
        </span>
      ) : null}

      <DetailGroups rows={rows} />

      {/* The 34px column header cannot hold a destructive control and its confirmation
          copy, so Forget lives here - selecting the character is what surfaces it. */}
      {confirming ? (
        <>
          <div className="triffskills-row-actions">
            <button type="button" className="danger-action" onClick={onConfirmForget}>
              Confirm forget
            </button>
            <button type="button" onClick={onCancelForget}>
              Cancel
            </button>
          </div>
          <small className="triffskills-confirm-note">
            Deletes the stored refresh token and this character&apos;s cached skills. Fleet Manager&apos;s
            credential for the same character is untouched.
          </small>
        </>
      ) : (
        <div className="triffskills-row-actions">
          <button type="button" onClick={onAskForget}>
            Forget character
          </button>
        </div>
      )}
    </div>
  );
}

function StaleSelection({ onClear }: { onClear: () => void }) {
  return (
    <div className="triffskills-detail is-empty">
      That selection no longer exists. Pick another cell.
      <DetailDismiss onClear={onClear} />
    </div>
  );
}

// A real button with a text label rather than a bare glyph: the toggle-to-clear
// behaviour above is not discoverable on its own, and a wordless x is not much
// of an improvement on that for anyone reading the panel with a screen reader.
function DetailDismiss({ onClear }: { onClear: () => void }) {
  return (
    <button type="button" className="triffskills-detail-dismiss" onClick={onClear}>
      Clear selection
    </button>
  );
}

function DetailGroups({ rows }: { rows: DetailRow[] }) {
  const groups = groupRows(rows);
  if (!groups.length) return null;

  return (
    <div className="triffskills-groups">
      {groups.map((group) => (
        <section key={group.key} className={`triffskills-group ${group.meta.className}`}>
          <h4>
            <em aria-hidden="true">{group.meta.glyph}</em>
            {group.meta.label}
            <span>{group.rows.length}</span>
          </h4>
          <ul>
            {group.rows.map((row) => (
              <li key={row.key}>
                <strong>{row.label}</strong>
                <small>{summarize(row.entry)}</small>
              </li>
            ))}
          </ul>
        </section>
      ))}
    </div>
  );
}

function CellDetail({ entry, stale }: { entry: MatrixEntry | null; stale: boolean }) {
  if (!entry) {
    return (
      <div className="triffskills-cell is-unscored">
        <span className="triffskills-state">
          <em aria-hidden="true">{UNSCORED_META.glyph}</em>
          {UNSCORED_META.label}
        </span>
        <small>No result for this character and plan yet. Use Refresh characters.</small>
      </div>
    );
  }

  const meta = metaFor(entry);
  const bucket = bucketOf(entry);
  const missing = entry.missingSkills || [];
  const unknown = entry.unknownSkills || [];
  const eta = formatUtc(entry.estimatedFinishUtc);

  return (
    <div className={`triffskills-cell ${meta.className}`}>
      <span className="triffskills-state">
        <em aria-hidden="true">{meta.glyph}</em>
        {meta.label}
      </span>

      {stale ? <small className="triffskills-stale">Stale - last good data</small> : null}

      {bucket === "Training" ? <small>{eta ? `Done ${eta}` : "Training, ETA unknown (queue paused)"}</small> : null}

      {bucket === "Missing" && missing.length ? (
        <ul className="triffskills-skill-list">
          {missing.map((skill) => (
            <li key={`${skill.skillName}-${skill.level}`}>
              {skill.skillName} {skill.level}
            </li>
          ))}
        </ul>
      ) : null}

      {unknown.length ? (
        <div className="triffskills-unknown">
          <span>Unresolved skill names - plan cannot be fully evaluated</span>
          <ul className="triffskills-skill-list">
            {unknown.map((name) => (
              <li key={name}>{name}</li>
            ))}
          </ul>
        </div>
      ) : null}
    </div>
  );
}

// Matches the established modal pattern at EveSettings.tsx:162-193 exactly:
// .triffview-modal-backdrop + .triffview-hotkey-modal, a header (h3 + p + Close), a
// body with data-hud-scroll, and a footer with Cancel and a primary-action.
function ImportPlanModal({
  draft,
  name,
  onNameChange,
  collision,
  submitError,
  busy,
  onCancel,
  onConfirm,
  onReplace,
}: {
  draft: { contents: string; preview: ImportPreview };
  name: string;
  onNameChange: (value: string) => void;
  collision: boolean;
  submitError: string;
  busy: boolean;
  onCancel: () => void;
  onConfirm: () => void;
  onReplace: () => void;
}) {
  const trimmedName = name.trim();
  // Hinted on the trimmed value, same as submit: hinting on the raw field
  // would grey out Import over a trailing space the controller strips anyway.
  const hint = planNameHint(trimmedName);
  const remaining = draft.preview.count - draft.preview.lines.length;

  return (
    <div className="triffview-modal-backdrop">
      <section className="triffview-hotkey-modal triffskills-import-modal">
        <header>
          <div>
            <h3>Import plan from clipboard</h3>
            <p>
              {draft.preview.count === 1 ? "1 skill parsed." : `${draft.preview.count} skills parsed.`} Name the plan
              to save it.
            </p>
          </div>
          <button type="button" onClick={onCancel}>
            Close
          </button>
        </header>

        <div className="triffskills-import-body" data-hud-scroll>
          <label className="triffskills-import-name">
            <span>Plan name</span>
            <input
              autoFocus
              value={name}
              placeholder="e.g. Marauder V"
              onChange={(event) => onNameChange(event.target.value)}
            />
          </label>
          {hint ? <small className="triffskills-import-hint">{hint}</small> : null}

          <div className="triffskills-import-preview">
            <strong>Preview</strong>
            <ul className="triffskills-skill-list">
              {draft.preview.lines.map((line) => (
                <li key={line}>{line}</li>
              ))}
            </ul>
            {remaining > 0 ? <small>...and {remaining} more</small> : null}
          </div>

          {collision ? (
            <div className="triffview-warning">
              <strong>&quot;{trimmedName}&quot; already exists.</strong>
              <span>Replace it, or Cancel and pick a different name.</span>
            </div>
          ) : null}

          {submitError ? (
            <div className="triffview-warning">
              <span>{submitError}</span>
            </div>
          ) : null}
        </div>

        <footer className="triffskills-import-actions">
          <button type="button" onClick={onCancel}>
            Cancel
          </button>
          {collision ? (
            <button
              type="button"
              className="danger-action"
              onClick={onReplace}
              disabled={busy || !trimmedName || Boolean(hint)}
            >
              Replace
            </button>
          ) : (
            <button type="button" className="primary-action" onClick={onConfirm} disabled={busy || !trimmedName || Boolean(hint)}>
              {busy ? "Importing..." : "Import"}
            </button>
          )}
        </footer>
      </section>
    </div>
  );
}
