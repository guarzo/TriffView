import React from "react";
import CombatLogExport from "./CombatLogExport.jsx";

function CombatLogs({ combatLogs, onOpenAlerts }) {
  const {
    lastFight,
    exportState,
    range,
    setRange,
    exportCombatLogs,
    uploadCombatLogs,
    webhookState,
    saveWebhook,
    clearWebhook,
    testWebhook,
    uploadState,
  } = combatLogs;

  return (
    <div className="triffview-settings">
      <div className="triffview-section-content" data-hud-scroll>
        <header className="triffview-section-header">
          <h2>Combat logs</h2>
        </header>
        <div className="triffview-panel">
          <CombatLogExport
            lastFight={lastFight}
            exportState={exportState}
            range={range}
            onRangeChange={setRange}
            onExport={exportCombatLogs}
            webhookState={webhookState}
            onSaveWebhook={saveWebhook}
            onClearWebhook={clearWebhook}
            onTestWebhook={testWebhook}
            uploadState={uploadState}
            onUpload={uploadCombatLogs}
            onOpenAlerts={onOpenAlerts}
          />
        </div>
      </div>
    </div>
  );
}

export default CombatLogs;
