/**
 * Playlist rewriting.
 *
 * mpv only ever sees the relay's own origin, so every URI a playlist names has
 * to be turned into a relay URI. Two shapes exist in practice:
 *
 *   #EXTM3U
 *   #EXT-X-KEY:METHOD=AES-128,URI="https://keys.example/k"   <-- URI attribute
 *   https://cdn.example/seg1.ts                              <-- bare line
 *
 * Relative URIs are resolved against the playlist's own remote URL and then
 * relayed as well: leaving them alone would make mpv request the *relay* with
 * the site's path (e.g. /w/<token>/v0), which the relay could only serve by
 * guessing which remote base that path belonged to.
 */

const URI_ATTRIBUTE = /URI="([^"]*)"/g;

export function isPlaylist(text) {
  if (typeof text !== "string") return false;
  const head = text.slice(0, 64).replace(/^\uFEFF/, "").trimStart();
  if (head.startsWith("#EXTM3U")) return true;
  return text.includes("#EXT-X-STREAM-INF") || text.includes("#EXTINF:");
}

export function isMasterPlaylist(text) {
  return typeof text === "string" && text.includes("#EXT-X-STREAM-INF");
}

/**
 * @param {string} text        playlist body
 * @param {string} remoteUrl   where that body came from
 * @param {(url: string) => string} toRelay  maps a remote URL to a relay URL
 * @returns {{ text: string, uris: string[] }} rewritten body plus every URI it names
 */
export function rewritePlaylist(text, remoteUrl, toRelay) {
  const uris = [];
  const base = remoteUrl;
  const map = (raw) => {
    const value = raw.trim();
    if (!value) return raw;
    let absolute;
    try {
      absolute = new URL(value, base).toString();
    } catch {
      return raw;
    }
    if (!/^https?:\/\//i.test(absolute)) return raw;
    uris.push(absolute);
    return toRelay(absolute);
  };

  const lines = String(text).replace(/\r\n/g, "\n").split("\n");
  const out = lines.map((line) => {
    if (line.startsWith("#")) {
      if (line.includes('URI="')) {
        return line.replace(URI_ATTRIBUTE, (_m, uri) => `URI="${map(uri)}"`);
      }
      return line;
    }
    if (!line.trim()) return line;
    return map(line);
  });

  return { text: out.join("\n"), uris };
}
