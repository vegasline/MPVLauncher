// Original implementation, MIT licensed.

/**
 * Stream metadata for the popup: how big a playlist's best variant is, and
 * whether a media URL still answers at all.
 *
 * Two rules keep this honest. A playlist is fetched as text, so bandwidth and
 * resolution come from the manifest itself. A media URL is only probed for its
 * status code, and only 404/410 count as dead: 401/403 usually mean the CDN
 * wanted the page's Referer, which a fetch from here cannot send, so calling
 * those dead would be a lie.
 */
var MPV_StreamMeta = (function () {
  "use strict";

  var TIMEOUT_MS = 5000;

  // A page can list hundreds of links, and probing them all at once looks like
  // a small flood to the CDN. Six at a time, and sixty unique URLs per popup.
  var MAX_CONCURRENT = 6;
  var MAX_PROBES = 60;

  var cache = {};
  var pending = {};
  var queue = [];
  var active = 0;
  var started = 0;

  function isPlaylist(url) {
    return /\.m3u8($|[?#])/i.test(url) || /\.mpd($|[?#])/i.test(url);
  }

  function isDash(url) {
    return /\.mpd($|[?#])/i.test(url);
  }

  /** Reads a response as text with a hard timeout; null when it failed. */
  function readText(url, limitBytes) {
    var controller = typeof AbortController === "function" ? new AbortController() : null;
    var timer = controller ? setTimeout(function () { controller.abort(); }, TIMEOUT_MS) : null;

    return fetch(url, {
      credentials: "omit",
      cache: "no-store",
      signal: controller ? controller.signal : undefined
    }).then(function (response) {
      if (!response.ok && response.status !== 206) return null;
      return response.text().then(function (text) {
        return text.length > limitBytes ? text.slice(0, limitBytes) : text;
      });
    }).catch(function () {
      return null;
    }).then(function (text) {
      if (timer) clearTimeout(timer);
      return text;
    });
  }

  /** Status only: the body is cancelled as soon as the headers arrive. */
  function probeStatus(url) {
    var controller = typeof AbortController === "function" ? new AbortController() : null;
    var timer = controller ? setTimeout(function () { controller.abort(); }, TIMEOUT_MS) : null;

    return fetch(url, {
      credentials: "omit",
      cache: "no-store",
      method: "GET",
      signal: controller ? controller.signal : undefined
    }).then(function (response) {
      if (timer) clearTimeout(timer);
      try { if (response.body && response.body.cancel) response.body.cancel(); } catch (e) { /* already closed */ }
      return response.status;
    }).catch(function () {
      if (timer) clearTimeout(timer);
      return 0;
    });
  }

  function parseHls(text) {
    var best = null;
    var lines = text.split(/\r?\n/);
    for (var i = 0; i < lines.length; i++) {
      if (lines[i].indexOf("#EXT-X-STREAM-INF") !== 0) continue;

      var bandwidth = /(?:AVERAGE-)?BANDWIDTH=(\d+)/i.exec(lines[i]);
      var resolution = /RESOLUTION=(\d+)x(\d+)/i.exec(lines[i]);
      var bps = bandwidth ? parseInt(bandwidth[1], 10) : 0;
      if (!bps || (best && best.bandwidth >= bps)) continue;

      best = {
        bandwidth: bps,
        width: resolution ? parseInt(resolution[1], 10) : 0,
        height: resolution ? parseInt(resolution[2], 10) : 0
      };
    }
    return best;
  }

  function parseDash(text) {
    var best = null;
    var re = /<Representation\b[^>]*>/gi;
    var match;
    while ((match = re.exec(text)) !== null) {
      var tag = match[0];
      var bandwidth = /bandwidth="(\d+)"/i.exec(tag);
      if (!bandwidth) continue;
      var bps = parseInt(bandwidth[1], 10);
      if (!bps || (best && best.bandwidth >= bps)) continue;

      var width = /width="(\d+)"/i.exec(tag);
      var height = /height="(\d+)"/i.exec(tag);
      best = {
        bandwidth: bps,
        width: width ? parseInt(width[1], 10) : 0,
        height: height ? parseInt(height[1], 10) : 0
      };
    }
    return best;
  }

  /** "5200000" -> "5.2 Mbps"; small numbers stay in kbps. */
  function formatBitrate(bps) {
    if (!bps || bps <= 0) return "";
    if (bps >= 1000000) return (bps / 1000000).toFixed(bps >= 10000000 ? 0 : 1) + " Mbps";
    return Math.round(bps / 1000) + " kbps";
  }

  function probeOnce(item) {
    var url = item.url;

    var playlist = isPlaylist(url)
      ? readText(url, 256 * 1024).then(function (text) {
          if (!text) return { dead: true };
          var best = isDash(url) ? parseDash(text) : parseHls(text);
          return { bandwidth: best ? best.bandwidth : 0, width: best ? best.width : 0, height: best ? best.height : 0 };
        })
      : probeStatus(url).then(function (status) {
          // Only a definitive answer counts. 0 is "we never learned why".
          if (status === 404 || status === 410) return { dead: true };
          return {};
        });

    return playlist.then(function (meta) {
      if (item.sizeBytes && !meta.bandwidth) meta.sizeBytes = item.sizeBytes;
      return meta;
    });
  }

  /** Runs one probe, allowing only MAX_CONCURRENT to be in flight. */
  function withLimit(task) {
    return new Promise(function (resolve) {
      queue.push(function () {
        return task().then(
          function (value) { resolve(value); },
          function () { resolve({}); }
        );
      });
      pump();
    });
  }

  function pump() {
    while (active < MAX_CONCURRENT && queue.length > 0) {
      var job = queue.shift();
      active++;
      job().then(function () {
        active--;
        pump();
      });
    }
  }

  return {
    /** Metadata for one item, cached per URL. Never rejects. */
    probe: function (item) {
      if (!item || !item.url) return Promise.resolve({});
      if (cache[item.url]) return Promise.resolve(cache[item.url]);
      if (pending[item.url]) return pending[item.url];

      // Past the budget the popup simply shows no badge: it stays honest, and
      // one very long list cannot turn into a hundred requests.
      if (started >= MAX_PROBES) {
        cache[item.url] = {};
        return Promise.resolve(cache[item.url]);
      }
      started++;

      var run = withLimit(function () { return probeOnce(item); })
        .then(function (meta) {
          delete pending[item.url];
          cache[item.url] = meta;
          return meta;
        });

      pending[item.url] = run;
      return run;
    },

    formatBitrate: formatBitrate,

    /** "1080p · 5.2 Mbps"; an empty string when nothing is known. */
    describe: function (meta) {
      if (!meta) return "";
      var parts = [];
      if (meta.height) parts.push(meta.height + "p");
      var rate = formatBitrate(meta.bandwidth);
      if (rate) parts.push(rate);
      return parts.join(" · ");
    }
  };
})();
