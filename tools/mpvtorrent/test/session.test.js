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
