chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  if (sender.id !== chrome.runtime.id || message?.type !== "status") return;

  let diagnostic;
  try {
    diagnostic = JSON.parse(
      document.documentElement.getAttribute("data-flow-local-diagnostic") ||
        "null",
    );
  } catch {}

  sendResponse({
    state:
      typeof diagnostic?.state === "string"
        ? diagnostic.state.slice(0, 80)
        : "not-loaded",
  });
});
