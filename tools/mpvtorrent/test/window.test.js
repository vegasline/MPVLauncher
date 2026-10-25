// Original implementation, MIT licensed.

import test from 'node:test'
import assert from 'node:assert/strict'
import {
  MIB,
  mergeRanges,
  planInitialPieces,
  planWindow,
  rangeIsComplete,
  shouldStartPlayback,
  subtractRanges
} from '../src/window.js'

const PIECE = 1024 * 1024 // 1 MiB
const FILE = 512 * MIB

function plan (overrides = {}) {
  return planWindow({
    position: 0,
    fileOffset: 0,
    fileLength: FILE,
    pieceLength: PIECE,
    pieceCount: 1024,
    ...overrides
  })
}

test('a 32 MiB window starts six MiB behind the read position and ends 32 MiB ahead', () => {
  const result = plan({ position: 10 * MIB })
  assert.equal(result.startPiece, 4)
  assert.equal(result.endPiece, 41)
  assert.equal(result.forwardBytes, 38 * MIB)
})

test('the window is at least four pieces even when the setting is smaller', () => {
  const result = planWindow({
    position: 0, fileOffset: 0, fileLength: FILE, pieceLength: 1 * MIB, pieceCount: 512, forwardMiB: 1
  })
  assert.equal(result.forwardBytes, 4 * MIB)
  assert.equal(result.endPiece, 3)
})

test('the window is never larger than 128 MiB', () => {
  const result = planWindow({
    position: 0, fileOffset: 0, fileLength: 4 * 1024 * MIB, pieceLength: 40 * MIB,
    pieceCount: 100, forwardMiB: 128
  })
  assert.ok(result.forwardBytes <= 128 * MIB)
  assert.ok(result.endPiece - result.startPiece < 4)
})

test('large pieces push the window to four pieces, but not past 128 MiB', () => {
  const result = planWindow({
    position: 0, fileOffset: 0, fileLength: 4096 * MIB, pieceLength: 16 * MIB,
    pieceCount: 256, forwardMiB: 32
  })
  assert.equal(result.forwardBytes, 64 * MIB)
  assert.equal(result.endPiece, 3)
})

test('the backward buffer is small and clipped at the start of the file', () => {
  const mid = plan({ position: 10 * MIB })
  assert.equal(mid.startByte, 4 * MIB) // 6 MiB back
  assert.equal(mid.startPiece, 4)
  const atStart = plan({ position: 2 * MIB })
  assert.equal(atStart.startByte, 0)
  assert.equal(atStart.startPiece, 0)
})

test('the window stops at the end of the file', () => {
  const result = plan({ position: FILE - MIB, fileLength: FILE, pieceCount: 512 })
  assert.equal(result.endPiece, 511)
  assert.equal(result.endByte, FILE)
  assert.equal(result.atEnd, true)
})

test('file offset is included so multi-file torrents align correctly', () => {
  const offset = 100 * MIB + 512 * 1024
  const result = plan({ position: 0, fileOffset: offset, fileLength: 50 * MIB })
  assert.equal(result.startPiece, 100) // piece containing the file start
  assert.equal(result.startByte, offset)
  assert.equal(result.endPiece, 132)
})

test('the urgent tier is the first 8 MiB or two pieces', () => {
  const result = plan({ position: 4 * MIB })
  assert.equal(result.urgentEndPiece, 11) // head 4 + 8 MiB -> piece 11
  const bigPieces = planWindow({
    position: 0, fileOffset: 0, fileLength: FILE, pieceLength: 5 * MIB, pieceCount: 200, urgentMiB: 1
  })
  assert.equal(bigPieces.urgentEndPiece, 1) // two pieces minimum
})

test('pieces larger than 8 MiB are flagged', () => {
  assert.equal(plan().hugePiece, false)
  const big = planWindow({
    position: 0, fileOffset: 0, fileLength: FILE, pieceLength: 9 * MIB, pieceCount: 100
  })
  assert.equal(big.hugePiece, true)
})

