// Original implementation, MIT licensed.

import test from 'node:test'
import assert from 'node:assert/strict'
import { isAllowedHost, isAuthorizedPath } from '../src/host.js'

test('loopback literals with our port are allowed', () => {
  assert.equal(isAllowedHost('127.0.0.1:8080', 8080), true)
  assert.equal(isAllowedHost('LOCALHOST:8080', 8080), true)
  assert.equal(isAllowedHost('[::1]:8080', 8080), true)
})

test('DNS names, other ports and missing ports are rejected', () => {
  assert.equal(isAllowedHost('evil.example:8080', 8080), false)
  assert.equal(isAllowedHost('127.0.0.1:8081', 8080), false)
  assert.equal(isAllowedHost('127.0.0.1', 8080), false)
  assert.equal(isAllowedHost('', 8080), false)
  assert.equal(isAllowedHost(undefined, 8080), false)
  assert.equal(isAllowedHost('127.0.0.1:8080', 0), false)
})

test('only the exact token path is authorised', () => {
  const token = 'a'.repeat(48)
  assert.equal(isAuthorizedPath(`/s/${token}`, token), true)
  assert.equal(isAuthorizedPath(`/s/${'b'.repeat(48)}`, token), false)
  assert.equal(isAuthorizedPath(`/s/${token}/extra`, token), false)
  assert.equal(isAuthorizedPath('/', token), false)
  assert.equal(isAuthorizedPath(`/s/${token}`, 'short'), false)
})
