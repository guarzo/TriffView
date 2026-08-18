import { useEffect, useState } from "react";
import { onNativeMessage, postNative } from "../nativeBridge.js";

// Local copy of TriffViewSettings.jsx's module-level `send` helper (:123-125),
// so this hook's native-message surface stays self-contained.
function send(type, payload = {}) {
  postNative({ type, ...payload });
}

export function useCombatLogs() {
  const [lastFight, setLastFight] = useState(null);
  const [exportState, setExportState] = useState({ result: null, error: "", busy: false });
  const [range, setRange] = useState({ from: "", to: "" });
  const [webhook, setWebhook] = useState({
    configured: false,
    description: "",
    testResult: null,
    error: "",
  });
  // 'save' | 'clear' | 'test' | null - which webhook action is in flight, so the
  // three buttons share one busy flag but CombatLogExport can still tell a
  // successful *save* apart from a successful clear or test.
  const [webhookAction, setWebhookAction] = useState(null);
  const [uploadState, setUploadState] = useState({ result: null, error: "", busy: false });

  // Omitting the range tells the native side to use the last detected fight.
  function exportCombatLogs(explicitRange) {
    setExportState({ result: null, error: "", busy: true });
    send(
      "triffview:export-combat-logs",
      explicitRange ? { fromUtc: explicitRange.from, toUtc: explicitRange.to } : {}
    );
  }

  // Same "omit the range for the last fight" convention as exportCombatLogs.
  function uploadCombatLogs(explicitRange) {
    setUploadState({ result: null, error: "", busy: true });
    send(
      "triffview:upload-combat-logs",
      explicitRange ? { fromUtc: explicitRange.from, toUtc: explicitRange.to } : {}
    );
  }

  function saveWebhook(url) {
    setWebhookAction("save");
    setWebhook((current) => ({ ...current, error: "" }));
    send("triffview:set-combat-log-webhook", { url });
  }

  function clearWebhook() {
    setWebhookAction("clear");
    setWebhook((current) => ({ ...current, error: "" }));
    send("triffview:clear-combat-log-webhook");
  }

  function testWebhook() {
    setWebhookAction("test");
    setWebhook((current) => ({ ...current, error: "" }));
    send("triffview:test-combat-log-webhook");
  }

  useEffect(() => {
    const unsubscribe = onNativeMessage((message) => {
      if (message?.type === "triffview:state") {
        setLastFight(message.lastFight || null);
        // configured/description are refreshed on every periodic state post;
        // testResult is left alone so a "Send test" outcome isn't wiped out by
        // the next routine post before the user has read it.
        const nextWebhook = message.combatLogWebhook || {};
        setWebhook((current) => ({
          ...current,
          configured: Boolean(nextWebhook.configured),
          description: nextWebhook.description || "",
        }));
      }

      if (message?.type === "triffview:combat-log-export") {
        // Cancelling the save dialog posts { cancelled: true } with no `result`
        // (TriffViewSubsystem.cs:1696). Keying this off `message.result` being
        // present (instead of the message type) would leave export buttons
        // disabled forever after a cancel.
        setExportState({ result: message.result || null, error: "", busy: false });
      }

      if (message?.type === "triffview:error" && message.action === "export-combat-logs") {
        setExportState({ result: null, error: message.message || "Export failed.", busy: false });
      }

      if (message?.type === "triffview:combat-log-webhook") {
        setWebhook({
          configured: Boolean(message.configured),
          description: message.description || "",
          testResult: message.testResult || null,
          error: "",
        });
        setWebhookAction(null);
      }

      if (
        message?.type === "triffview:error"
        && [
          "set-combat-log-webhook",
          "clear-combat-log-webhook",
          "test-combat-log-webhook",
        ].includes(message.action)
      ) {
        setWebhook((current) => ({
          ...current,
          error: message.message || "Webhook action failed.",
        }));
        setWebhookAction(null);
      }

      if (message?.type === "triffview:combat-log-upload") {
        // No cancelled branch: the upload path has no dialog and no user-facing
        // cancel, so the native side never sends one. A timeout arrives here as
        // a result with succeeded === false.
        setUploadState({
          result: message.result || null,
          error: "",
          busy: false,
        });
      }

      if (message?.type === "triffview:error" && message.action === "upload-combat-logs") {
        setUploadState({ result: null, error: message.message || "Upload failed.", busy: false });
      }
    });

    return () => {
      unsubscribe();
    };
  }, []);

  useEffect(() => {
    send("triffview:get-state");
  }, []);

  return {
    lastFight,
    exportState,
    range,
    setRange,
    exportCombatLogs,
    uploadCombatLogs,
    webhookState: { ...webhook, action: webhookAction },
    saveWebhook,
    clearWebhook,
    testWebhook,
    uploadState,
  };
}
