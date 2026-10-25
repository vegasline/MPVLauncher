// Original implementation, MIT licensed.

import test from 'node:test'
import assert from 'node:assert/strict'
import { EventEmitter } from 'node:events'
import { applyUploadPolicy, normalizeUploadKiB, uploadPolicy, watchWire } from '../src/upload.js'

test('upload is off by default and unknown values never mean unlimited', () => {
  assert.equal(normalizeUploadKiB(undefined), 0)
  assert.equal(normalizeUploadKiB(null), 0)
  assert.equal(normalizeUploadKiB('abc'), 0)
  assert.equal(normalizeUploadKiB(-1), 0)
  assert.equal(normalizeUploadKiB(0), 0)
  assert.equal(normalizeUploadKiB(1), 1)
  assert.equal(normalizeUploadKiB(128), 128)
  assert.equal(normalizeUploadKiB(129), 128)
})

test('a zero limit chokes every wire', () => {
  assert.deepEqual(uploadPolicy(0), { limitKiB: 0, bytesPerSecond: 0, chokeAll: true })
  assert.deepEqual(uploadPolicy(32), { limitKiB: 32, bytesPerSecond: 32768, chokeAll: false })
})

test('the policy is pushed to the client in bytes per second', () => {
  const rates = []
  const client = { throttleUpload: rate => rates.push(rate) }
  applyUploadPolicy(client, 0)
  applyUploadPolicy(client, 64)
  // Zero is enforced by choking wires; sending rate 0 to the limiter would
  // stall the handshake and every peer would time out.
  assert.deepEqual(rates, [65536])
})

test('choked wires stay choked when a peer becomes interested', () => {
  let choked = 0
  const wire = new EventEmitter()
  wire.choke = () => { choked++ }
  watchWire(wire, true)
  assert.equal(choked, 1)
  wire.emit('interested')
  wire.emit('request')
  assert.equal(choked, 3)
})

test('wires are untouched when a positive limit is configured', () => {
  let choked = 0
  const wire = new EventEmitter()
  wire.choke = () => { choked++ }
  watchWire(wire, false)
  wire.emit('interested')
  assert.equal(choked, 0)
})
