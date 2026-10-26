// Original implementation, MIT licensed.

import test from 'node:test'
import assert from 'node:assert/strict'
import { TorrentSession } from '../src/session.js'

function bareSession () {
  return new TorrentSession({
    options: {},
    emit: () => {},
    log: { info: () => {}, warn: () => {}, error: () => {}, debug: () => {} },
    onClose: () => {}
  })
}

test('a file chosen from the picker goes through the initial preload path', () => {
  const session = bareSession()
  session.metaHandled = true
  let seen = null
  session.beginFile = (index, options) => {
    seen = { index, options }
    return Promise.resolve()
  }

  session.selectFile(3)

  assert.deepEqual(seen, { index: 3, options: { initial: true } })
})

test('a selection before metadata arrives is a non-fatal error, not a preload', () => {
  const session = bareSession()
  const events = []
  session.emit = event => events.push(event)
  let called = false
  session.beginFile = () => { called = true; return Promise.resolve() }

  session.selectFile(0)

  assert.equal(called, false)
  assert.equal(events.length, 1)
  assert.equal(events[0].code, 'not_ready')
  assert.equal(events[0].fatal, false)
})

function readyFixture (tailWaitSec = 0) {
  const session = bareSession()
  session.options = { tailWaitSec }
  session.calls = []
  session.torrent = {
    pieceLength: 8 * 1024 * 1024,
    pieces: new Array(100).fill(null),
    select: (start, end, priority) => session.calls.push({ start, end, priority }),
    deselect: () => {},
    critical: () => {}
  }
  session.currentFile = { offset: 0, length: 800 * 1024 * 1024, path: 'a.mkv', name: 'a.mkv' }
  session.server = { url: 'http://127.0.0.1:1/x' }
  session.infoHash = 'f'.repeat(40)
  session.fileIndex = 0
  session.preload = {
    head: { startPiece: 0, endPiece: 1 },
    tail: { startPiece: 98, endPiece: 99 }
  }
  session.preloadStartedAt = Date.now()
  session.applyWindow = () => {}
  return session
}

test('a seek moves the window before the response can block', () => {
  const session = readyFixture()
  session.ready = true
  session.position = 0
  session.window = { startPiece: 0, endPiece: 4, urgentEndPiece: 1 }
  session.applied = []
  session.applyWindow = (plan, opts) => session.applied.push({ plan, opts })

  session.noteSeek(400 * 1024 * 1024)

  assert.equal(session.position, 400 * 1024 * 1024)
  assert.equal(session.applied.length, 1)
  assert.equal(session.applied[0].opts.force, true)
  // 400 MiB into the file is piece 50; the window opens 6 MiB behind it.
  assert.equal(session.applied[0].plan.startPiece, 49)
})

test('a normal forward step is not treated as a seek', () => {
  const session = readyFixture()
  session.ready = true
  session.position = 10 * 1024 * 1024
  session.applied = []
  session.applyWindow = (plan, opts) => session.applied.push({ plan, opts })

  session.noteSeek(11 * 1024 * 1024)

  assert.equal(session.applied.length, 0)
  assert.equal(session.position, 10 * 1024 * 1024)
})

test('a seek before playback starts does not move anything', () => {
  const session = readyFixture()
  session.applied = []
  session.applyWindow = (plan, opts) => session.applied.push({ plan, opts })

  session.noteSeek(400 * 1024 * 1024)

  assert.equal(session.applied.length, 0)
})

test('the tail is fetched only once playback is ready', () => {
  const session = readyFixture()
  const tail = session.preload.tail

  session.becomeReady(tail, false)

  assert.deepEqual(session.calls, [{ start: 98, end: 99, priority: 1 }])
  assert.deepEqual(session.tailPending, { start: 98, end: 99 })
})

test('an optional tail wait starts fetching the tail while the head is done', () => {
  const session = readyFixture(10)
  session.later = () => null

  session.armTailWait(5000)

  assert.deepEqual(session.calls, [{ start: 98, end: 99, priority: 1 }])
})
