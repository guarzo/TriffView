import React, { useEffect, useRef, useState } from "react";
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

// The range inputs are minute-granular and read as UTC by the native parser
// (DateTime.TryParse with AssumeUniversal), so this must match the placeholder
// format exactly. Local time never enters into it - every accessor is getUTC*.
function formatUtcInput(date) {
  const pad = (value) => String(value).padStart(2, "0");
  return (
    `${date.getUTCFullYear()}-${pad(date.getUTCMonth() + 1)}-${pad(date.getUTCDate())}` +
    ` ${pad(date.getUTCHours())}:${pad(date.getUTCMinutes())}`
  );
}

// Rounds the window end up to the next minute. Truncating "now" would exclude
// anything logged in the current partial minute - the tail of a fight that just
// ended, which is exactly what these buttons are for.
function quickRange(hours) {
  const now = new Date();
  const end = new Date(now);
  if (end.getUTCSeconds() > 0 || end.getUTCMilliseconds() > 0) {
    end.setUTCMinutes(end.getUTCMinutes() + 1);
  }
  end.setUTCSeconds(0, 0);
  const start = new Date(end.getTime() - hours * 60 * 60 * 1000);
  return { from: formatUtcInput(start), to: formatUtcInput(end) };
}

function formatBytes(value) {
  const bytes = Number(value);
  if (!Number.isFinite(bytes) || bytes < 0) return "";
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(0)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}

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
  const [webhookInput, setWebhookInput] = useState("");
  // Once a webhook is configured, the URL field and its explanation are just
  // clutter - collapse down to the configured summary and only bring the
  // field back if the user asks to replace it.
  const [replacing, setReplacing] = useState(false);
  // Only a successful *save* should clear the typed URL - if this fired on
  // any action finishing, a successful "Send test" or "Clear" would wipe out
  // an edit the user had not saved yet.
  const prevActionRef = useRef(null);
  useEffect(() => {
    if (prevActionRef.current === "save" && webhookState.action === null && !webhookState.error) {
      setWebhookInput("");
      setReplacing(false);
    }
    prevActionRef.current = webhookState.action;
  }, [webhookState.action, webhookState.error]);

  const webhookBusy = webhookState.action !== null;
  const showWebhookForm = !webhookState.configured || replacing;
  // Export and upload both compress the archive on the native side, so
  // either one running blocks the other rather than racing two builds.
  const runBusy = exportState.busy || uploadState.busy;
  const uploadDisabled = runBusy || !webhookState.configured;

  return (
    <div className="triff-combat-export">
      <p className="triffview-muted">
        Packages the EVE game logs covering a fight into a zip you can upload to Discord for
        eve-intel. Game logs only, copied as-is, plus a small manifest listing your characters.
        Chat logs are never included.
      </p>

      <div className={`triff-combat-export-webhook${showWebhookForm ? "" : " is-configured"}`}>
        {showWebhookForm ? (
          <>
            <div className="triff-combat-export-webhook-head">
              <h3>Discord destination</h3>
              <span className={webhookState.configured ? "triff-alert-status is-on" : "triff-alert-status"}>
                {webhookState.configured ? "Connected" : "Not connected"}
              </span>
            </div>
            <p className="triffview-muted">
              Paste a webhook URL to enable one-click uploads. It is stored in Windows Credential
              Manager, never written to triffview-settings.json, and never sent back to this screen
              once saved.
            </p>
            <div className="triff-combat-export-webhook-row">
              <Field label="Webhook URL">
                <input
                  type="password"
                  placeholder="https://discord.com/api/webhooks/…"
                  value={webhookInput}
                  disabled={webhookBusy}
                  onChange={(event) => setWebhookInput(event.target.value)}
                />
              </Field>
              <button
                type="button"
                disabled={webhookBusy || !webhookInput.trim()}
                onClick={() => onSaveWebhook(webhookInput.trim())}
              >
                Save
              </button>
              {replacing && webhookState.configured ? (
                <button
                  type="button"
                  disabled={webhookBusy}
                  onClick={() => {
                    setReplacing(false);
                    setWebhookInput("");
                  }}
                >
                  Cancel
                </button>
              ) : null}
            </div>
          </>
        ) : (
          <div className="triff-combat-export-webhook-summary">
            <span className="triff-alert-status is-on">Connected</span>
            <p className="triff-combat-export-webhook-desc">{webhookState.description}</p>
            <div className="triff-combat-export-webhook-actions">
              <button type="button" disabled={webhookBusy} onClick={onTestWebhook}>
                Send test
              </button>
              <button type="button" disabled={webhookBusy} onClick={() => setReplacing(true)}>
                Replace
              </button>
              <button type="button" disabled={webhookBusy} onClick={onClearWebhook}>
                Clear
              </button>
            </div>
          </div>
        )}
        {webhookState.testResult ? (
          <p
            className={
              webhookState.testResult.ok ? "triff-combat-export-result" : "triff-combat-export-error"
            }
          >
            {webhookState.testResult.message}
          </p>
        ) : null}
        {webhookState.error ? <p className="triff-combat-export-error">{webhookState.error}</p> : null}
      </div>

      <div className="triff-combat-export-paths">
        <div className="triff-combat-export-path is-detected">
          <div className="triff-combat-export-path-head">
            <h3>Last fight</h3>
            <span className={lastFight ? "triff-alert-status is-on" : "triff-alert-status"}>
              {lastFight ? "Ready" : "None"}
            </span>
          </div>
          <p className="triffview-muted">Export the fight TriffAlerts most recently detected.</p>
          <div className="triff-combat-export-path-body">
            <span className="triff-combat-export-path-info">
              {lastFight
                ? `${formatUtcWindow(lastFight.startUtc, lastFight.endUtc)}${
                    lastFight.characters?.length ? ` - ${lastFight.characters.join(", ")}` : ""
                  }`
                : "No fight detected yet. Alerts must be enabled, and only fights seen while TriffView has been running are detected - use a time range for anything older."}
            </span>
            {!lastFight && onOpenAlerts ? (
              <button type="button" className="triff-alert-summary-action" onClick={onOpenAlerts}>
                Open Alerts settings
              </button>
            ) : null}
          </div>
          <div className="triff-combat-export-path-actions">
            <button
              type="button"
              className="primary-action"
              disabled={!lastFight || runBusy}
              onClick={() => onExport(null)}
            >
              Export last fight
            </button>
            <button
              type="button"
              className="primary-action"
              disabled={!lastFight || uploadDisabled}
              onClick={() => onUpload(null)}
            >
              Upload to Discord
            </button>
          </div>
        </div>

        <div className="triff-combat-export-path is-manual">
          <div className="triff-combat-export-path-head">
            <h3>Time range</h3>
          </div>
          <p className="triffview-muted">For a fight from before TriffView was started.</p>
          <div className="triff-combat-export-quick">
            <span>Quick range:</span>
            <button type="button" disabled={runBusy} onClick={() => onRangeChange(quickRange(1))}>
              Last 1 hour
            </button>
            <button type="button" disabled={runBusy} onClick={() => onRangeChange(quickRange(2))}>
              Last 2 hours
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
          </div>
          <div className="triff-combat-export-path-actions">
            <button
              type="button"
              className="primary-action"
              disabled={!range.from || !range.to || runBusy}
              onClick={() => onExport(range)}
            >
              Export range
            </button>
            <button
              type="button"
              className="primary-action"
              disabled={!range.from || !range.to || uploadDisabled}
              onClick={() => onUpload(range)}
            >
              Upload to Discord
            </button>
          </div>
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
        {uploadState.result ? (
          uploadState.result.succeeded ? (
            <p className="triff-combat-export-result">
              Uploaded {uploadState.result.fileCount} log
              {uploadState.result.fileCount === 1 ? "" : "s"}
              {uploadState.result.characters?.length
                ? ` (${uploadState.result.characters.join(", ")})`
                : ""}{" "}
              to Discord - {formatBytes(uploadState.result.zipBytes)} sent.
              {uploadState.result.droppedFileCount
                ? ` ${uploadState.result.droppedFileCount} further matching log${
                    uploadState.result.droppedFileCount === 1 ? " was" : "s were"
                  } left out at the file limit - narrow the time range to cover them.`
                : ""}
            </p>
          ) : (
            <p className="triff-combat-export-error">{uploadState.result.message}</p>
          )
        ) : null}
        {uploadState.error ? <p className="triff-combat-export-error">{uploadState.error}</p> : null}
      </div>
    </div>
  );
}

export default CombatLogExport;
