import React, { useEffect, useRef, useState } from "react";
import { onNativeMessage, postNative } from "../../nativeBridge.js";

type Diagnostic = { line?: number; message: string };

type ClipboardPreview = {
  ok: boolean;
  requirementCount: number;
  diagnostics: Diagnostic[];
};

const requestId = () =>
  typeof crypto?.randomUUID === "function"
    ? crypto.randomUUID().replaceAll("-", "")
    : `${Date.now().toString(36)}${Math.random().toString(36).slice(2)}`;

function send(type: string, payload: Record<string, unknown> = {}) {
  return postNative({ type, ...payload });
}

/**
 * Imports a plan straight from the OS clipboard. Unlike the textarea import
 * modal, the plan text never touches the web layer: native reads the
 * clipboard, previews it, and holds the pending preview keyed to a
 * requestId/revision minted once on open.
 *
 * Three rules — each protects against a design that looked fine and broke:
 *
 *  1. `canCommit` reads only `ok` (the plan) and the name field, never
 *     `nameHint`/nameError. `ok`/diagnostics describe the plan; name/nameError
 *     describe the candidate name — separate axes. A `# CON` clipboard title
 *     comes back ok with a real requirement count and an empty, rejected
 *     name: one corrected field away from committable.
 *  2. Editing the name never bumps a revision or re-requests a preview. The
 *     clipboard text lives natively and was never sent to the web, so an
 *     invalidated preview here could never be rebuilt.
 *  3. A commit rejected for a bad name leaves the native pending preview in
 *     place, so retrying under a corrected name with the same
 *     requestId/revision succeeds without re-reading the clipboard.
 */
export default function ImportClipboardDialog({
  onImported,
  onClose,
}: {
  onImported: () => void;
  onClose: () => void;
}) {
  const [phase, setPhase] = useState<"loading" | "ready" | "failed">("loading");
  const [loadError, setLoadError] = useState("");
  const [preview, setPreview] = useState<ClipboardPreview | null>(null);
  const [name, setName] = useState("");
  const [nameHint, setNameHint] = useState("");
  const [collision, setCollision] = useState(false);
  const [saving, setSaving] = useState(false);
  const requestRef = useRef<{ requestId: string; revision: number } | null>(null);

  useEffect(() => {
    const pending = { requestId: requestId(), revision: 0 };
    requestRef.current = pending;
    if (!send("triffskills:import-clipboard", pending)) {
      setPhase("failed");
      setLoadError("The native TriffView bridge is unavailable.");
    }

    const unsubscribe = onNativeMessage((message: any) => {
      const current = requestRef.current;
      if (!current) return;

      if (
        message?.type === "triffskills:clipboard-preview" &&
        message.requestId === current.requestId &&
        message.revision === current.revision
      ) {
        setPhase("ready");
        setPreview({
          ok: Boolean(message.ok),
          requirementCount: message.requirementCount ?? 0,
          diagnostics: message.diagnostics || [],
        });
        setName(message.name || "");
        setNameHint(message.nameError || "");
        return;
      }

      if (
        message?.type === "triffskills:plan-commit" &&
        message.requestId === current.requestId &&
        message.revision === current.revision
      ) {
        setSaving(false);
        if (message.ok) {
          onImported();
          return;
        }
        if (message.collision) {
          setCollision(true);
          return;
        }
        // Rule 3: the native pending preview survives this rejection —
        // leave the dialog open with `preview` intact so Import can retry.
        setNameHint(message.message || "Plan import failed.");
        return;
      }

      if (message?.type === "triffskills:error" && message.action === "import-clipboard") {
        setPhase("failed");
        setLoadError(message.message || "The clipboard could not be read.");
      }
    });

    return unsubscribe;
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // Rule 1: canCommit must not consult nameHint — a name rejection still
  // leaves a valid, committable plan preview behind it.
  const canCommit = Boolean(preview?.ok) && name.trim().length > 0 && !saving;

  function changeName(value: string) {
    // Rule 2: no invalidation here — requestId/revision are untouched and no
    // preview request is sent.
    setName(value);
    setNameHint("");
    setCollision(false);
  }

  function commit(replace: boolean) {
    const current = requestRef.current;
    if (!current || !canCommit) return;
    setSaving(true);
    if (
      !send("triffskills:commit-plan", {
        requestId: current.requestId,
        revision: current.revision,
        replace,
        name: name.trim(),
      })
    ) {
      setSaving(false);
      setNameHint("The native TriffView bridge is unavailable.");
    }
  }

  return (
    <div className="triffview-modal-backdrop triffskills-modal-backdrop">
      <section
        className="triffview-hotkey-modal triffskills-clipboard-modal"
        role="dialog"
        aria-modal="true"
        aria-labelledby="triffskills-clipboard-title"
      >
        <header>
          <div>
            <h3 id="triffskills-clipboard-title">Import from clipboard</h3>
            <p>Reads the current system clipboard. Nothing is saved until you confirm a name below.</p>
          </div>
          <button type="button" disabled={saving} onClick={onClose}>Close</button>
        </header>

        <div className="triffskills-import-body" data-hud-scroll>
          {phase === "loading" ? <p className="triffskills-detail-loading">Reading the clipboard…</p> : null}
          {phase === "failed" ? <div className="triffskills-import-error" role="alert">{loadError}</div> : null}

          {phase === "ready" ? (
            <>
              <label>
                <span>Plan name</span>
                <input
                  autoFocus
                  maxLength={128}
                  disabled={saving}
                  value={name}
                  onChange={(event) => changeName(event.target.value)}
                />
              </label>
              {nameHint ? <div className="triffskills-import-error" role="alert">{nameHint}</div> : null}

              {preview ? (
                <section className={`triffskills-preview${preview.ok ? " is-valid" : " is-invalid"}`} aria-live="polite">
                  {preview.ok ? (
                    <strong>
                      {preview.requirementCount} validated requirement{preview.requirementCount === 1 ? "" : "s"}
                    </strong>
                  ) : (
                    <>
                      <strong>Plan validation found issues</strong>
                      <ul data-hud-scroll>
                        {preview.diagnostics.map((diagnostic, index) => (
                          <li key={index}>{diagnostic.line ? `Line ${diagnostic.line}: ` : ""}{diagnostic.message}</li>
                        ))}
                      </ul>
                    </>
                  )}
                </section>
              ) : null}

              {collision ? (
                <div className="triffskills-collision" role="alert">
                  A plan named <strong>{name.trim()}</strong> already exists. Replacing it overwrites that local file.
                </div>
              ) : null}
            </>
          ) : null}
        </div>

        <footer className="triffskills-import-actions">
          <button type="button" disabled={saving} onClick={onClose}>Cancel</button>
          <button
            type="button"
            disabled={!canCommit}
            className={collision ? "danger-action" : "primary-action"}
            onClick={() => commit(collision)}
          >
            {saving ? "Saving…" : collision ? "Replace local plan" : "Import"}
          </button>
        </footer>
      </section>
    </div>
  );
}
