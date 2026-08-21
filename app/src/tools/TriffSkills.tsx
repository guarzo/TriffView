import React, { useEffect, useRef, useState } from "react";
import { onNativeMessage, postNative } from "../nativeBridge.js";
import "./TriffSkills.css";

type Readiness = "Ready" | "Training" | "Locked" | "Missing" | "Unknown" | "Unscored";

type Character = {
  characterId: number;
  characterName: string;
  fetchedUtc?: string | null;
  error?: string;
  needsReauth?: boolean;
  stale?: boolean;
};

type Plan = { name: string; requirementCount: number };

type MatrixCell = {
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
};

const READINESS_ORDER: Readiness[] = ["Ready", "Training", "Locked", "Missing", "Unknown", "Unscored"];
const STATUS: Record<Readiness, { label: string; description: string; sampleFill?: number }> = {
  Ready: { label: "Ready", description: "All requirements are active", sampleFill: 1 },
  Training: { label: "Training", description: "A requirement is in the queue", sampleFill: 0.5 },
  Locked: { label: "Locked", description: "Trained requirements are not active", sampleFill: 1 },
  Missing: { label: "Missing", description: "Requirements still need training", sampleFill: 0 },
  Unknown: { label: "Unknown", description: "A skill could not be resolved" },
  Unscored: { label: "Unscored", description: "No successful character fetch yet" },
};
const LEVELS = ["", "I", "II", "III", "IV", "V"];

const requestId = () =>
  typeof crypto?.randomUUID === "function"
    ? crypto.randomUUID().replaceAll("-", "")
    : `${Date.now().toString(36)}${Math.random().toString(36).slice(2)}`;

function send(type: string, payload: Record<string, unknown> = {}) {
  return postNative({ type, ...payload });
}

function formatDate(value?: string | null) {
  if (!value) return "";
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? "" : date.toLocaleString();
}

function quantizedProgress(progress?: number) {
  return progress === undefined ? undefined : Math.round(progress * 4) / 4;
}

function statusClass(readiness: Readiness) {
  return `is-${readiness.toLowerCase()}`;
}

function ProgressMark({ readiness, fill }: { readiness: Readiness; fill?: number }) {
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
  const inputRevisionRef = useRef(0);
  const previewRequestRef = useRef<{ requestId: string; revision: number } | null>(null);
  const commitRequestRef = useRef("");
  const previewRef = useRef<Preview | null>(null);

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
            <button type="button" onClick={() => send("triffskills:open-plans-folder")}>Open plans folder</button>
            <button type="button" onClick={() => send("triffskills:refresh-plans")}>Reload plans</button>
            <button type="button" onClick={() => { setImportError(""); setImportOpen(true); }}>Import local plan</button>
          </nav>

          {state.authInProgress ? <p className="triffskills-rail-status" aria-live="polite">Waiting for EVE SSO…</p> : null}
          {!state.authConfigured ? (
            <div className="triffview-warning triffskills-sso-warning">
              <strong>SSO not configured</strong>
              <span>This build needs an EVE SSO client ID before authentication can finish.</span>
            </div>
          ) : null}

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
              <p>Rows are plans, columns are characters. Select a cell, plan, or character for detail.</p>
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
            <div className="triffskills-empty"><p>Roster coming in the next task.</p></div>
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
