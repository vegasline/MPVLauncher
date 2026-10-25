// Original implementation, MIT licensed.
// Tests for the clean-room compact peer list decoder (BEP 23), including the
// multi6 entry point that bittorrent-tracker's HTTP tracker requires.

import { test } from 'node:test'
import assert from 'node:assert/strict'
import { createRequire } from 'node:module'

const require = createRequire(import.meta.url)
const compact2string = require('../vendor/compact2string/index.js')

test('decodes a single IPv4 peer', () => {
  const buf = Buffer.from([1, 2, 3, 4, 0x1f, 0x90])
  assert.equal(compact2string(buf), '1.2.3.4:8080')
})

test('decodes multiple IPv4 peers', () => {
  const buf = Buffer.from([10, 0, 0, 1, 0, 80, 10, 0, 0, 2, 0x01, 0xbb])
  assert.deepEqual(compact2string.multi(buf), ['10.0.0.1:80', '10.0.0.2:443'])
})

test('decodes one IPv6 peer', () => {
  const buf = Buffer.alloc(18)
  buf[15] = 1
  buf.writeUInt16BE(6881, 16)
  assert.equal(compact2string(buf), '[0000:0000:0000:0000:0000:0000:0000:0001]:6881')
})

test('decodes multiple IPv6 peers', () => {
  const buf = Buffer.alloc(36)
  buf[15] = 1
  buf.writeUInt16BE(80, 16)
  buf[33] = 2
  buf.writeUInt16BE(443, 34)
  assert.deepEqual(compact2string.multi6(buf), [
    '[0000:0000:0000:0000:0000:0000:0000:0001]:80',
    '[0000:0000:0000:0000:0000:0000:0000:0002]:443'
  ])
})

test('rejects wrong lengths', () => {
  assert.throws(() => compact2string(Buffer.alloc(7)), /expected 6 or 18/)
  assert.throws(() => compact2string.multi(Buffer.alloc(7)), /multiple of 6/)
  assert.throws(() => compact2string.multi6(Buffer.alloc(19)), /multiple of 18/)
})

test('exposes isIP', () => {
  assert.equal(compact2string.isIP('127.0.0.1'), 4)
  assert.equal(compact2string.isIP('::1'), 6)
  assert.equal(compact2string.isIP('nope'), 0)
})
