import React from "react";
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

function formatBytes(value) {
  const bytes = Number(value);
  if (!Number.isFinite(bytes) || bytes < 0) return "";
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(0)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}

function CombatLogExport({ lastFight, exportState, range, onRangeChange, onExport }) {
  return (
    <div className="triffview-subsection">
      <div className="triff-alert-history-head">
        <h4>Combat log export</h4>
      </div>
      <p className="triffview-muted">
        Packages the EVE game logs covering a fight into a zip you can upload to Discord for
        eve-intel. Game logs only, copied as-is, plus a small manifest listing your characters.
        Chat logs are never included.
      </p>
      {lastFight ? (
        <p className="triff-combat-export-window">
          Last fight <strong>{formatUtcWindow(lastFight.startUtc, lastFight.endUtc)}</strong>
          {lastFight.characters?.length ? ` - ${lastFight.characters.join(", ")}` : ""}
        </p>
      ) : (
        <p className="triffview-muted">
          No fight detected yet. Alerts must be enabled, and only fights seen while TriffView
          has been running are detected - use the time range below for anything older.
        </p>
      )}
      <div className="triff-combat-export-actions">
        <button
          type="button"
          disabled={!lastFight || exportState.busy}
          onClick={() => onExport(null)}
        >
          Export last fight
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
        <button
          type="button"
          disabled={!range.from || !range.to || exportState.busy}
          onClick={() => onExport(range)}
        >
          Export range
        </button>
      </div>
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
    </div>
  );
}

export default CombatLogExport;
