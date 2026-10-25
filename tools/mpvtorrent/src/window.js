// Original implementation, MIT licensed.
//
// Pure piece-window planning. No I/O, no WebTorrent objects: given a read
// position inside one file it returns the piece range that should be selected
// and which part of it is urgent. Keeping this pure is what makes the
// prioritisation testable without a swarm.

export const MIB = 1024 * 1024

export const WINDOW_DEFAULTS = Object.freeze({
  forwardMiB: 32,
  backMiB: 6,
  urgentMiB: 8,
  maxForwardMiB: 128,
  hugePieceBytes: 8 * MIB
})

/**
 * @param {object} input
 * @param {number} input.position        byte offset of the read head inside the file
 * @param {number} input.fileOffset      byte offset of the file inside the torrent
 * @param {number} input.fileLength      file length in bytes
 * @param {number} input.pieceLength     torrent piece length in bytes
 * @param {number} input.pieceCount      total pieces in the torrent
 * @param {number} [input.forwardMiB]    configured forward window
 * @param {number} [input.backMiB]       backward buffer
 * @param {number} [input.urgentMiB]     urgent part at the front of the window
 * @returns {{startPiece:number,endPiece:number,urgentEndPiece:number,
 *            startByte:number,endByte:number,forwardBytes:number,
 *            hugePiece:boolean,atEnd:boolean}}
 */
export function planWindow (input) {
  const pieceLength = Math.max(1, Math.floor(input.pieceLength))
  const pieceCount = Math.max(1, Math.floor(input.pieceCount))
  const fileOffset = Math.max(0, Math.floor(input.fileOffset))
  const fileLength = Math.max(0, Math.floor(input.fileLength))
  const position = clamp(Math.floor(input.position), 0, Math.max(0, fileLength - 1))

  const forwardMiB = input.forwardMiB ?? WINDOW_DEFAULTS.forwardMiB
  const backMiB = input.backMiB ?? WINDOW_DEFAULTS.backMiB
  const urgentMiB = input.urgentMiB ?? WINDOW_DEFAULTS.urgentMiB

  // The window is never smaller than four pieces: below that, a slow peer that
  // delivers one piece at a time stalls playback between requests.
  const requestedForward = Math.max(0, forwardMiB) * MIB
  const forwardBytes = Math.min(
    Math.max(requestedForward, 4 * pieceLength),
    WINDOW_DEFAULTS.maxForwardMiB * MIB
  )
  const backBytes = Math.min(Math.max(0, backMiB) * MIB, Math.max(0, position))

  const head = fileOffset + position
  const startByte = Math.max(fileOffset, head - backBytes)
  // endByte is exclusive, clamped to the file end so the tail of a large
  // torrent does not stay selected after the read head reached EOF.
  const endByte = Math.min(fileOffset + fileLength, head + forwardBytes)
  const urgentBytes = Math.max(0, urgentMiB) * MIB
  const urgentEnd = Math.min(endByte, head + Math.max(urgentBytes, 2 * pieceLength))

  const startPiece = clamp(Math.floor(startByte / pieceLength), 0, pieceCount - 1)
  const endPiece = clamp(Math.ceil(endByte / pieceLength) - 1, startPiece, pieceCount - 1)
  const urgentEndPiece = clamp(Math.ceil(urgentEnd / pieceLength) - 1, startPiece, endPiece)

  return {
    startPiece,
    endPiece,
    urgentEndPiece,
    startByte,
    endByte,
    forwardBytes: endByte - startByte,
    // A piece larger than 8 MiB makes the urgent tier coarse: one missing
    // piece can be the whole preload budget, so the caller logs this.
    hugePiece: pieceLength > WINDOW_DEFAULTS.hugePieceBytes,
    atEnd: endByte >= fileOffset + fileLength
  }
}

/** Piece range covering [offset, offset+length) clipped to one file. */
export function planInitialPieces ({ fileOffset, fileLength, pieceLength, pieceCount, headBytes = 4 * MIB, tailBytes = 4 * MIB }) {
  const startByte = Math.max(0, fileOffset)
  const endByte = Math.max(startByte, fileOffset + fileLength)
  const headEnd = Math.min(endByte, startByte + Math.max(headBytes, pieceLength))
  const tailStart = Math.max(headEnd, endByte - Math.max(tailBytes, pieceLength))

  const firstPiece = clamp(Math.floor(startByte / pieceLength), 0, pieceCount - 1)
  const headEndPiece = clamp(Math.ceil(headEnd / pieceLength) - 1, firstPiece, pieceCount - 1)
  const tailStartPiece = clamp(Math.floor(tailStart / pieceLength), firstPiece, pieceCount - 1)
  const lastPiece = clamp(Math.ceil(endByte / pieceLength) - 1, firstPiece, pieceCount - 1)

  return {
    head: { startPiece: firstPiece, endPiece: headEndPiece },
    tail: { startPiece: tailStartPiece, endPiece: lastPiece },
    coversWholeFile: tailStartPiece <= headEndPiece + 1
  }
}

/** True when every piece of [startPiece, endPiece] is verified locally. */
export function rangeIsComplete (bitfield, startPiece, endPiece) {
  if (!bitfield) return false
  for (let i = startPiece; i <= endPiece; i++) {
    if (!bitfield.get(i)) return false
  }
  return startPiece <= endPiece
}

/**
 * Playback gate for the initial preload.
 *
 * The beginning of the file is mandatory: without it the stream cannot start
 * at all. The tail is a seek convenience, and on a large or sparsely seeded
 * torrent it can take minutes while the first bytes arrived in seconds, so it
 * is only waited for when the launcher explicitly asks for a short delay.
 */
export function shouldStartPlayback ({ headComplete, tailComplete, tailWaitMs, waitedMs }) {
  if (!headComplete) return false
  if (tailComplete) return true
  if (!Number.isFinite(tailWaitMs) || tailWaitMs <= 0) return true
  return Number.isFinite(waitedMs) && waitedMs >= tailWaitMs
}

/** Merges adjacent/overlapping inclusive piece ranges. */
export function mergeRanges (ranges) {
  const sorted = ranges
    .filter(r => r && r.endPiece >= r.startPiece)
    .map(r => ({ startPiece: r.startPiece, endPiece: r.endPiece }))
    .sort((a, b) => a.startPiece - b.startPiece)
  const out = []
  for (const range of sorted) {
    const last = out[out.length - 1]
    if (last && range.startPiece <= last.endPiece + 1) last.endPiece = Math.max(last.endPiece, range.endPiece)
    else out.push({ ...range })
  }
  return out
}

/** Difference a - b, both lists of inclusive ranges. */
export function subtractRanges (a, b) {
  let current = mergeRanges(a)
  for (const cut of mergeRanges(b)) {
    const next = []
    for (const range of current) {
      if (cut.endPiece < range.startPiece || cut.startPiece > range.endPiece) {
        next.push(range)
        continue
      }
      if (cut.startPiece > range.startPiece) next.push({ startPiece: range.startPiece, endPiece: cut.startPiece - 1 })
      if (cut.endPiece < range.endPiece) next.push({ startPiece: cut.endPiece + 1, endPiece: range.endPiece })
    }
    current = next
  }
  return current
}

function clamp (value, min, max) {
  if (!Number.isFinite(value)) return min
  return Math.min(max, Math.max(min, value))
}
