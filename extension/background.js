// YDownloader: Rechtsklick auf Link, Bild, Video, Audio oder markierten Text schickt den Link an die App.
// Läuft in Chrome (Service Worker) und Firefox (Event-Seite). Kein Inhaltsskript, keine Seitenrechte:
// nur das Kontextmenü und der Native-Messaging-Host "ydownloader.host".
"use strict";

const api = globalThis.browser ?? globalThis.chrome;
const HOST = "ydownloader.host";
const MENU_ID = "ydownloader-download";
const BADGE_MS = 4000;

const text = (key, ...subs) => api.i18n.getMessage(key, subs) || key;

function createMenu() {
  // Erst leeren: onInstalled und onStartup können beide feuern, doppelte IDs wären ein Fehler.
  api.contextMenus.removeAll(() => {
    api.contextMenus.create({
      id: MENU_ID,
      title: text("menuDownload"),
      contexts: ["link", "image", "video", "audio", "selection"],
    });
  });
}

api.runtime.onInstalled.addListener(createMenu);
api.runtime.onStartup.addListener(createMenu);

async function sendToHost(message) {
  try {
    return await api.runtime.sendNativeMessage(HOST, message);
  } catch (e) {
    // Meist: Host nicht registriert ("Specified native messaging host not found").
    return { ok: false, error: "no-host", detail: String(e?.message ?? e) };
  }
}

let clearTimer = 0;
function showBadge(label, color, title) {
  api.action.setBadgeText({ text: label });
  api.action.setBadgeBackgroundColor({ color });
  api.action.setTitle({ title });
  clearTimeout(clearTimer);
  clearTimer = setTimeout(() => {
    api.action.setBadgeText({ text: "" });
    api.action.setTitle({ title: text("actionTitle") });
  }, BADGE_MS);
}

function showError(reply) {
  const known = { "no-host": "errNoHost", "no-link": "errNoLink", "app-unreachable": "errUnreachable" };
  showBadge("!", "#d93025", text(known[reply?.error] ?? "errGeneric"));
}

// Der Link, auf den geklickt wurde, hat Vorrang vor Bildquelle und markiertem Text.
api.contextMenus.onClicked.addListener(async (info) => {
  if (info.menuItemId !== MENU_ID) return;
  const payload = info.linkUrl || info.srcUrl || info.selectionText || "";
  if (!/https?:\/\//i.test(payload)) {
    showError({ error: "no-link" });
    return;
  }
  const reply = await sendToHost({ type: "add", text: payload });
  if (!reply?.ok) showError(reply);
});

// Klick auf das Symbol in der Werkzeugleiste: prüft die Verbindung zur App.
api.action.onClicked.addListener(async () => {
  const reply = await sendToHost({ type: "ping" });
  if (!reply?.ok) {
    showError(reply);
    return;
  }
  showBadge("OK", "#188038", text(reply.running ? "pingRunning" : "pingIdle", reply.version ?? "?"));
});