test('a zero-length file and a one-byte file do not produce negative ranges', () => {
  const empty = planWindow({ position: 0, fileOffset: 0, fileLength: 0, pieceLength: PIECE, pieceCount: 1 })
  assert.equal(empty.startPiece, 0)
  assert.equal(empty.endPiece, 0)
  const one = planWindow({ position: 0, fileOffset: 0, fileLength: 1, pieceLength: PIECE, pieceCount: 1 })
  assert.equal(one.endPiece, 0)
})

test('initial plan preloads the first and last 4 MiB', () => {
  const result = planInitialPieces({
    fileOffset: 0, fileLength: 512 * MIB, pieceLength: PIECE, pieceCount: 512
  })
  assert.deepEqual(result.head, { startPiece: 0, endPiece: 3 })
  assert.deepEqual(result.tail, { startPiece: 508, endPiece: 511 })
  assert.equal(result.coversWholeFile, false)
})

test('small files preload in one pass', () => {
  const result = planInitialPieces({
    fileOffset: 0, fileLength: 5 * MIB, pieceLength: PIECE, pieceCount: 5
  })
  assert.equal(result.coversWholeFile, true)
})

test('bitfield completeness is inclusive', () => {
  const bitfield = { get: i => i >= 2 && i <= 4 }
  assert.equal(rangeIsComplete(bitfield, 2, 4), true)
  assert.equal(rangeIsComplete(bitfield, 2, 5), false)
  assert.equal(rangeIsComplete(null, 0, 0), false)
})

test('range merge and subtraction', () => {
  assert.deepEqual(mergeRanges([
    { startPiece: 5, endPiece: 7 },
    { startPiece: 1, endPiece: 2 },
    { startPiece: 3, endPiece: 4 }
  ]), [{ startPiece: 1, endPiece: 7 }])

  assert.deepEqual(subtractRanges(
    [{ startPiece: 0, endPiece: 9 }],
    [{ startPiece: 3, endPiece: 4 }]
  ), [{ startPiece: 0, endPiece: 2 }, { startPiece: 5, endPiece: 9 }])
})

test('playback starts as soon as the head is complete when no tail wait is configured', () => {
  assert.equal(shouldStartPlayback({
    headComplete: true, tailComplete: false, tailWaitMs: 0, waitedMs: 0
  }), true)
  assert.equal(shouldStartPlayback({
    headComplete: true, tailComplete: false, tailWaitMs: 0, waitedMs: 120_000
  }), true)
})

test('playback never starts before the head is complete', () => {
  assert.equal(shouldStartPlayback({
    headComplete: false, tailComplete: true, tailWaitMs: 0, waitedMs: 0
  }), false)
  assert.equal(shouldStartPlayback({
    headComplete: false, tailComplete: false, tailWaitMs: 30_000, waitedMs: 60_000
  }), false)
})

test('playback starts immediately when head and tail are both complete', () => {
  assert.equal(shouldStartPlayback({
    headComplete: true, tailComplete: true, tailWaitMs: 30_000, waitedMs: 0
  }), true)
})

test('an optional tail wait delays playback only until it elapses', () => {
  assert.equal(shouldStartPlayback({
    headComplete: true, tailComplete: false, tailWaitMs: 10_000, waitedMs: 9_999
  }), false)
  assert.equal(shouldStartPlayback({
    headComplete: true, tailComplete: false, tailWaitMs: 10_000, waitedMs: 10_000
  }), true)
  assert.equal(shouldStartPlayback({
    headComplete: true, tailComplete: false, tailWaitMs: 10_000, waitedMs: 25_000
  }), true)
})

test('an invalid tail wait never blocks playback', () => {
  assert.equal(shouldStartPlayback({
    headComplete: true, tailComplete: false, tailWaitMs: Number.NaN, waitedMs: 5_000
  }), true)
  assert.equal(shouldStartPlayback({
    headComplete: true, tailComplete: false, tailWaitMs: -1, waitedMs: 5_000
  }), true)
})
