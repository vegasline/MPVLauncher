// Original implementation, MIT licensed.
//
// Upload policy. This is deliberately not "uploadLimit: 0": in WebTorrent a
// throttle rate of 0 does not mean unlimited, it means the speed limiter never
// releases chunks - but peers can still keep a request queued forever. The
// policy below is enforced twice: throttleUpload(0) stops chunk flow, and every
// wire is choked so we never advertise interest in uploading. Seeding is not a
// feature of this program; when the session ends the process exits.

export const UPLOAD_MAX_KIB = 128

/** 0 means off. Invalid values fall back to off, never to unlimited. */
export function normalizeUploadKiB (value) {
  const n = Number(value)
  if (!Number.isFinite(n) || !Number.isInteger(n) || n <= 0) return 0
  return Math.min(n, UPLOAD_MAX_KIB)
}

/** @returns {{limitKiB:number, bytesPerSecond:number, chokeAll:boolean}} */
export function uploadPolicy (value) {
  const limitKiB = normalizeUploadKiB(value)
  return {
    limitKiB,
    // WebTorrent takes bytes/second; 0 is the "stop chunks" rate, not unlimited.
    bytesPerSecond: limitKiB * 1024,
    chokeAll: limitKiB === 0
  }
}

/** Applies the policy to a WebTorrent client. */
export function applyUploadPolicy (client, value) {
  const policy = uploadPolicy(value)
  // A throttle rate of 0 is not "off": the limiter holds every chunk, and the
  // socket pipeline it sits in also stalls the BitTorrent handshake, so peers
  // time out before they ever connect. With uploads off the only mechanism is
  // choking every wire; a positive limit is passed through as bytes/second.
  if (!policy.chokeAll && typeof client.throttleUpload === 'function') {
    client.throttleUpload(policy.bytesPerSecond)
  }
  return policy
}

/**
 * Wires are choked when uploads are off. Choking is re-applied whenever a peer
 * says it is interested, because an unchoke can race with our handler.
 */
export function watchWire (wire, chokeAll, log) {
  if (!chokeAll) return
  const choke = () => {
    try { wire.choke() } catch { /* wire already closed */ }
  }
  choke()
  wire.on('interested', () => {
    choke()
    log?.debug('choked interested peer', { peer: wire.peerId })
  })
  wire.on('request', choke)
}
