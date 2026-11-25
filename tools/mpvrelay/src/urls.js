/**
 * URL helpers for the relay.
 *
 * The relay never fetches anything it was not told about: a remote URL is only
 * usable once some playlist in the chain named it, and private or loopback
 * targets are refused outright so a hostile playlist cannot turn the local
 * server into a proxy for the machine behind it.
 */

const PRIVATE_V4 =
  /^(0\.|10\.|127\.|169\.254\.|192\.168\.|172\.(1[6-9]|2\d|3[01])\.)/;

/** Encode/decode a remote URL for a relay path without padding problems. */
export function encodeRemote(url) {
  return Buffer.from(url, "utf8").toString("base64url");
}

export function decodeRemote(value) {
  return Buffer.from(value, "base64url").toString("utf8");
}

/** True for http(s) URLs only - the same boundary mpv itself gets. */
export function isHttpUrl(value) {
  try {
    const url = new URL(value);
    return url.protocol === "http:" || url.protocol === "https:";
  } catch {
    return false;
  }
}

/**
 * Loopback and RFC1918 literals are refused. Hostnames are not resolved: a
 * playlist may legitimately point at a host that resolves privately, and a DNS
 * lookup here would leak the request before the allowlist check.
 */
export function isPrivateHost(hostname) {
  let host = String(hostname || "").toLowerCase();
  if (!host) return true;
  // Callers pass either a hostname (the relay server) or a whole URL (routing),
  // so a scheme is unwrapped rather than treated as an IPv6 colon.
  if (host.includes("://")) {
    try {
      host = new URL(host).hostname.toLowerCase();
    } catch {
      return true;
    }
  }
  if (!host) return true;
  if (host === "localhost" || host.endsWith(".localhost")) return true;
  if (host.startsWith("[") || host.includes(":")) return true; // IPv6 literals
  if (/^\d+\.\d+\.\d+\.\d+$/.test(host)) {
    return PRIVATE_V4.test(host) || host === "0.0.0.0";
  }
  return false;
}

/** strip query and fragment, for routing decisions only */
export function bareUrl(value) {
  try {
    const url = new URL(value);
    return url.origin + url.pathname;
  } catch {
    return String(value || "").split(/[?#]/)[0];
  }
}

const MEDIA_EXTENSIONS = [
  ".m3u8",
  ".m3u",
  ".mpd",
  ".mp4",
  ".m4v",
  ".mkv",
  ".webm",
  ".mov",
  ".avi",
  ".ts",
  ".m2ts",
  ".mp3",
  ".aac",
  ".flac",
  ".ogg",
  ".opus",
  ".wav",
];

/** True when the path itself names a media file (routing rule 2). */
export function hasMediaExtension(value) {
  const path = bareUrl(value).toLowerCase();
  return MEDIA_EXTENSIONS.some((ext) => path.endsWith(ext));
}

export function sameOrigin(a, b) {
  try {
    return new URL(a).origin === new URL(b).origin;
  } catch {
    return false;
  }
}
