const SCRIPT_ID = "flow-helper";

const registration = {
  id: SCRIPT_ID,
  matches: ["https://flow.google.com/*"],
  js: ["hook.js"],
  runAt: "document_start",
  world: "MAIN",
  persistAcrossSessions: true,
};

chrome.runtime.onInstalled.addListener(async ({ reason }) => {
  if (reason !== "install" && reason !== "update") return;
  try {
    const registered = await chrome.scripting.getRegisteredContentScripts({
      ids: [SCRIPT_ID],
    });
    if (registered.length)
      await chrome.scripting.updateContentScripts([registration]);
    else if (reason === "install")
      await chrome.scripting.registerContentScripts([registration]);
  } catch (e) {
    console.error("Flow Helper setup failed:", e.message);
  }
});

chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  if (
    sender.id !== chrome.runtime.id ||
    sender.tab ||
    message?.type !== "setEnabled" ||
    typeof message.enabled !== "boolean"
  )
    return;

  (async () => {
    const registered = await chrome.scripting.getRegisteredContentScripts({
      ids: [SCRIPT_ID],
    });

    if (message.enabled && !registered.length)
      await chrome.scripting.registerContentScripts([registration]);

    if (!message.enabled && registered.length)
      await chrome.scripting.unregisterContentScripts({ ids: [SCRIPT_ID] });

    sendResponse({ ok: true });
  })().catch((e) => sendResponse({ ok: false, error: e.message }));

  return true;
});
