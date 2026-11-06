const SCRIPT_ID = "flow-helper";
const FLOW_ORIGIN = "https://flow.google.com/";

const toggle = document.getElementById("enabled");
const status = document.getElementById("status");
const error = document.getElementById("error");
const reload = document.getElementById("reload");

const MESSAGES = {
  enabled: "Включено. Перезагрузите вкладку Flow.",
  disabled: "Выключено для новых загрузок. Перезагрузите открытые вкладки Flow.",
  applied: "Помощник работает на этой вкладке.",
  armed: "Готово. Жду ответ от Flow.",
  mismatch: "Flow мог обновиться. Нужен апдейт помощника.",
  unavailable: "Не удалось установить перехватчик. Проверьте конфликты расширений и перезагрузите Flow.",
  wrongTab: "Открой вкладку Flow, чтобы использовать помощник.",
};

let tab;

async function init() {
  toggle.disabled = true;

  const registered = await chrome.scripting.getRegisteredContentScripts({
    ids: [SCRIPT_ID],
  });
  toggle.checked = registered.length > 0;

  [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
  const isFlow = tab?.url?.startsWith(FLOW_ORIGIN);
  reload.hidden = !isFlow;

  status.textContent = toggle.checked ? MESSAGES.enabled : MESSAGES.disabled;
  if (!isFlow && toggle.checked) status.textContent = MESSAGES.wrongTab;

  if (isFlow && toggle.checked) {
    try {
      const result = await chrome.tabs.sendMessage(tab.id, { type: "status" });
      status.textContent = result?.state?.startsWith("schema")
        ? MESSAGES.mismatch
        : result?.state === "hook-unavailable"
          ? MESSAGES.unavailable
          : result?.state === "applied"
            ? MESSAGES.applied
            : result?.state === "armed"
              ? MESSAGES.armed
              : MESSAGES.enabled;
    } catch {
      status.textContent = MESSAGES.enabled;
    }
  }

  toggle.disabled = false;
}

toggle.addEventListener("change", async () => {
  toggle.disabled = true;
  error.textContent = "";

  try {
    const response = await chrome.runtime.sendMessage({
      type: "setEnabled",
      enabled: toggle.checked,
    });
    if (!response?.ok)
      throw new Error(response?.error || "Не удалось сохранить настройку");
    status.textContent =
      "Сохранено. Перезагрузите открытые вкладки Flow.";
  } catch (e) {
    toggle.checked = !toggle.checked;
    error.textContent = e.message;
  } finally {
    toggle.disabled = false;
  }
});

document.getElementById("open").onclick = async () => {
  error.textContent = "";
  try {
    await chrome.tabs.create({ url: FLOW_ORIGIN });
  } catch (e) {
    error.textContent = e.message;
  }
};

reload.onclick = async () => {
  error.textContent = "";
  try {
    const current = await chrome.tabs.get(tab.id);
    if (!current.url?.startsWith(FLOW_ORIGIN))
      throw new Error("Сначала выберите вкладку Flow.");

    const url = new URL(current.url);
    if (/\/unsupported-country\/?$/.test(url.pathname)) {
      url.pathname = url.pathname.replace(/unsupported-country\/?$/, "");
      await chrome.tabs.update(tab.id, { url: url.href });
    } else {
      await chrome.tabs.reload(tab.id);
    }
    window.close();
  } catch (e) {
    error.textContent = e.message;
  }
};

init().catch((e) => {
  error.textContent = e.message;
});
