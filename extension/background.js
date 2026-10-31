/**
 * MPV Launcher - background script
 */

// MV3 service worker: pull in the sidecar scripts. Firefox loads the same
// files through manifest background.scripts, where importScripts is absent.
if (typeof importScripts === "function" && typeof MPV_MEDIA_TYPES === "undefined") {
  try {
    importScripts("media_types.js", "network_capture.js", "themes.js");
  } catch (e) {
    // Firefox path - files already loaded by the manifest.
  }
}

const HOST_NAME = "com.mpv.launcher";
const api = (typeof browser !== "undefined" && browser.runtime && browser.runtime.sendNativeMessage)
  ? browser
  : chrome;

// The toolbar icon is tinted with the theme's accent, and that tint used to be
// rebuilt only while the popup was open: a browser restart dropped it and the
// packaged colour came back until the extension was clicked once.
//
// A top-level call is not enough on Chromium. The MV3 service worker is not
// started when the browser starts - it starts on the first event - so at launch
// nothing runs and the toolbar keeps manifest's default icon. runtime.onStartup
// is that first event: it fires once per browser start, which is exactly when
// the icon has to be repainted. The other two cover the rest: a worker restart
// mid-session (top level) and a fresh install or update (onInstalled).
function repaintToolbarIcon() {
  if (typeof MPV_Themes === "undefined") return;
  try {
    MPV_Themes.restoreIcon();
  } catch (e) {
    // A browser that refuses the repaint keeps the packaged icon.
  }
}

repaintToolbarIcon();

for (const event of [api.runtime && api.runtime.onStartup,
                      api.runtime && api.runtime.onInstalled]) {
  try {
    if (event && typeof event.addListener === "function") event.addListener(repaintToolbarIcon);
  } catch (e) {
    // An event this browser does not implement is not worth failing over.
  }
}

// Belt and braces: a restored session can deliver a window event before
// onStartup arrives. The first focus repaints once, then stays quiet.
let iconChecked = false;
try {
  api.windows.onFocusChanged.addListener(function () {
    if (iconChecked) return;
    iconChecked = true;
    repaintToolbarIcon();
  });
} catch (e) {
  // No windows API in this browser: onStartup is enough.
}

/**
 * Finds the headers of the request that actually fetched this media URL.
 *
 * Some CDNs answer 403 unless the playback presents the same User-Agent the
 * page used, so those two headers are forwarded to mpv. The Referer is
 * forwarded only as the browser actually sent it - never guessed. Guessing
 * breaks page URLs outright: yt-dlp answers "Unsupported URL" for an embed
 * page given --referer pointing at the embed host instead of the site that
 * embeds it.
 *
 * Credentials are deliberately not included: cookies and Authorization are
 * dropped in the capture layer and never leave the browser. A video behind a
 * login will therefore not play from mpv, which is the intended trade-off.
 */
function findCapturedRequest(url) {
  if (typeof MPV_Network === "undefined" || !url) return null;
  const items = MPV_Network.peekAll();
  for (let i = items.length - 1; i >= 0; i--) {
    if (items[i] && items[i].url === url) return items[i];
  }
  return null;
}

function headerValue(headers, name) {
  if (!headers) return "";
  for (let i = 0; i < headers.length; i++) {
    if (headers[i].name && headers[i].name.toLowerCase() === name) {
      return headers[i].value || "";
    }
  }
  return "";
}

function sendNative(url, referrer, title) {
  const payload = { url: url, referrer: referrer || "" };

  // The tab title is the site's own heading ("Paws & Palaces 4. Bölüm İzle"),
  // which is what the launcher's history should show - the media URL is a
  // stream behind a player and says nothing about what was being watched.
  if (title) payload.title = String(title).slice(0, 300);

  // Attach whatever the capture layer recorded for this exact URL.
  const captured = findCapturedRequest(url);
  if (captured) {
    const ua = headerValue(captured.headers, "user-agent");
    const ref = headerValue(captured.headers, "referer");

    if (ua) payload.userAgent = ua;
    // prefer the Referer the page really sent over the tab URL
    if (ref) payload.referrer = ref;
    else if (captured.referer && /^https?:/i.test(captured.referer)) {
      payload.referrer = captured.referer;
    }
  }

  if (typeof browser !== "undefined" && browser.runtime && browser.runtime.sendNativeMessage) {
    return browser.runtime.sendNativeMessage(HOST_NAME, payload);
  }

  return new Promise((resolve, reject) => {
    chrome.runtime.sendNativeMessage(HOST_NAME, payload, (response) => {
      const err = chrome.runtime.lastError;
      if (err) reject(new Error(err.message || "Native host could not be started."));
      else resolve(response);
    });
  });
}

