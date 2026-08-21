import React, { useEffect, useMemo, useRef, useState } from "react";
import { copyText, onNativeMessage, postNative } from "../nativeBridge.js";
import { buildRoster } from "./skills/rosterOrdering";
import PlanRail from "./skills/PlanRail";
import CharacterRow from "./skills/CharacterRow";
import "./TriffSkills.css";

export type Readiness = "Ready" | "Training" | "Locked" | "Missing" | "Unknown" | "Unscored";
export type RequirementState = "Active" | "TrainedInactive" | "Queued" | "Missing" | "Unknown";

export type Character = {
  characterId: number;
  characterName: string;
  fetchedUtc?: string | null;
  error?: string;
  needsReauth?: boolean;
  stale?: boolean;
};

type Plan = { name: string; requirementCount: number };

export type MatrixCell = {
  characterId: number;
  planName: string;
  readiness: Readiness;
  estimatedFinishUtc?: string | null;
  queueTimingUnknown?: boolean;
  activeCount: number;
  trainedInactiveCount: number;
  queuedCount: number;
  missingCount: number;
  unknownCount: number;
};

type Diagnostic = { line?: number; message: string };
type PlanIssue = { fileName: string; message: string; diagnostics?: Diagnostic[] };
export type CharacterGroupDef = { name: string; characterIds: number[] };

export type RequirementDetail = {
  skillName: string;
  requiredLevel: number;
  activeLevel: number | null;
  trainedLevel: number | null;
  state: RequirementState;
  queuedFinishUtc?: string | null;
  queueTimingUnknown?: boolean;
};

export type CellDetail = {
  characterId: number;
  planName: string;
  readiness: Readiness;
  estimatedFinishUtc?: string | null;
  queueTimingUnknown?: boolean;
  requirements: RequirementDetail[];
};

type SkillsState = {
  authConfigured: boolean;
  authInProgress: boolean;
  refreshInFlight: boolean;
  characters: Character[];
  plans: Plan[];
  matrix: MatrixCell[];
  planIssues: PlanIssue[];
  warnings: string[];
  plansUpdatedUtc?: string;
  pinnedCharacterIds: number[];
  characterGroups: CharacterGroupDef[];
  selectedPlanName: string;
};

type Preview = {
  requestId: string;
  revision: number;
  ok: boolean;
  name: string;
  requirementCount: number;
  requirements: Array<{ skillName: string; level: number }>;
  diagnostics: Diagnostic[];
  collision?: boolean;
  message?: string;
};

const EMPTY_STATE: SkillsState = {
  authConfigured: false,
  authInProgress: false,
  refreshInFlight: false,
  characters: [],
  plans: [],
  matrix: [],
  planIssues: [],
  warnings: [],
  pinnedCharacterIds: [],
  characterGroups: [],
  selectedPlanName: "",
};

const READINESS_ORDER: Readiness[] = ["Ready", "Training", "Locked", "Missing", "Unknown", "Unscored"];
export const STATUS: Record<Readiness, { label: string; description: string; sampleFill?: number }> = {
  Ready: { label: "Ready", description: "All requirements are active", sampleFill: 1 },
  Training: { label: "Training", description: "A requirement is in the queue", sampleFill: 0.5 },
  Locked: { label: "Locked", description: "Trained requirements are not active", sampleFill: 1 },
  Missing: { label: "Missing", description: "Requirements still need training", sampleFill: 0 },
  Unknown: { label: "Unknown", description: "A skill could not be resolved" },
  Unscored: { label: "Unscored", description: "No successful character fetch yet" },
};
export const REQUIREMENT_ORDER: RequirementState[] = ["TrainedInactive", "Queued", "Missing", "Unknown"];
export const REQUIREMENT_LABEL: Record<RequirementState, string> = {
  Active: "Active",
  TrainedInactive: "Trained, inactive",
  Queued: "Queued",
  Missing: "Missing",
  Unknown: "Unknown",
};
export const LEVELS = ["", "I", "II", "III", "IV", "V"];

