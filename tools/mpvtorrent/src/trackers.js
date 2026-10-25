// Original implementation, MIT licensed.
//
// Extra tracker list. The launcher owns the file and lets the user edit it;
// the helper only validates what it finds. Only schemes that a TCP/UDP-only
// build can actually announce to are accepted, and a hard cap keeps a broken
// file from opening thousands of sockets.

export const MAX_TRACKERS = 50
const ALLOWED = /^(udp|https?):\/\/[^\s]+$/i

/** @returns {string[]} unique, validated tracker URLs */
export function parseTrackerList (text, limit = MAX_TRACKERS) {
  if (typeof text !== 'string') return []
  const out = []
  for (const rawLine of text.split(/\r?\n/)) {
    const line = rawLine.trim()
    if (line === '' || line.startsWith('#')) continue
    if (!ALLOWED.test(line)) continue
    if (line.length > 2048) continue
    if (!out.includes(line)) out.push(line)
    if (out.length >= limit) break
  }
  return out
}

export function trackerListFor (path, readFileSync) {
  try {
    return parseTrackerList(readFileSync(path, 'utf8'))
  } catch {
    return []
  }
}
