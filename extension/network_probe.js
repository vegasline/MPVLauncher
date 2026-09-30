/**
 * MPV Launcher - Network probe (page world)
 *
 * Injected into the page so it can observe the requests an HLS/DASH player
 * makes after load. A content script runs in an isolated world and cannot see
 * this, so results are published through a dataset attribute on <html>, which
 * both worlds share.
 *
 * What it captures:
 *   - fetch() and XHR responses whose URL or Content-Type is a playlist
 *   - the blob: URL that MSE handed to <video>, mapped back to the manifest
 *     that created it (hls.js always plays via blob:, never a real URL)
 *
 * Every hook is best-effort and never changes page behaviour.
 */

(function () {
  "use strict";

  if (window.__mpvProbeInstalled) return;
  window.__mpvProbeInstalled = true;

  var MAX_ENTRIES = 60;
  var streams = [];          // captured playlist/file URLs
  var blobMap = {};          // blob URL -> manifest URL
  var lastStreamUrl = "";    // most recent playlist, used as the blob's owner
  var published = false;

  function isPlaylist(url) {
    if (!url) return false;
    return /\.(?:m3u8|mpd)(?:$|[?#])/i.test(url) ||
           /[?&](?:format|type|output)=(?:hls|m3u8|dash|mpd)\b/i.test(url) ||
           /\/(?:hls|manifest|playlist|master)(?:[/?&#]|$)/i.test(url);
  }

  function isMediaFile(url) {
    return /\.(?:mp4|webm|mkv|avi|mov|flv|m4v|ogv)(?:$|[?#])/i.test(url);
  }

  function publish() {
    try {
      var el = document.documentElement;
      if (!el) return;
      el.dataset.mpvStreams = streams.join("\n");
      el.dataset.mpvBlobMap = JSON.stringify(blobMap);
      published = true;
    } catch (e) {
      /* dataset unavailable - nothing we can do */
    }
  }

  function remember(url) {
    if (!url || typeof url !== "string") return;
    if (streams.indexOf(url) !== -1) return;
    streams.push(url);
    if (streams.length > MAX_ENTRIES) streams.shift();
    if (isPlaylist(url)) lastStreamUrl = url;
    publish();
  }

  function typeLooksLikeMedia(type) {
    if (!type) return false;
    return /mpegurl|dash\+xml|application\/vnd\.apple\.mpegurl|application\/x-mpegurl/i.test(type);
  }

  // ------------------------------------------------------------------- fetch
  try {
    var nativeFetch = window.fetch;
    window.fetch = function (input, init) {
      var requestUrl = typeof input === "string" ? input : (input && input.url) || "";
      var promise = nativeFetch.apply(this, arguments);

      if (isPlaylist(requestUrl) || isMediaFile(requestUrl)) {
        // Remember immediately: the player may have already fired the request.
        remember(requestUrl);
      }

      return promise.then(function (response) {
        try {
          if (typeLooksLikeMedia(response.headers && response.headers.get("content-type"))) {
            remember(response.url || requestUrl);
          } else if (isPlaylist(response.url)) {
            remember(response.url);
          }
        } catch (e) { /* opaque response */ }
        return response;
      }).catch(function (err) {
        throw err;
      });
    };
  } catch (e) { /* frozen fetch */ }

  // --------------------------------------------------------------------- XHR
  try {
    var nativeOpen = XMLHttpRequest.prototype.open;
    XMLHttpRequest.prototype.open = function (method, url) {
      try {
        this.__mpvUrl = typeof url === "string" ? url : (url && url.href) || "";
        if (isPlaylist(this.__mpvUrl) || isMediaFile(this.__mpvUrl)) remember(this.__mpvUrl);
      } catch (e) { /* ignore */ }
      return nativeOpen.apply(this, arguments);
    };

    var nativeSend = XMLHttpRequest.prototype.send;
    XMLHttpRequest.prototype.send = function () {
      var xhr = this;
      try {
        xhr.addEventListener("load", function () {
          try {
            if (xhr.responseURL && isPlaylist(xhr.responseURL)) remember(xhr.responseURL);
            var type = xhr.getResponseHeader && xhr.getResponseHeader("content-type");
            if (typeLooksLikeMedia(type) && xhr.__mpvUrl) remember(xhr.__mpvUrl);
          } catch (e) { /* ignore */ }
        }, { once: true });
      } catch (e) { /* ignore */ }
      return nativeSend.apply(this, arguments);
    };
  } catch (e) { /* frozen XHR */ }

  // ------------------------------------------------------ MSE blob -> manifest
  try {
    var nativeCreateObjectURL = URL.createObjectURL;
    URL.createObjectURL = function (obj) {
      var url = nativeCreateObjectURL.apply(this, arguments);
      try {
        // An object URL handed straight to a MediaSource belongs to whatever
        // playlist was fetched most recently - that is the playable stream.
        if (obj && typeof MediaSource !== "undefined" && (obj instanceof MediaSource || obj.sourceBuffers)) {
          if (lastStreamUrl) blobMap[url] = lastStreamUrl;
        }
      } catch (e) { /* ignore */ }
      publish();
      return url;
    };
  } catch (e) { /* frozen URL */ }

  publish();
})();