const requestId = () =>
  typeof crypto?.randomUUID === "function"
    ? crypto.randomUUID().replaceAll("-", "")
    : `${Date.now().toString(36)}${Math.random().toString(36).slice(2)}`;

function send(type: string, payload: Record<string, unknown> = {}) {
  return postNative({ type, ...payload });
}

export function formatDate(value?: string | null) {
  if (!value) return "";
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? "" : date.toLocaleString();
}

function quantizedProgress(progress?: number) {
  return progress === undefined ? undefined : Math.round(progress * 4) / 4;
}

export function statusClass(readiness: Readiness) {
  return `is-${readiness.toLowerCase()}`;
}

function key(characterId: number, planName: string) {
  return `${characterId}:${planName}`;
}

export function isDegraded(character: Character) {
  return Boolean(character.stale || character.error);
}

export function statusLine(cell: MatrixCell | undefined, readiness: Readiness) {
  if (readiness === "Training") {
    if (cell?.queueTimingUnknown) return "Training — timing unknown";
    const eta = formatDate(cell?.estimatedFinishUtc);
    return eta ? `Training — ${eta}` : "Training";
  }
  if (readiness === "Missing") {
    return `Missing ${cell?.missingCount ?? 0}`;
  }
  return STATUS[readiness].label;
}

export function ProgressMark({ readiness, fill }: { readiness: Readiness; fill?: number }) {
  const quantized = quantizedProgress(readiness === "Ready" ? 1 : fill);
  return (
    <span
      className={`triffskills-progress-mark ${statusClass(readiness)}`}
      style={{ "--tv-fill": quantized ?? 0 } as React.CSSProperties}
      aria-hidden="true"
    />
  );
}

function ImportPlanModal({
  planName,
  planText,
  preview,
  previewRequest,
  commitRequest,
  importError,
  onNameChange,
  onTextChange,
  onPreview,
  onCommit,
  onClose,
}: {
  planName: string;
  planText: string;
  preview: Preview | null;
  previewRequest: string;
  commitRequest: string;
  importError: string;
  onNameChange: (value: string) => void;
  onTextChange: (value: string) => void;
  onPreview: () => void;
  onCommit: (replace: boolean) => void;
  onClose: () => void;
}) {
  const saving = Boolean(commitRequest);
  const validating = Boolean(previewRequest);
  const canPreview = Boolean(planName.trim() && planText.trim()) && !validating && !saving;
  const canCommit = Boolean(preview?.ok && preview.revision >= 0) && !validating && !saving;

  return (
    <div className="triffview-modal-backdrop triffskills-modal-backdrop">
      <section className="triffview-hotkey-modal triffskills-import-modal" role="dialog" aria-modal="true" aria-labelledby="triffskills-import-title">
        <header>
          <div>
            <h3 id="triffskills-import-title">Import local plan</h3>
            <p>Native validation is authoritative. Nothing is saved until the validated preview is committed.</p>
          </div>
          <button type="button" disabled={saving} onClick={onClose}>Close</button>
        </header>

        <div className="triffskills-import-body" data-hud-scroll>
          <label>
            <span>Plan name</span>
            <input
              autoFocus
              maxLength={128}
              disabled={saving}
              value={planName}
              onChange={(event) => onNameChange(event.target.value)}
            />
          </label>
          <label>
            <span>Plan text</span>
            <textarea
              rows={9}
              maxLength={524288}
              disabled={saving}
              value={planText}
              placeholder="Navigation V\nSpaceship Command IV"
              onChange={(event) => onTextChange(event.target.value)}
            />
          </label>
          <small>Paste one skill per line followed by level I–V or 1–5.</small>

          {importError ? <div className="triffskills-import-error" role="alert">{importError}</div> : null}

          {preview ? (
            <section className={`triffskills-preview${preview.ok ? " is-valid" : " is-invalid"}`} aria-live="polite">
              {preview.ok ? (
                <>
                  <strong>{preview.requirementCount} validated requirement{preview.requirementCount === 1 ? "" : "s"}</strong>
                  <ul data-hud-scroll>
                    {preview.requirements.map((requirement) => (
                      <li key={requirement.skillName}>
                        <span>{requirement.skillName}</span>
                        <b>{LEVELS[requirement.level] || requirement.level}</b>
                      </li>
                    ))}
                  </ul>
                  {preview.requirementCount > preview.requirements.length ? <small>First {preview.requirements.length} shown.</small> : null}
                  {preview.collision ? (
                    <div className="triffskills-collision" role="alert">
                      A plan named <strong>{preview.name || planName.trim()}</strong> already exists. Replacing it overwrites that local file.
                    </div>
                  ) : null}
                </>
              ) : (
                <>
                  <strong>Plan validation found issues</strong>
                  <ul data-hud-scroll>
                    {(preview.diagnostics || []).map((diagnostic, index) => (
                      <li key={index}>{diagnostic.line ? `Line ${diagnostic.line}: ` : ""}{diagnostic.message}</li>
                    ))}
                  </ul>
                </>
              )}
            </section>
          ) : null}
        </div>

        <footer className="triffskills-import-actions">
          <button type="button" disabled={saving} onClick={onClose}>Cancel</button>
          <button type="button" disabled={!canPreview} onClick={onPreview}>
            {validating ? "Validating…" : "Preview"}
          </button>
          {preview?.ok ? (
            <button
              type="button"
              disabled={!canCommit}
              className={preview.collision ? "danger-action" : "primary-action"}
              onClick={() => onCommit(Boolean(preview.collision))}
            >
              {saving ? "Saving…" : preview.collision ? "Replace local plan" : "Import local plan"}
            </button>
          ) : null}
        </footer>
      </section>
    </div>
  );
}