function updateBadge(tabId, count) {
  api.storage.local.get(["showBadge"], (result) => {
    const show = result && typeof result.showBadge === "boolean" ? result.showBadge : true;
    const text = (!show || count <= 0) ? "" : String(count);

    if (api.action && api.action.setBadgeText) {
      api.action.setBadgeText({ tabId: tabId, text: text });
      api.action.setBadgeBackgroundColor({ tabId: tabId, color: "#6366f1" });
    } else if (api.browserAction && api.browserAction.setBadgeText) {
      api.browserAction.setBadgeText({ tabId: tabId, text: text });
      api.browserAction.setBadgeBackgroundColor({ tabId: tabId, color: "#6366f1" });
    }
  });
}

api.runtime.onMessage.addListener((message, sender, sendResponse) => {
  if (message.action === "open_in_mpv") {
    const targetUrl = message.url;
    const referrer = message.referrer || (sender && sender.tab ? sender.tab.url : "");
    // Prefer the title the caller measured (the popup knows its tab); fall back
    // to the sender's tab. A content-script message has both, a popup message
    // only the first.
    const pageTitle = message.title || (sender && sender.tab ? sender.tab.title : "");
    if (!targetUrl) {
      sendResponse({ success: false, error: "Invalid or empty URL." });
      return false;
    }

    sendNative(targetUrl, referrer, pageTitle)
      .then((response) => {
        if (response && response.status === "error") {
          sendResponse({ success: false, error: response.message || "Unknown host error." });
        } else {
          sendResponse({ success: true, data: response });
        }
      })
      .catch((err) => {
        sendResponse({
          success: false,
          error: err && err.message ? err.message : "Native host could not be started."
        });
      });

    return true;
  }

  if (message.action === "update_badge") {
    const tabId = message.tabId || (sender && sender.tab ? sender.tab.id : null);
    if (tabId) {
      updateBadge(tabId, message.count || 0);
    }
    sendResponse({ success: true });
    return false;
  }

  if (message.action === "toggle_badge") {
    const tabId = message.tabId || (sender && sender.tab ? sender.tab.id : null);
    if (tabId) {
      updateBadge(tabId, message.showBadge ? (message.count || 0) : 0);
    }
    sendResponse({ success: true });
    return false;
  }

  // ------------------------------------------------- network capture queries
  if (message.action === "get_network_media") {
    if (typeof MPV_Network === "undefined") {
      sendResponse({ success: true, available: false, items: [] });
      return false;
    }
    const tabId = message.tabId;
    // getItems is async: captures saved by a previous event-page instance are
    // read back from storage first.
    MPV_Network.getItems(tabId).then((items) => {
      sendResponse({ success: true, available: MPV_Network.isRegistered(), items: items });
    }).catch(() => {
      sendResponse({ success: true, available: MPV_Network.isRegistered(), items: [] });
    });
    return true;
  }

  if (message.action === "clear_network_media") {
    if (typeof MPV_Network !== "undefined") {
      if (typeof message.tabId === "number") MPV_Network.clearTab(message.tabId);
      else MPV_Network.clearAll();
    }
    sendResponse({ success: true });
    return false;
  }

  if (message.action === "get_permission_status") {
    if (typeof MPV_Network === "undefined") {
      sendResponse({ success: true, available: false, hostPermission: false });
      return false;
    }
    Promise.resolve(MPV_Network.ready())
      .then(() => sendResponse({
        success: true,
        available: MPV_Network.isRegistered(),
        hostPermission: MPV_Network.hasHostPermission()
      }))
      .catch(() => sendResponse({ success: true, available: false, hostPermission: false }));
    return true;
  }

  if (message.action === "request_permission") {
    if (typeof MPV_Network === "undefined") {
      sendResponse({ success: false, hostPermission: false });
      return false;
    }
    MPV_Network.requestPermission().then((granted) => {
      sendResponse({
        success: granted,
        hostPermission: granted,
        available: MPV_Network.isRegistered()
      });
    }).catch(() => {
      sendResponse({ success: false, hostPermission: false, available: false });
    });
    return true;
  }

  return false;
});
