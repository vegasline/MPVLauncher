/**
 * MPV Launcher - Media type registry
 *
 * Classification table shared by the background network listener and the
 * popup filter. Matching is done on the response Content-Type first (most
 * reliable) and on the URL path extension second.
 *
 * Loaded as a plain script (no ES modules) so the exact same file works as a
 * Chromium MV3 service-worker import and as a Firefox background script.
 */

var MPV_MEDIA_TYPES = [
  // ---------------------------------------------------------- manifests
  {
    id: "hls", label: "HLS", category: "stream",
    ext: ["m3u8"],
    ct: [
      "application/x-mpegurl",
      "application/vnd.apple.mpegurl",
      "audio/x-mpegurl",
      "audio/mpegurl",
      "application/mpegurl",
      "application/m3u8"
    ]
  },
  {
    id: "dash", label: "DASH", category: "stream",
    ext: ["mpd"],
    ct: ["application/dash+xml", "application/vnd.mpeg.dash+xml"]
  },
  {
    id: "hds", label: "HDS", category: "stream",
    ext: ["f4m"],
    ct: ["application/f4m"]
  },
  {
    id: "mss", label: "MSS", category: "stream",
    // Smooth Streaming keeps its manifest at "<path>/.ism/manifest"
    ext: ["ism/manifest"],
    ct: ["application/vnd.ms-sstr+xml"]
  },
  {
    id: "apple", label: "APPLE", category: "stream",
    ext: [],
    ct: ["application/vnd.apple.mpegurl.audio", "application/x-apple-binary-plist"]
  },

  // Subtitle formats (VTT/SRT/TTML) are intentionally not captured: MPV does
  // not consume them as a media source, so they would only add noise.

  // ------------------------------------------------------------- files
  {
    id: "mp4", label: "MP4", category: "files",
    ext: ["mp4", "m4v", "m4s"],
    ct: ["video/mp4", "video/x-m4v", "application/mp4"]
  },
  {
    id: "ts", label: "TS", category: "files",
    ext: ["ts", "m2t", "mts"],
    ct: ["video/mp2t", "video/mpeg", "video/mpeg2", "video/mp2t-stream"]
  },
  {
    id: "aac", label: "AAC", category: "files",
    ext: ["aac", "m4a"],
    ct: ["audio/aac", "audio/mp4", "audio/m4a", "audio/x-aac"]
  },
  {
    id: "mp3", label: "MP3", category: "files",
    ext: ["mp3"],
    ct: ["audio/mpeg", "audio/mpeg3", "audio/mp3"]
  },
  {
    id: "ogg", label: "OGG", category: "files",
    ext: ["ogg", "ogv", "oga", "opus"],
    ct: ["video/ogg", "audio/ogg", "audio/opus", "application/ogg"]
  },
  {
    id: "webm", label: "WEBM", category: "files",
    ext: ["webm", "weba"],
    ct: ["video/webm", "audio/webm"]
  },
  {
    id: "flv", label: "FLV", category: "files",
    ext: ["flv"],
    ct: ["video/x-flv"]
  }
];

var MPV_FILTER_DEFAULTS = {
  showStreams: true,
  showFiles: true,
  showIframes: true,
  minSizeKb: 0,
  blacklist: [],
  maxItems: 300
};

/** True when the path ends in one of the given extensions. */
function mpvPathHasExt(pathname, exts) {
  if (!pathname) return false;
  const lower = pathname.toLowerCase();
  for (var i = 0; i < exts.length; i++) {
    var ext = exts[i];
    if (lower.indexOf("." + ext) === -1) continue;
    if (lower.slice(-ext.length - 1) === "." + ext) return true;
  }
  return false;
}

/** Content-Type header value with any ";charset=..." suffix removed. */
function mpvNormalizeContentType(value) {
  if (!value) return "";
  return String(value).split(";")[0].trim().toLowerCase();
}

/**
 * Resolves a response to a media type.
 * Content-Type wins because it survives extension-less and signed URLs.
 * Returns null when the response is not media.
 */
function mpvClassifyResponse(url, contentType) {
  var ct = mpvNormalizeContentType(contentType);

  for (var i = 0; i < MPV_MEDIA_TYPES.length; i++) {
    var t = MPV_MEDIA_TYPES[i];
    for (var j = 0; j < t.ct.length; j++) {
      if (t.ct[j] === ct) return { type: t, matchedBy: "content-type" };
    }
  }

  var pathname = "";
  try {
    pathname = new URL(url).pathname;
  } catch (e) {
    pathname = "";
  }

  for (var k = 0; k < MPV_MEDIA_TYPES.length; k++) {
    var t2 = MPV_MEDIA_TYPES[k];
    if (mpvPathHasExt(pathname, t2.ext)) return { type: t2, matchedBy: "extension" };
  }

  return null;
}

/**
 * Whether a captured item passes the user's filters.
 * blacklistedBy lets a match on the request referrer hide child requests.
 */
function mpvPassesFilter(item, filters) {
  if (!item) return false;
  if (filters.blacklist && filters.blacklist.length) {
    var haystacks = [item.url || "", item.referer || "", item.typeLabel || ""];
    for (var i = 0; i < filters.blacklist.length; i++) {
      var entry = String(filters.blacklist[i] || "").toLowerCase();
      if (!entry) continue;
      for (var j = 0; j < haystacks.length; j++) {
        if (haystacks[j].toLowerCase().indexOf(entry) !== -1) return false;
      }
    }
  }

  var category = item.category;
  if (category === "stream") return filters.showStreams !== false;
  if (category === "iframe") return filters.showIframes !== false;
  if (category === "files") {
    if (filters.showFiles === false) return false;
    // A size gate only makes sense for real files, not for manifests.
    var minKb = Number(filters.minSizeKb) || 0;
    if (minKb > 0 && item.sizeBytes && item.sizeBytes < minKb * 1024) return false;
    return true;
  }
  return false;
}
