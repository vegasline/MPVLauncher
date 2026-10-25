// Original implementation, MIT licensed.

import test from 'node:test'
import assert from 'node:assert/strict'
import { contentRange, parseRange } from '../src/range.js'

test('missing header means the whole file', () => {
  assert.deepEqual(parseRange(undefined, 100), { type: 'full' })
  assert.deepEqual(parseRange('  ', 100), { type: 'full' })
})

test('plain ranges are inclusive', () => {
  assert.deepEqual(parseRange('bytes=0-99', 100), { type: 'range', start: 0, end: 99, length: 100 })
  assert.deepEqual(parseRange('bytes=10-20', 100), { type: 'range', start: 10, end: 20, length: 11 })
  assert.equal(contentRange(10, 20, 100), 'bytes 10-20/100')
})

test('open-ended and suffix ranges', () => {
  assert.deepEqual(parseRange('bytes=90-', 100), { type: 'range', start: 90, end: 99, length: 10 })
  assert.deepEqual(parseRange('bytes=-10', 100), { type: 'range', start: 90, end: 99, length: 10 })
  assert.deepEqual(parseRange('bytes=-1000', 100), { type: 'range', start: 0, end: 99, length: 100 })
})

test('an end past the file is clamped', () => {
  assert.deepEqual(parseRange('bytes=90-1000', 100), { type: 'range', start: 90, end: 99, length: 10 })
})

test('out-of-bounds ranges are unsatisfiable, not malformed', () => {
  assert.deepEqual(parseRange('bytes=100-200', 100), { type: 'unsatisfiable' })
  assert.deepEqual(parseRange('bytes=20-10', 100), { type: 'unsatisfiable' })
  assert.deepEqual(parseRange('bytes=-0', 100), { type: 'unsatisfiable' })
})

test('malformed and multi-range headers are invalid', () => {
  assert.equal(parseRange('items=0-1', 100).type, 'invalid')
  assert.equal(parseRange('bytes=abc', 100).type, 'invalid')
  assert.equal(parseRange('bytes=-', 100).type, 'invalid')
  assert.equal(parseRange('bytes=0-1,4-5', 100).type, 'invalid')
  assert.equal(parseRange('bytes=0', 100).type, 'invalid')
})

test('any range of an empty file is unsatisfiable', () => {
  assert.equal(parseRange('bytes=0-0', 0).type, 'unsatisfiable')
  assert.deepEqual(parseRange('bytes=0-0', -1), { type: 'unsatisfiable' })
})

test('numbers too large for safe arithmetic are rejected', () => {
  assert.equal(parseRange('bytes=999999999999999999999-', 100).type, 'invalid')
})
