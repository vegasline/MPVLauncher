// Original implementation, MIT licensed.

import test from 'node:test'
import assert from 'node:assert/strict'
import { Readable } from 'node:stream'
import { TorrentSession } from '../src/session.js'

function sessionWith (file) {
  const session = new TorrentSession({
    options: {},
    emit: () => {},
    log: { info: () => {}, warn: () => {}, error: () => {}, debug: () => {} },
    onClose: () => {}
  })
  session.currentFile = file
  return session
}

function fakeFile (length, ranges) {
  return {
    length,
    offset: 0,
    name: 'a.mkv',
    createReadStream: ({ start, end }) => {
      ranges.push([start, end])
      return Readable.from([Buffer.alloc(end - start + 1)])
    }
  }
}

test('a long range is read in bounded segments instead of one whole-file selection', async () => {
  const length = 40 * 1024 * 1024 + 123
  const ranges = []
  const session = sessionWith(fakeFile(length, ranges))

  const stream = session.openStream(0, length - 1)
  let total = 0
  for await (const chunk of stream) total += chunk.length

  assert.equal(total, length)
  assert.deepEqual(ranges, [
    [0, 16 * 1024 * 1024 - 1],
    [16 * 1024 * 1024, 32 * 1024 * 1024 - 1],
    [32 * 1024 * 1024, length - 1]
  ])
})

test('a short range stays a single read', async () => {
  const ranges = []
  const session = sessionWith(fakeFile(1024 * 1024, ranges))

  const stream = session.openStream(10, 1000)
  let total = 0
  for await (const chunk of stream) total += chunk.length

  assert.equal(total, 991)
  assert.deepEqual(ranges, [[10, 1000]])
})

test('a range past the end is clamped to the file', async () => {
  const ranges = []
  const session = sessionWith(fakeFile(4096, ranges))

  const stream = session.openStream(4000, 999999)
  let total = 0
  for await (const chunk of stream) total += chunk.length

  assert.equal(total, 96)
  assert.deepEqual(ranges, [[4000, 4095]])
})

test('the segment size can be overridden for tests and small files', async () => {
  const ranges = []
  const file = fakeFile(10, ranges)
  const session = new TorrentSession({
    options: { readSegmentBytes: 4 },
    emit: () => {},
    log: { info: () => {}, warn: () => {}, error: () => {}, debug: () => {} },
    onClose: () => {}
  })
  session.currentFile = file

  const stream = session.openStream(0, 9)
  for await (const chunk of stream) void chunk

  assert.deepEqual(ranges, [[0, 3], [4, 7], [8, 9]])
})
