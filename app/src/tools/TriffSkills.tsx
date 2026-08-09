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

const REAUTH_HINT =
  "Needs re-authentication for esi-skills.read_skills.v1 and esi-skills.read_skillqueue.v1. Use Add character to reauthorize.";

function send(type: string, payload: Record<string, unknown> = {}) {
  postNative({ type, ...payload });
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

export default function TriffSkills() {
  const [state, setState] = useState<TriffSkillsState>(EMPTY_STATE);
  const [error, setError] = useState("");
  const [confirmForgetId, setConfirmForgetId] = useState(0);

  useEffect(() => {
    const unsubscribe = onNativeMessage((message) => {
      if (message?.type === "triffskills:state") {
        setState({
          ...EMPTY_STATE,
          ...(message as TriffSkillsState),
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
  const hasMatrix = state.characters.length > 0 && state.plans.length > 0;

  function confirmForget(characterId: number) {
    send("triffskills:forget-character", { characterId });
    setConfirmForgetId(0);
  }

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
          </div>
        </aside>

        <div className="triffview-section-content" data-hud-scroll>
          <header className="triffview-section-header">
            <div>
              <h2>Skill plan readiness</h2>
              <p>Every character is scored against every plan in your plans folder. Failures show per row or per cell.</p>
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
            <div className="triffskills-matrix-scroll" data-hud-scroll>
              <table className="triffskills-matrix">
                <thead>
                  <tr>
                    <th scope="col">Character</th>
                    {state.plans.map((plan) => (
                      <th scope="col" key={plan.name}>
                        <span>{plan.name}</span>
                        <small>{plan.requirementCount} skills</small>
                      </th>
                    ))}
                  </tr>
                </thead>
                <tbody>
                  {state.characters.map((character) => (
                    <tr key={character.characterId} className={isDegraded(character) ? "is-degraded" : ""}>
                      <th scope="row">
                        <CharacterCell
                          character={character}
                          confirming={confirmForgetId === character.characterId}
                          onAskForget={() => setConfirmForgetId(character.characterId)}
                          onCancelForget={() => setConfirmForgetId(0)}
                          onConfirmForget={() => confirmForget(character.characterId)}
                        />
                      </th>
                      {state.plans.map((plan) => (
                        <td key={plan.name}>
                          <MatrixCell
                            entry={cells.get(matrixKey(character.characterId, plan.name)) || null}
                            stale={isDegraded(character)}
                          />
                        </td>
                      ))}
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          ) : null}
        </div>
      </section>
    </div>
  );
}

function CharacterCell({
  character,
  confirming,
  onAskForget,
  onCancelForget,
  onConfirmForget,
}: {
  character: SkillCharacter;
  confirming: boolean;
  onAskForget: () => void;
  onCancelForget: () => void;
  onConfirmForget: () => void;
}) {
  const degraded = isDegraded(character);
  const stamp = formatUtc(character.fetchedUtc);

  return (
    <div className="triffskills-character">
      <strong>{character.characterName}</strong>
      <small>{stamp ? `${degraded ? "Last good" : "Updated"} ${stamp}` : "Never fetched"}</small>

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

function MatrixCell({ entry, stale }: { entry: MatrixEntry | null; stale: boolean }) {
  if (!entry) {
    return (
      <div className="triffskills-cell is-unscored">
        <span className="triffskills-state">
          <em aria-hidden="true">?</em>
          Not scored
        </span>
        <small>No result for this character and plan yet. Use Refresh characters.</small>
      </div>
    );
  }

  // A readiness value outside the three known strings is an anomaly, not a
  // confident "Missing" - route it to the same unscored/unknown vocabulary
  // the null-entry branch above uses, rather than silently reading as Missing.
  const meta = READINESS_META[entry.readiness] ?? { glyph: "?", label: "Unknown", className: "is-unscored" };
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

      {entry.readiness === "Training" ? (
        <small>{eta ? `Done ${eta}` : "Training, ETA unknown (queue paused)"}</small>
      ) : null}

      {entry.readiness === "Missing" && missing.length ? (
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
