// Original implementation, MIT licensed.

import test from 'node:test'
import assert from 'node:assert/strict'
import { isProcessAlive } from '../src/process.js'

test('the current process is alive', () => {
  assert.equal(isProcessAlive(process.pid), true)
})

test('impossible and invalid PIDs are not alive', () => {
  assert.equal(isProcessAlive(0), false)
  assert.equal(isProcessAlive(-1), false)
  assert.equal(isProcessAlive(1.5), false)
  assert.equal(isProcessAlive(999999), false)
})