export default function TriffSkills() {
  const [state, setState] = useState<SkillsState>(EMPTY_STATE);
  const [error, setError] = useState("");
  const [progress, setProgress] = useState("");
  const [importOpen, setImportOpen] = useState(false);
  const [importError, setImportError] = useState("");
  const [planName, setPlanName] = useState("");
  const [planText, setPlanText] = useState("");
  const [previewRequest, setPreviewRequest] = useState("");
  const [commitRequest, setCommitRequest] = useState("");
  const [preview, setPreview] = useState<Preview | null>(null);
  const [filter, setFilter] = useState("");
  const [activeGroup, setActiveGroup] = useState<string | null>(null);
  const [expandedIds, setExpandedIds] = useState<Set<number>>(new Set());
  const [details, setDetails] = useState<Map<number, CellDetail>>(new Map());
  const inputRevisionRef = useRef(0);
  const previewRequestRef = useRef<{ requestId: string; revision: number } | null>(null);
  const commitRequestRef = useRef("");
  const previewRef = useRef<Preview | null>(null);
  const pendingDetailRequestsRef = useRef<Map<string, number>>(new Map());
  const expandedIdsRef = useRef<Set<number>>(new Set());

  const cells = useMemo(() => {
    const map = new Map<string, MatrixCell>();
    for (const cell of state.matrix) map.set(key(cell.characterId, cell.planName), cell);
    return map;
  }, [state.matrix]);

  const selectedPlanName = useMemo(() => {
    if (state.plans.some((plan) => plan.name === state.selectedPlanName)) return state.selectedPlanName;
    return state.plans[0]?.name ?? "";
  }, [state.plans, state.selectedPlanName]);

  const readyCounts = useMemo(() => {
    const counts: Record<string, number> = {};
    for (const cell of state.matrix) {
      if (cell.readiness === "Ready") counts[cell.planName] = (counts[cell.planName] ?? 0) + 1;
    }
    return counts;
  }, [state.matrix]);

  const pinnedSet = useMemo(() => new Set(state.pinnedCharacterIds), [state.pinnedCharacterIds]);

  const cellFor = (characterId: number) => cells.get(key(characterId, selectedPlanName));

  const roster = useMemo(
    () =>
      buildRoster({
        characters: state.characters,
        readinessOf: (characterId) => cellFor(characterId)?.readiness ?? "Unscored",
        missingOf: (characterId) => cellFor(characterId)?.missingCount ?? Number.MAX_SAFE_INTEGER,
        pinnedIds: state.pinnedCharacterIds,
        filter,
        activeGroup,
        groups: state.characterGroups,
      }),
    [state.characters, state.pinnedCharacterIds, state.characterGroups, cells, selectedPlanName, filter, activeGroup],
  );

  const charactersById = useMemo(() => {
    const map = new Map<number, Character>();
    for (const character of state.characters) map.set(character.characterId, character);
    return map;
  }, [state.characters]);

  const filtersActive = Boolean(filter.trim() || activeGroup);
  const rosterIsEmpty = roster.every((group) => !group.characterIds.length);

  function clearRosterFilters() {
    setFilter("");
    setActiveGroup(null);
  }

  function togglePinned(characterId: number) {
    send("triffskills:set-pinned", { characterId, pinned: !pinnedSet.has(characterId) });
  }

  function toggleGroupMembership(characterId: number, groupName: string) {
    const group = state.characterGroups.find((item) => item.name === groupName);
    if (!group) return;
    const isMember = group.characterIds.includes(characterId);
    const characterIds = isMember
      ? group.characterIds.filter((id) => id !== characterId)
      : [...group.characterIds, characterId];
    send("triffskills:save-group", { name: group.name, originalName: group.name, characterIds });
  }

  function forgetCharacter(characterId: number) {
    send("triffskills:forget-character", { characterId });
    setExpandedIds((current) => {
      if (!current.has(characterId)) return current;
      const next = new Set(current);
      next.delete(characterId);
      return next;
    });
  }

  function requestCellDetail(characterId: number, planNameForRequest: string) {
    const id = requestId();
    pendingDetailRequestsRef.current.set(id, characterId);
    send("triffskills:get-cell-detail", { requestId: id, characterId, planName: planNameForRequest });
  }

  function toggleExpand(characterId: number) {
    const opening = !expandedIds.has(characterId);
    setExpandedIds((current) => {
      const next = new Set(current);
      if (opening) next.add(characterId);
      else next.delete(characterId);
      return next;
    });
    // One request per expansion: skip the fetch if detail for the current
    // plan is already cached, so re-expanding a row doesn't re-ask native.
    if (opening) {
      const existing = details.get(characterId);
      if (!existing || existing.planName !== selectedPlanName) requestCellDetail(characterId, selectedPlanName);
    }
  }

  function copyMissingSkills(characterId: number) {
    const detail = details.get(characterId);
    if (!detail) return;
    const outstanding = detail.requirements.filter((requirement) => requirement.state !== "Active");
    if (!outstanding.length) return;
    const lines = outstanding.map((requirement) => `${requirement.skillName} ${LEVELS[requirement.requiredLevel] || requirement.requiredLevel}`);
    copyText(lines.join("\n"));
  }

  useEffect(() => {
    expandedIdsRef.current = expandedIds;
  }, [expandedIds]);

  // The cached detail is scoped to whichever plan it was fetched for; switching
  // plans invalidates it and re-fetches only the rows a user already has open,
  // never the whole roster.
  useEffect(() => {
    setDetails(new Map());
    for (const characterId of expandedIdsRef.current) requestCellDetail(characterId, selectedPlanName);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [selectedPlanName]);

  useEffect(() => {
    const unsubscribe = onNativeMessage((message: any) => {
      if (message?.type === "triffskills:state") {
        setState({ ...EMPTY_STATE, ...message });
        return;
      }
      if (message?.type === "triffskills:refresh-progress") {
        setProgress(`Refreshed ${message.completed ?? 0} of ${message.total ?? 0} characters`);
        return;
      }
      if (message?.type === "triffskills:error") {
        setError(`${message.action || "Skill Planner"}: ${message.message || "Unknown error"}`);
        return;
      }
      if (message?.type === "triffskills:cell-detail") {
        const characterId = pendingDetailRequestsRef.current.get(message.requestId);
        if (characterId === undefined) return;
        pendingDetailRequestsRef.current.delete(message.requestId);
        if (message.ok) {
          setDetails((current) => {
            const next = new Map(current);
            next.set(characterId, message as CellDetail);
            return next;
          });
        } else {
          setError(message.message || "Could not load requirement detail.");
        }
        return;
      }
      const pendingPreview = previewRequestRef.current;
      if (message?.type === "triffskills:plan-preview"
        && pendingPreview
        && message.requestId === pendingPreview.requestId
        && message.revision === pendingPreview.revision
        && message.revision === inputRevisionRef.current) {
        previewRequestRef.current = null;
        setPreviewRequest("");
        previewRef.current = message as Preview;
        setPreview(previewRef.current);
        setImportError("");
        return;
      }
      if (message?.type === "triffskills:plan-commit"
        && message.requestId === commitRequestRef.current
        && message.requestId === previewRef.current?.requestId
        && message.revision === previewRef.current?.revision
        && message.revision === inputRevisionRef.current) {
        commitRequestRef.current = "";
        setCommitRequest("");
        if (message.ok) {
          setPlanName("");
          setPlanText("");
          previewRef.current = null;
          setPreview(null);
          setImportError("");
          setImportOpen(false);
          setError("");
        } else if (message.collision) {
          setPreview((current) => {
            previewRef.current = current ? { ...current, collision: true } : current;
            return previewRef.current;
          });
          setImportError("");
        } else {
          setImportError(message.message || "Plan import failed.");
        }
      }
    });
    send("triffskills:get-state");
    return unsubscribe;
  }, []);

  useEffect(() => {
    if (!state.refreshInFlight) setProgress("");
  }, [state.refreshInFlight]);

  function invalidatePlanPreview() {
    if (commitRequestRef.current) return;
    inputRevisionRef.current += 1;
    previewRequestRef.current = null;
    previewRef.current = null;
    setPreviewRequest("");
    setPreview(null);
    setImportError("");
  }

  function previewPlan() {
    setImportError("");
    previewRef.current = null;
    setPreview(null);
    const id = requestId();
    const revision = inputRevisionRef.current;
    previewRequestRef.current = { requestId: id, revision };
    setPreviewRequest(id);
    if (!send("triffskills:preview-plan", { requestId: id, revision, name: planName, contents: planText })) {
      previewRequestRef.current = null;
      setPreviewRequest("");
      setImportError("The native TriffView bridge is unavailable.");
    }
  }

  function commitPlan(replace: boolean) {
    const current = previewRef.current;
    if (!current?.ok || current.revision !== inputRevisionRef.current || commitRequestRef.current) return;
    commitRequestRef.current = current.requestId;
    setCommitRequest(current.requestId);
    setImportError("");
    if (!send("triffskills:commit-plan", { requestId: current.requestId, revision: current.revision, replace })) {
      commitRequestRef.current = "";
      setCommitRequest("");
      setImportError("The native TriffView bridge is unavailable.");
    }
  }

  function closeImportModal() {
    if (commitRequestRef.current) return;
    setImportOpen(false);
    setPlanName("");
    setPlanText("");
    invalidatePlanPreview();
  }

  const hasNotices = Boolean(error || progress || state.warnings.length || state.planIssues.length);
  const plansStamp = state.plansUpdatedUtc ? `Plans updated ${formatDate(state.plansUpdatedUtc)}` : "No plans loaded";

  return (
    <div className="triffview-settings triffskills" data-hud-select-text-controls="true">
      <section className="triffview-settings-shell">
        <aside className="triffview-side-nav triffskills-rail">
          <div className="triffview-nav-brand">
            <h2>TriffSkills</h2>
            <p>{state.characters.length} character{state.characters.length === 1 ? "" : "s"} / {state.plans.length} plan{state.plans.length === 1 ? "" : "s"}</p>
          </div>

          <nav className="triffskills-rail-actions" aria-label="Skill Planner actions">
            {state.authInProgress ? (
              <button type="button" onClick={() => send("triffskills:cancel-auth")}>Cancel sign-in</button>
            ) : (
              <button type="button" className="primary-action" onClick={() => send("triffskills:auth")}>Add character</button>
            )}
            <button type="button" disabled={state.refreshInFlight || !state.characters.length} onClick={() => send("triffskills:refresh-characters")}>
              {state.refreshInFlight ? "Refreshing…" : "Refresh characters"}
            </button>
          </nav>

          {state.authInProgress ? <p className="triffskills-rail-status" aria-live="polite">Waiting for EVE SSO…</p> : null}
          {!state.authConfigured ? (
            <div className="triffview-warning triffskills-sso-warning">
              <strong>SSO not configured</strong>
              <span>This build needs an EVE SSO client ID before authentication can finish.</span>
            </div>
          ) : null}

          <section className="triffskills-plan-section">
            <h3>Plans</h3>
            <PlanRail
              plans={state.plans}
              readyCounts={readyCounts}
              characterCount={state.characters.length}
              selectedPlanName={selectedPlanName}
              onSelect={(name) => send("triffskills:select-plan", { planName: name })}
            />
          </section>

          <nav className="triffskills-rail-actions triffskills-rail-actions-bottom" aria-label="Plan file actions">
            <button type="button" disabled title="Copy the selected plan to the clipboard (coming soon)">Copy plan</button>
            <button type="button" disabled title="Import a plan from the clipboard (coming soon)">Import from clipboard</button>
            <button type="button" onClick={() => { setImportError(""); setImportOpen(true); }}>Import local plan</button>
            <button type="button" onClick={() => send("triffskills:open-plans-folder")}>Open plans folder</button>
            <button type="button" onClick={() => send("triffskills:refresh-plans")}>Reload plans</button>
          </nav>

          <section className="triffskills-legend" aria-label="Readiness legend">
            <h3>Readiness</h3>
            {READINESS_ORDER.map((readiness) => (
              <span className={statusClass(readiness)} key={readiness} title={STATUS[readiness].description}>
                <ProgressMark readiness={readiness} fill={STATUS[readiness].sampleFill} />
                {STATUS[readiness].label}
              </span>
            ))}
            <p>Color shows why a plan is blocked. Fill shows the share of requirements already trained.</p>
          </section>
        </aside>

        <main className="triffview-section-content">
          <header className="triffview-section-header triffskills-header">
            <div>
              <h2>Skill plan readiness</h2>
              <p>Pick a plan from the left rail, then browse characters grouped by readiness.</p>
            </div>
            <span className="triffskills-plans-stamp">{plansStamp}</span>
          </header>

          <div className={`triffskills-notices${hasNotices ? "" : " is-empty"}`} data-hud-scroll aria-live="polite">
            {error ? (
              <div className="triffskills-notice is-error" role="alert"><span>{error}</span><button type="button" onClick={() => setError("")}>Dismiss</button></div>
            ) : null}
            {progress ? <div className="triffskills-notice">{progress}</div> : null}
            {state.warnings.map((warning, index) => <div className="triffskills-notice" key={`${index}:${warning}`}>{warning}</div>)}
            {state.planIssues.length ? (
              <details className="triffskills-notice triffskills-issues">
                <summary>{state.planIssues.length} plan file issue{state.planIssues.length === 1 ? "" : "s"}</summary>
                {state.planIssues.map((issue) => (
                  <div key={`${issue.fileName}:${issue.message}`}>
                    <strong>{issue.fileName}</strong>: {issue.message}
                    {(issue.diagnostics || []).map((diagnostic, index) => (
                      <p key={index}>{diagnostic.line ? `Line ${diagnostic.line}: ` : ""}{diagnostic.message}</p>
                    ))}
                  </div>
                ))}
              </details>
            ) : null}
          </div>

          <div className="triffskills-workspace">
            <div className="triffskills-filters">
              <input
                type="search"
                className="triffskills-filter"
                placeholder="Filter characters…"
                value={filter}
                onChange={(event) => setFilter(event.target.value)}
                aria-label="Filter characters by name"
              />
              <button
                type="button"
                className={`triffskills-chip${activeGroup === null ? " is-on" : ""}`}
                onClick={() => setActiveGroup(null)}
              >
                All {state.characters.length}
              </button>
              {state.characterGroups.map((group) => (
                <button
                  type="button"
                  key={group.name}
                  className={`triffskills-chip${activeGroup === group.name ? " is-on" : ""}`}
                  onClick={() => setActiveGroup(activeGroup === group.name ? null : group.name)}
                >
                  {group.name}
                </button>
              ))}
            </div>

            <div className="triffskills-roster" data-hud-scroll>
              {!state.characters.length ? (
                <div className="triffskills-empty">
                  <p><strong>No characters yet.</strong> Add one from the actions on the left.</p>
                </div>
              ) : rosterIsEmpty ? (
                <div className="triffskills-empty">
                  <p><strong>No characters match the current filter.</strong></p>
                  {filtersActive ? (
                    <p>
                      <button type="button" onClick={clearRosterFilters}>Clear filter</button>
                    </p>
                  ) : null}
                </div>
              ) : (
                roster.map((group) => (
                  <section key={group.key} className="triffskills-roster-group">
                    <h4 className={group.key === "Pinned" ? "is-pinned" : statusClass(group.key as Readiness)}>
                      {group.key !== "Pinned" ? (
                        <ProgressMark readiness={group.key as Readiness} fill={STATUS[group.key as Readiness].sampleFill} />
                      ) : (
                        <span aria-hidden="true">★</span>
                      )}
                      {group.label}
                      <span>{group.characterIds.length}</span>
                    </h4>
                    {group.characterIds.map((characterId) => {
                      const character = charactersById.get(characterId);
                      if (!character) return null;
                      return (
                        <React.Fragment key={characterId}>
                          <CharacterRow
                            character={character}
                            cell={cellFor(characterId)}
                            planName={selectedPlanName}
                            pinned={pinnedSet.has(characterId)}
                            groups={state.characterGroups}
                            expanded={expandedIds.has(characterId)}
                            detail={details.get(characterId)}
                            onToggleExpand={() => toggleExpand(characterId)}
                            onTogglePin={() => togglePinned(characterId)}
                            onToggleGroup={(groupName) => toggleGroupMembership(characterId, groupName)}
                            onForget={() => forgetCharacter(characterId)}
                            onCopyMissing={() => copyMissingSkills(characterId)}
                          />
                        </React.Fragment>
                      );
                    })}
                  </section>
                ))
              )}
            </div>
          </div>
        </main>
      </section>

      {importOpen ? (
        <ImportPlanModal
          planName={planName}
          planText={planText}
          preview={preview}
          previewRequest={previewRequest}
          commitRequest={commitRequest}
          importError={importError}
          onNameChange={(value) => { setPlanName(value); invalidatePlanPreview(); }}
          onTextChange={(value) => { setPlanText(value); invalidatePlanPreview(); }}
          onPreview={previewPlan}
          onCommit={commitPlan}
          onClose={closeImportModal}
        />
      ) : null}
    </div>
  );
}
