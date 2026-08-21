import React, { useState } from "react";

/**
 * A single manual-group chip in the filter row: toggles the group filter on
 * click, and carries a "⋯" affordance for rename/delete. Rename and delete
 * are fire-and-forget against native, same as pin/membership toggles
 * elsewhere in this tab — native is authoritative and re-posts full state on
 * both success and failure, so nothing here is optimistic beyond closing its
 * own popover.
 */
export default function GroupChip({
  name,
  active,
  onToggle,
  onRename,
  onDelete,
}: {
  name: string;
  active: boolean;
  onToggle: () => void;
  onRename: (nextName: string) => void;
  onDelete: () => void;
}) {
  const [menuOpen, setMenuOpen] = useState(false);
  const [renaming, setRenaming] = useState(false);
  const [renameValue, setRenameValue] = useState(name);
  const [confirmDelete, setConfirmDelete] = useState(false);

  function startRename() {
    setRenameValue(name);
    setRenaming(true);
    setMenuOpen(false);
  }

  function submitRename() {
    const trimmed = renameValue.trim();
    setRenaming(false);
    if (trimmed && trimmed !== name) onRename(trimmed);
  }

  return (
    <span className="triffskills-group-chip">
      <button
        type="button"
        className={`triffskills-chip${active ? " is-on" : ""}`}
        onClick={onToggle}
      >
        {name}
      </button>
      <button
        type="button"
        className="triffskills-group-chip-menu-btn"
        aria-label={`Manage group ${name}`}
        aria-expanded={menuOpen}
        onClick={() => { setMenuOpen((open) => !open); setConfirmDelete(false); }}
      >
        ⋯
      </button>

      {renaming ? (
        <form
          className="triffskills-group-chip-popover triffskills-group-chip-rename"
          onSubmit={(event) => { event.preventDefault(); submitRename(); }}
        >
          <input
            autoFocus
            maxLength={32}
            value={renameValue}
            aria-label={`Rename group ${name}`}
            onChange={(event) => setRenameValue(event.target.value)}
          />
          <div>
            <button type="submit" className="primary-action" disabled={!renameValue.trim()}>Save</button>
            <button type="button" onClick={() => setRenaming(false)}>Cancel</button>
          </div>
        </form>
      ) : null}

      {menuOpen && !renaming ? (
        <div className="triffskills-group-chip-popover" role="menu">
          <button type="button" role="menuitem" onClick={startRename}>Rename</button>
          <button
            type="button"
            role="menuitem"
            className="danger-action"
            onClick={() => { setMenuOpen(false); setConfirmDelete(true); }}
          >
            Delete
          </button>
        </div>
      ) : null}

      {confirmDelete ? (
        <div className="triffskills-group-chip-popover triffskills-forget-confirm" role="alert">
          <span>Delete group “{name}”?</span>
          <div>
            <button type="button" onClick={() => setConfirmDelete(false)}>Keep group</button>
            <button
              type="button"
              className="danger-action"
              onClick={() => { setConfirmDelete(false); onDelete(); }}
            >
              Delete group
            </button>
          </div>
        </div>
      ) : null}
    </span>
  );
}
