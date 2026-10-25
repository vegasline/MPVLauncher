// Original implementation, MIT licensed.

import test from 'node:test'
import assert from 'node:assert/strict'
import { join } from 'node:path'
import { DEFAULTS, parseArgs } from '../src/args.js'

const HASH = 'a'.repeat(40)

function base (...extra) {
  return [
    '--source', `magnet:?xt=urn:btih:${HASH}`,
    '--download-dir', join(process.cwd(), 'tmp', 'download'),
    '--cache-dir', join(process.cwd(), 'tmp', 'cache'),
    '--launcher-pid', '4242',
    ...extra
  ]
}

test('defaults are the documented ones', () => {
  const result = parseArgs(base())
  assert.equal(result.ok, true)
  assert.equal(result.options.forwardWindowMiB, 32)
  assert.equal(result.options.uploadLimitKiB, 0)
  assert.equal(result.options.maxConns, 45)
  assert.equal(result.options.maxHalfOpen, 8)
  assert.equal(result.options.contract, 2)
  assert.equal(result.warnings.length, 0)
})

test('upload limit clamps to 0..128', () => {
  assert.equal(parseArgs(base('--upload-limit-kib', '128')).options.uploadLimitKiB, 128)
  assert.equal(parseArgs(base('--upload-limit-kib', '999')).options.uploadLimitKiB, 128)
  assert.equal(parseArgs(base('--upload-limit-kib=-5')).options.uploadLimitKiB, 0)
  assert.equal(parseArgs(base('--upload-limit-kib', 'abc')).options.uploadLimitKiB, 0)
  assert.equal(parseArgs(base('--upload-limit-kib', '12.5')).options.uploadLimitKiB, 0)
})

test('tail wait defaults to zero and clamps to 0..30 s', () => {
  assert.equal(parseArgs(base()).options.tailWaitSec, 0)
  assert.equal(parseArgs(base('--tail-wait-sec', '10')).options.tailWaitSec, 10)
  assert.equal(parseArgs(base('--tail-wait-sec', '30')).options.tailWaitSec, 30)
  assert.equal(parseArgs(base('--tail-wait-sec', '99')).options.tailWaitSec, 30)
  assert.equal(parseArgs(base('--tail-wait-sec=-5')).options.tailWaitSec, 0)
  assert.equal(parseArgs(base('--tail-wait-sec', 'abc')).options.tailWaitSec, 0)
})

test('window clamps to 4..128 MiB', () => {
  assert.equal(parseArgs(base('--window-mib', '8')).options.forwardWindowMiB, 8)
  assert.equal(parseArgs(base('--window-mib', '1')).options.forwardWindowMiB, 4)
  assert.equal(parseArgs(base('--window-mib', '4096')).options.forwardWindowMiB, 128)
  assert.equal(parseArgs(base('--window-mib', 'nope')).options.forwardWindowMiB, DEFAULTS.forwardWindowMiB)
})

test('half-open is never larger than total connections', () => {
  const result = parseArgs(base('--max-conns', '10', '--max-half-open', '40'))
  assert.equal(result.options.maxConns, 10)
  assert.equal(result.options.maxHalfOpen, 10)
  assert.ok(result.warnings.some(w => w.includes('max-half-open')))
})

test('connection bounds are clamped', () => {
  assert.equal(parseArgs(base('--max-conns', '1')).options.maxConns, 5)
  assert.equal(parseArgs(base('--max-conns', '9999')).options.maxConns, 200)
  assert.equal(parseArgs(base('--max-half-open', '0')).options.maxHalfOpen, 1)
})

test('missing required values are rejected', () => {
  assert.equal(parseArgs([]).code, 'missing_source')
  assert.equal(parseArgs(['--source', 'magnet:?xt=urn:btih:' + HASH]).code, 'missing_download_dir')
  const noPid = base().filter(arg => arg !== '--launcher-pid' && arg !== '4242')
  assert.equal(parseArgs(noPid).code, 'bad_launcher_pid')
})

test('root directories are rejected because they are deleted on exit', () => {
  const result = parseArgs([
    '--source', 'x',
    '--download-dir', process.platform === 'win32' ? 'C:\\' : '/',
    '--cache-dir', join(process.cwd(), 'tmp', 'cache'),
    '--launcher-pid', '1'
  ])
  assert.equal(result.ok, false)
  assert.equal(result.code, 'bad_download_dir')
})

test('relative directories are rejected', () => {
  const result = parseArgs([
    '--source', 'x', '--download-dir', 'relative', '--cache-dir', 'cache', '--launcher-pid', '1'
  ])
  assert.equal(result.code, 'bad_download_dir')
})

test('unknown options and positionals are rejected', () => {
  assert.equal(parseArgs(['--bogus']).code, 'bad_arguments')
  assert.equal(parseArgs([...base(), 'stray']).code, 'bad_arguments')
})

test('select-file accepts non-negative integers only', () => {
  assert.equal(parseArgs(base('--select-file', '3')).options.selectFile, 3)
  const bad = parseArgs(base('--select-file=-1'))
  assert.equal(bad.options.selectFile, null)
  assert.ok(bad.warnings.length > 0)
})

test('version and help short-circuit without requiring arguments', () => {
  assert.deepEqual(parseArgs(['--version']), { ok: true, version: true })
  assert.deepEqual(parseArgs(['--help']), { ok: true, help: true })
})
