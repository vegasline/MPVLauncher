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
 * The declared type with this id, or null when there is none.
 *
 * A miss returns null rather than a synthesised entry: an invented entry is a
 * second, quietly wrong answer to a question that already has one.
 */
function mpvTypeById(id) {
  if (!id) return null;
  var key = String(id).toLowerCase();
  for (var i = 0; i < MPV_MEDIA_TYPES.length; i++) {
    if (MPV_MEDIA_TYPES[i].id === key) return MPV_MEDIA_TYPES[i];
  }
  return null;
}

/**
 * Which of the three filter chips this type belongs to.
 *
 * The popup used to answer this from its own short list, which named four
 * stream types and nothing else - so mp4, webm, flv, ts, aac, mp3 and ogg all
 * came back "stream". Turning the Streams chip off therefore hid captured
 * video files along with the streams, which is the opposite of what the chip
 * says.
 *
 * An unrecognised id is a stream: showing a row the user can ignore is a
 * smaller failure than hiding one they wanted.
 */
function mpvTypeCategory(id) {
  var entry = mpvTypeById(id);
  if (!entry) return "stream";
  if (entry.category === "files" || entry.category === "stream" || entry.category === "iframe") {
    return entry.category;
  }
  return "stream";
}

/**
 * Everything the popup needs to draw a type badge: the class to use and the
 * text to show.
 *
 * The badge used to be written as `t("type_" + id) || item.typeLabel`, which
 * cannot work: the translation lookup returns the key itself when it misses,
 * and a key is a non-empty string, so || kept it and the badge read
 * "type_mp4" in every language. The comparison against the key is the whole
 * fix - a translation is used when one exists and not otherwise.
 *
 * Order matters: a real translation beats the label the capture layer carried,
 * which beats this file's own label, which beats the bare id. The format names
 * are acronyms and do not translate, so the vocabulary's label is usually the
 * right answer.
 */
function mpvTypeBadge(typeId, typeLabel, translate) {
  var id = (typeId ? String(typeId) : "media").toLowerCase();
  var entry = mpvTypeById(id);
  var text = "";

  if (typeof translate === "function") {
    var key = "type_" + id;
    var candidate = translate(key);
    // A lookup that echoes the key back is telling us it missed.
    if (candidate && candidate !== key) text = candidate;
  }

  if (!text) text = typeLabel || "";
  if (!text && entry) text = entry.label || "";
  if (!text) text = id.toUpperCase();

  return { id: id, className: "tag-" + id, text: text };
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
 *
 * The popup used to keep its own copy of this, and the two disagreed: an
 * unrecognised category was hidden here and shown there. The copy that
 * actually ran was the popup's, and the popup is something the test suite
 * cannot execute - so twenty-odd checks were pinning a function no user
 * reached, and the behaviour that shipped was the untested one.
 *
 * There is now one answer, asked from the module that owns the vocabulary.
 * The category comes from the type id when the item does not carry one, which
 * is what makes mp4 and webm behave as files rather than as streams.
 *
 * An unrecognised category is shown. Hiding a stream the user wanted to play
 * is a worse failure than showing a row they can ignore.
 */
function mpvPassesFilter(item, filters) {
  if (!item) return false;
  var f = filters || {};

  if (f.blacklist && f.blacklist.length) {
    var haystacks = [item.url || "", item.referer || "", item.typeLabel || ""];
    for (var i = 0; i < f.blacklist.length; i++) {
      var entry = String(f.blacklist[i] || "").toLowerCase();
      if (!entry) continue;
      for (var j = 0; j < haystacks.length; j++) {
        var h = String(haystacks[j] || "").toLowerCase();
        if (h && h.indexOf(entry) !== -1) return false;
      }
    }
  }

  var category = item.category || mpvTypeCategory(item.typeId || item.type);
  if (category === "stream") return f.showStreams !== false;
  if (category === "iframe") return f.showIframes !== false;
  if (category === "files") {
    if (f.showFiles === false) return false;
    // A size gate only makes sense for a real file, not for a manifest.
    var minKb = Number(f.minSizeKb) || 0;
    if (minKb > 0 && item.sizeBytes && item.sizeBytes < minKb * 1024) return false;
    return true;
  }
  return true;
}
