import React, { useState } from "react";
import { postNative } from "../../nativeBridge.js";
import {
  CellDetail,
  Character,
  CharacterGroupDef,
  LEVELS,
  MatrixCell,
  REQUIREMENT_LABEL,
  REQUIREMENT_ORDER,
  formatDate,
  isDegraded,
  statusClass,
  statusLine,
} from "../TriffSkills";

/**
 * This row is the only surface for forgetting or re-authenticating a
 * character once the detail panel is gone. A character with no row, or one
 * whose row cannot be expanded, cannot be repaired or removed — its stale
 * credentials are stuck in settings with nothing to clear them. That applies
 * to `Unscored` characters too: every newly added character, until its first
 * refresh, has no matrix cell at all and must still get a working row.
 */
export default function CharacterRow({
  character,
  cell,
  planName,
  pinned,
  groups,
  expanded,
  detail,
  onToggleExpand,
  onTogglePin,
  onToggleGroup,
  onForget,
  onCopyMissing,
}: {
  character: Character;
  cell?: MatrixCell;
  planName: string;
  pinned: boolean;
  groups: CharacterGroupDef[];
  expanded: boolean;
  detail?: CellDetail;
  onToggleExpand: () => void;
  onTogglePin: () => void;
  onToggleGroup: (groupName: string) => void;
  onForget: () => void;
  onCopyMissing: () => void;
}) {
  const [confirmForget, setConfirmForget] = useState(false);
  const readiness = cell?.readiness ?? "Unscored";
  const memberGroupNames = groups.filter((group) => group.characterIds.includes(character.characterId)).map((group) => group.name);
  const degraded = isDegraded(character);
  // The detail request is keyed to a specific plan; if the fetched detail is
  // for a different plan (a switch happened while collapsed) this row shows
  // its loading state until the parent re-fetches for the current plan.
  const detailForPlan = detail && detail.planName === planName ? detail : undefined;
  const outstanding = detailForPlan?.requirements.filter((requirement) => requirement.state !== "Active") ?? [];

  return (
    <div className="triffskills-roster-row-wrap">
      <div className="triffskills-roster-row">
        <button
          type="button"
          className={`triffskills-pin-btn${pinned ? " is-pinned" : ""}`}
          aria-pressed={pinned}
          aria-label={pinned ? `Unpin ${character.characterName}` : `Pin ${character.characterName}`}
          onClick={onTogglePin}
        >
          ★
        </button>
        <button
          type="button"
          className="triffskills-roster-toggle"
          aria-expanded={expanded}
          onClick={onToggleExpand}
        >
          <span className="triffskills-roster-chevron" aria-hidden="true">{expanded ? "▾" : "▸"}</span>
          <span className="triffskills-roster-name">{character.characterName}</span>
          {memberGroupNames.length ? (
            <span className="triffskills-roster-tags">
              {memberGroupNames.map((name) => (
                <span className="triffskills-roster-tag" key={name}>{name}</span>
              ))}
            </span>
          ) : null}
          {degraded ? <span className="triffskills-roster-badge is-stale">Stale</span> : null}
          <span className={`triffskills-roster-status ${statusClass(readiness)}`}>
            {statusLine(cell, readiness)}
          </span>
        </button>
      </div>

      {expanded ? (
        <div className="triffskills-roster-expansion" data-hud-scroll>
          {character.needsReauth ? (
            <div className="triffskills-detail-flag is-error">
              <span>EVE sign-in must be refreshed before this character can update.</span>
              <button type="button" onClick={() => postNative({ type: "triffskills:auth" })}>Re-authenticate</button>
            </div>
          ) : null}
          {character.error ? <div className="triffskills-detail-flag is-error">{character.error}</div> : null}

          <div className="triffskills-detail-meta">
            <span>Last successful fetch</span>
            <strong>{formatDate(character.fetchedUtc) || "Never fetched"}</strong>
          </div>

          <section className="triffskills-expansion-requirements" aria-label="Outstanding requirements">
            {!detailForPlan ? (
              <p className="triffskills-detail-loading">Loading requirement details…</p>
            ) : outstanding.length ? (
              <>
                {detailForPlan.queueTimingUnknown ? <div className="triffskills-detail-flag">Queue timing is unavailable or paused.</div> : null}
                <div className="triffskills-requirement-groups">
                  {REQUIREMENT_ORDER.map((requirementState) => {
                    const items = outstanding.filter((requirement) => requirement.state === requirementState);
                    if (!items.length) return null;
                    return (
                      <section className={`is-${requirementState.toLowerCase()}`} key={requirementState}>
                        <h4>{REQUIREMENT_LABEL[requirementState]}<span>{items.length}</span></h4>
                        <ul>
                          {items.map((requirement) => (
                            <li key={requirement.skillName}>
                              <div>
                                <strong>{requirement.skillName}</strong>
                                <small>Required {LEVELS[requirement.requiredLevel] || requirement.requiredLevel}</small>
                              </div>
                              <div className="triffskills-levels">
                                <span>Active {requirement.activeLevel ?? 0}</span>
                                <span>Trained {requirement.trainedLevel ?? 0}</span>
                              </div>
                              {requirement.queuedFinishUtc ? <small>Queue finish {formatDate(requirement.queuedFinishUtc)}</small> : null}
                              {requirement.queueTimingUnknown ? <small>Queue timing unavailable</small> : null}
                            </li>
                          ))}
                        </ul>
                      </section>
                    );
                  })}
                </div>
                <button type="button" onClick={onCopyMissing}>Copy missing skills</button>
              </>
            ) : (
              <p className="triffskills-detail-loading">{readiness === "Ready" ? "All requirements are active." : "No outstanding requirements for this plan."}</p>
            )}
          </section>

          {groups.length ? (
            <section className="triffskills-expansion-groups" aria-label="Group membership">
              <h4>Groups</h4>
              {groups.map((group) => (
                <label key={group.name} className="triffskills-group-checkbox">
                  <input
                    type="checkbox"
                    checked={group.characterIds.includes(character.characterId)}
                    onChange={() => onToggleGroup(group.name)}
                  />
                  <span>{group.name}</span>
                </label>
              ))}
            </section>
          ) : null}

          <div className="triffskills-detail-actions">
            {confirmForget ? (
              <div className="triffskills-forget-confirm" role="alert">
                <span>Delete this character’s stored TriffSkills token?</span>
                <div>
                  <button type="button" onClick={() => setConfirmForget(false)}>Keep character</button>
                  <button
                    type="button"
                    className="danger-action"
                    onClick={() => {
                      setConfirmForget(false);
                      onForget();
                    }}
                  >
                    Forget character
                  </button>
                </div>
              </div>
            ) : (
              <button type="button" className="danger-action" onClick={() => setConfirmForget(true)}>Forget character</button>
            )}
          </div>
        </div>
      ) : null}
    </div>
  );
}
