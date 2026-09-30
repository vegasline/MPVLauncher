/**
 * MPV Launcher - network capture layer
 *
 * Runs alongside the native-messaging background script. Observes every HTTP
 * response in every tab and records the ones that are video manifests or
 * media files, so the popup can list them even when the page hides them from
 * the DOM (cross-origin players, Web Worker HLS, signed extension-less URLs).
 *
 * Listeners are registered synchronously at the top level because an MV3
 * service worker may be evicted and restarted at any time.
 */

var MPV_Network = (function () {
  "use strict";

  // Resolve the extension API here rather than borrowing background.js's `api`.
  // This file is loaded BEFORE background.js, so `api` does not exist yet and
  // referencing it would throw and silently leave the listener unregistered.
  var webext = (typeof browser !== "undefined" && browser && browser.webRequest)
    ? browser
    : (typeof chrome !== "undefined" ? chrome : null);

  // Chrome accepts "extraHeaders", Firefox rejects the option entirely.
  var isFirefox = typeof browser !== "undefined" && !!browser.runtime && !!(browser && browser.runtime);
  var isChrome = !isFirefox;

  var requestHeaderExtras = isChrome ? ["requestHeaders", "extraHeaders"] : ["requestHeaders"];
  var responseHeaderExtras = isChrome ? ["responseHeaders", "extraHeaders"] : ["responseHeaders"];

  // Guards against a second registration when the permission is granted later.
  var listenersInstalled = false;

  var HEADER_BLOCKLIST = ["cookie", "authorization", "set-cookie", "proxy-authorization"];
  var MAX_PER_TAB = 400;
  var PRUNE_AFTER_MS = 30 * 60 * 1000;

  // tabId -> array of captured items
  var byTab = new Map();
  // requestId -> request headers seen before the response arrived
  var pendingRequestHeaders = new Map();

  // Firefox runs the MV3 background as a non-persistent EVENT PAGE: after ~30s
  // idle the script is discarded and module-scope state is lost. Chromium keeps
  // its service worker alive far longer, which is why the miss only shows up in
  // Firefox. Captures are therefore mirrored into storage.session, which
  // survives the restart, and re-read when the script wakes back up.
  var STORAGE_KEY = "mpvNetworkCaptures";
  var restoreDone = false;
  var restorePromise = null;
  var saveTimer = -1;

  function hasWebRequest() {
    return !!(webext && webext.webRequest && webext.webRequest.onHeadersReceived);
  }

  /**
   * Host permissions are NOT granted automatically.
   *
   * Firefox shows them in the install prompt only from 127, and even then an
   * update that adds new host permissions is not shown at all (bug 1893232).
   * A user who declined - or who installed before the prompt existed - ends up
   * with a webRequest listener that sees nothing and fails silently. The
   * permission therefore has to be verified at runtime and requested on demand.
   */
  var hostPermission = { origins: ["<all_urls>"] };
  var hasHostPermission = false;
  var permissionChecked = false;

  function checkHostPermission() {
    if (permissionChecked) return Promise.resolve(hasHostPermission);
    permissionChecked = true;

    var perms = webext && webext.permissions;
    if (!perms || typeof perms.contains !== "function") {
      // No permissions API: assume the manifest grant is in effect (Chromium).
      hasHostPermission = true;
      return Promise.resolve(true);
    }

    try {
      var result = perms.contains(hostPermission);
      if (result && typeof result.then === "function") {
        return result.then(function (ok) {
          hasHostPermission = !!ok;
          return hasHostPermission;
        }).catch(function () { return false; });
      }
      hasHostPermission = !!result;
      return Promise.resolve(hasHostPermission);
    } catch (e) {
      hasHostPermission = false;
      return Promise.resolve(false);
    }
  }

  /** Asks the user for host access. Must be called from a user gesture. */
  function requestHostPermission() {
    var perms = webext && webext.permissions;
    if (!perms || typeof perms.request !== "function") {
      return Promise.resolve(false);
    }
    try {
      var result = perms.request(hostPermission);
      return Promise.resolve(result)
        .then(function (ok) {
          hasHostPermission = !!ok;
          permissionChecked = true;
          // register() returns the new state; keep `registered` in step so
          // callers reading it after the grant see the truth.
          registered = register();
          return hasHostPermission;
        })
        .catch(function () { return false; });
    } catch (e) {
      return Promise.resolve(false);
    }
  }

  function headerValue(headers, name) {
    if (!headers) return "";
    for (var i = 0; i < headers.length; i++) {
      if (headers[i].name.toLowerCase() === name) return headers[i].value || "";
    }
    return "";
  }

  /**
   * Request headers are kept so MPV / yt-dlp can be given the Referer,
   * User-Agent and Cookie the site demanded. Credentials are dropped.
   */
  function safeRequestHeaders(headers) {
    var out = [];
    if (!headers) return out;
    for (var i = 0; i < headers.length; i++) {
      var name = headers[i].name.toLowerCase();
      if (HEADER_BLOCKLIST.indexOf(name) !== -1) continue;
      if (name !== "referer" && name !== "referrer" && name !== "user-agent" && name !== "origin") continue;
      out.push({ name: headers[i].name, value: headers[i].value });
    }
    return out;
  }

  function itemsFor(tabId) {
    if (!byTab.has(tabId)) byTab.set(tabId, []);
    return byTab.get(tabId);
  }

  function sessionStore() {
    try {
      if (webext && webext.storage) {
        if (webext.storage.session) return webext.storage.session;
        if (webext.storage.local) return webext.storage.local;
      }
    } catch (e) { /* fall through */ }
    return null;
  }

  /** Mirrors the in-memory map into storage so it survives an event-page restart. */
  function schedulePersist() {
    if (saveTimer >= 0) clearTimeout(saveTimer);
    saveTimer = setTimeout(function () {
      saveTimer = -1;
      var store = sessionStore();
      if (!store || typeof store.set !== "function") return;
      try {
        var plain = {};
        byTab.forEach(function (items, tabId) { plain[tabId] = items; });
        var result = store.set({ [STORAGE_KEY]: plain });
        if (result && typeof result.catch === "function") result.catch(function () {});
      } catch (e) { /* storage full or unavailable: memory still works */ }
    }, 250);
  }

  /**
   * Re-reads captures saved by a previous instance of the background script.
   * Returns a promise so callers can wait for the read; storage.get is async
   * and getItems must not answer before the data is in memory.
   */
  function restoreOnce() {
    if (restorePromise) return restorePromise;
    restoreDone = true;

    var store = sessionStore();
    if (!store || typeof store.get !== "function") {
      restorePromise = Promise.resolve(false);
      return restorePromise;
    }

    restorePromise = new Promise(function (resolve) {
      var settled = false;
      function finish(ok) { if (!settled) { settled = true; resolve(ok); } }

      try {
        var result = store.get(STORAGE_KEY);

        if (result && typeof result.then === "function") {
          result.then(function (data) { merge(data && data[STORAGE_KEY]); finish(true); })
                 .catch(function () { finish(false); });
          return;
        }

        if (typeof result === "object" && result) {
          merge(result[STORAGE_KEY]);
          finish(true);
          return;
        }

        // callback-style storage (older WebExtension shapes)
        if (typeof result === "undefined" && typeof store.get === "function") {
          try {
            var maybeCallback = store.get(STORAGE_KEY, function (data) { merge(data && data[STORAGE_KEY]); finish(true); });
            if (maybeCallback && typeof maybeCallback.then === "function") return;
            if (result === undefined && maybeCallback === undefined) { finish(false); return; }
          } catch (e) { /* not callback style */ }
        }
        finish(false);
      } catch (e) {
        finish(false);
      }
    });

    return restorePromise;
  }

  function merge(saved) {
    if (!saved || typeof saved !== "object") return;
    Object.keys(saved).forEach(function (tabId) {
      var list = saved[tabId];
      if (!Array.isArray(list) || !list.length) return;
      var target = itemsFor(parseInt(tabId, 10));
      list.forEach(function (item) {
        if (!item || !item.url) return;
        if (item.headers && !Array.isArray(item.headers)) item.headers = [];
        for (var i = 0; i < target.length; i++) {
          if (target[i].url === item.url) return;
        }
        target.push(item);
      });
    });
  }

  function push(tabId, item) {
    var items = itemsFor(tabId);
    for (var i = 0; i < items.length; i++) {
      if (items[i].url === item.url) {
        // a later observation may carry details the first one lacked
        var existing = items[i];
        if (!existing.sizeBytes && item.sizeBytes) existing.sizeBytes = item.sizeBytes;
        if (!existing.referer && item.referer) existing.referer = item.referer;
        if (!existing.headers.length && item.headers.length) existing.headers = item.headers;
        existing.seenAt = Date.now();
        return false;
      }
    }
    item.seenAt = Date.now();
    items.push(item);
    if (items.length > MAX_PER_TAB) items.splice(0, items.length - MAX_PER_TAB);
    schedulePersist();
    return true;
  }

  function onResponseHeaders(details) {
    if (!hasWebRequest()) return;
    // tabId -1 is a browser-internal or extension request, not a page.
    if (details.tabId === undefined || details.tabId < 0) return;
    if (!details.url || !/^https?:/i.test(details.url)) return;
    if (typeof MPV_MEDIA_TYPES === "undefined") return;

    var contentType = headerValue(details.responseHeaders, "content-type");
    var hit = mpvClassifyResponse(details.url, contentType);
    if (!hit) {
      pendingRequestHeaders.delete(details.requestId);
      return;
    }

    var stashed = pendingRequestHeaders.get(details.requestId) || [];
    pendingRequestHeaders.delete(details.requestId);

    push(details.tabId, {
      url: details.url,
      typeId: hit.type.id,
      typeLabel: hit.type.label,
      category: hit.type.category,
      matchedBy: hit.matchedBy,
      sizeBytes: parseInt(headerValue(details.responseHeaders, "content-length"), 10) || 0,
      referer: details.documentUrl || details.originUrl || details.initiator || "",
      headers: stashed,
      source: "network"
    });
  }

  function onBeforeSendHeaders(details) {
    if (!hasWebRequest()) return;
    if (details.tabId === undefined || details.tabId < 0) return;
    // The request hook fires BEFORE the response, so the item usually does not
    // exist yet. Stash the headers and let onHeadersReceived attach them.
    pendingRequestHeaders.set(details.requestId, safeRequestHeaders(details.requestHeaders));
  }

  function register() {
    if (!hasWebRequest()) return false;

    // Without host access webRequest delivers nothing. Registering anyway
    // would look healthy while silently capturing zero events, so check first.
    if (!hasHostPermission) return false;

    if (listenersInstalled) return true;
    try {
      webext.webRequest.onHeadersReceived.addListener(
        onResponseHeaders, { urls: ["<all_urls>"] }, responseHeaderExtras);
      if (webext.webRequest.onBeforeSendHeaders) {
        webext.webRequest.onBeforeSendHeaders.addListener(
          onBeforeSendHeaders, { urls: ["<all_urls>"] }, requestHeaderExtras);
      }
      listenersInstalled = true;
      return true;
    } catch (e) {
      // A rejected option (e.g. extraHeaders on Firefox) must not leave the
      // capture half-installed; retry without it before giving up.
      try {
        webext.webRequest.onHeadersReceived.addListener(
          onResponseHeaders, { urls: ["<all_urls>"] }, ["responseHeaders"]);
        if (webext.webRequest.onBeforeSendHeaders) {
          webext.webRequest.onBeforeSendHeaders.addListener(
            onBeforeSendHeaders, { urls: ["<all_urls>"] }, ["requestHeaders"]);
        }
        listenersInstalled = true;
        return true;
      } catch (e2) {
        return false;
      }
    }
  }

  // Resolve permissions, then install the listeners. Both are asynchronous, so
  // `registered` is only final once this settles; callers await the check.
  var registered = false;
  var ready = checkHostPermission().then(function (ok) {
    registered = register();
    return registered;
  });
  restoreOnce();

  function prune() {
    var cutoff = Date.now() - PRUNE_AFTER_MS;
    var changed = false;
    byTab.forEach(function (items, tabId) {
      var kept = items.filter(function (i) { return i.seenAt > cutoff; });
      if (kept.length !== items.length) { byTab.set(tabId, kept); changed = true; }
    });
    if (changed) schedulePersist();
  }

  function clearTab(tabId) {
    byTab.delete(tabId);
    schedulePersist();
  }

  return {
    /** Resolves once the permission check and listener setup have finished. */
    ready: function () { return ready; },
    /** Live registration state, valid after ready() has settled. */
    isRegistered: function () { return registered; },
    /** Whether host access has actually been granted. */
    hasHostPermission: function () { return hasHostPermission; },
    /** Asks the user for host access; must be triggered by a user gesture. */
    requestPermission: requestHostPermission,
    /**
     * Raw capture list for a tab, newest last.
     * Async because captures from a previous event-page instance have to be
     * read back from storage first.
     */
    getItems: function (tabId) {
      return restoreOnce().then(function () {
        prune();
        return byTab.get(tabId) || [];
      });
    },
    /**
     * Synchronous view of every tab's captures, for callers that already hold
     * the URL and must not wait. Returns only what is in memory.
     */
    peekAll: function () {
      var out = [];
      byTab.forEach(function (items) {
        for (var i = 0; i < items.length; i++) out.push(items[i]);
      });
      return out;
    },
    clearTab: clearTab,
    clearAll: function () { byTab.clear(); schedulePersist(); }
  };
})();
