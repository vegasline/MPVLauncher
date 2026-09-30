/**
 * MPV Launcher - background script
 */

// MV3 service worker: pull in the sidecar scripts. Firefox loads the same
// files through manifest background.scripts, where importScripts is absent.
if (typeof importScripts === "function" && typeof MPV_MEDIA_TYPES === "undefined") {
  try {
    importScripts("media_types.js", "network_capture.js");
  } catch (e) {
    // Firefox path - files already loaded by the manifest.
  }
}

const HOST_NAME = "com.mpv.launcher";
const api = (typeof browser !== "undefined" && browser.runtime && browser.runtime.sendNativeMessage)
  ? browser
  : chrome;

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

function sendNative(url, referrer) {
  const payload = { url: url, referrer: referrer || "" };

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
    if (!targetUrl) {
      sendResponse({ success: false, error: "Invalid or empty URL." });
      return false;
    }

    sendNative(targetUrl, referrer)
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

  if (message.action === "get_filter_defaults") {
    sendResponse({
      success: true,
      defaults: typeof MPV_FILTER_DEFAULTS !== "undefined" ? MPV_FILTER_DEFAULTS : {}
    });
    return false;
  }

  if (message.action === "apply_filter") {
    if (typeof MPV_Network === "undefined") {
      sendResponse({ success: false, items: [] });
      return false;
    }
    const filters = message.filters || {};
    MPV_Network.getItems(message.tabId).then((raw) => {
      const kept = raw.filter((item) => mpvPassesFilter(item, filters));
      sendResponse({ success: true, items: kept, total: raw.length });
    }).catch(() => {
      sendResponse({ success: true, items: [], total: 0 });
    });
    return true;
  }

  return false;
});
