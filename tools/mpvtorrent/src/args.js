// Original implementation, MIT licensed.
//
// Command-line parsing and validation. The launcher sanitizes its own settings
// before spawning, but the helper is a separate executable that anyone can run,
// so every value is validated again here. Invalid numbers are clamped or
// replaced with the documented default and reported as warnings; the launcher
// must never be able to ask for a 500 KiB/s upload limit or a 10 GiB window.

import { parseArgs as nodeParseArgs } from 'node:util'
import { isAbsolute, parse as parsePath } from 'node:path'
import { CONTRACT_VERSION } from './protocol.js'

export const DEFAULTS = Object.freeze({
  forwardWindowMiB: 32,
  uploadLimitKiB: 0,
  tailWaitSec: 0,
  maxConns: 45,
  maxHalfOpen: 8,
  readTimeoutMs: 60_000,
  maxFileCount: 2000,
  maxTorrentBytes: 10 * 1024 * 1024
})

export const LIMITS = Object.freeze({
  forwardWindowMinMiB: 4,
  forwardWindowMaxMiB: 128,
  uploadMaxKiB: 128,
  tailWaitMaxSec: 30,
  maxConnsMin: 5,
  maxConnsMax: 200,
  halfOpenMin: 1,
  halfOpenMax: 50,
  readTimeoutMinMs: 5000,
  readTimeoutMaxMs: 300_000
})

const OPTION_SPEC = {
  source: { type: 'string' },
  'download-dir': { type: 'string' },
  'cache-dir': { type: 'string' },
  'launcher-pid': { type: 'string' },
  'window-mib': { type: 'string' },
  'upload-limit-kib': { type: 'string' },
  'tail-wait-sec': { type: 'string' },
  'max-conns': { type: 'string' },
  'max-half-open': { type: 'string' },
  'tracker-file': { type: 'string' },
  'select-file': { type: 'string' },
  'read-timeout-ms': { type: 'string' },
  contract: { type: 'string' },
  verbose: { type: 'boolean', short: 'v' },
  'log-file': { type: 'string' },
  version: { type: 'boolean' },
  help: { type: 'boolean', short: 'h' }
}

export function usage () {
  return [
    'mpvtorrent - stream one torrent file over a local HTTP endpoint',
    '',
    'Usage:',
    '  mpvtorrent --source <magnet|url|file|infohash> --download-dir <dir>',
    '             --cache-dir <dir> --launcher-pid <pid> [options]',
    '',
    'Options:',
    '  --window-mib <n>         forward window, default 32, max 128',
    '  --upload-limit-kib <n>   upload limit, default 0 (off), max 128',
    '  --tail-wait-sec <n>      wait for the file tail before playback, default 0, max 30',
    '  --max-conns <n>          total connections, default 45',
    '  --max-half-open <n>      simultaneous connection attempts, default 8',
    '  --tracker-file <path>    extra tracker list, one URL per line',
    '  --select-file <index>    pre-select this file when metadata arrives',
    '  --read-timeout-ms <n>    stall timeout for an HTTP read, default 60000',
    '  --contract <n>           launcher contract version (currently 2)',
    '  --log-file <path>        append diagnostic lines to a file',
    '  --verbose                also write diagnostics to stderr',
    '  --version                print the version event and exit',
    '  -h, --help               print this text and exit'
  ].join('\n')
}

/**
 * Parses argv (without the leading node/exec path).
 *
 * Returns { ok: true, options } or { ok: false, code, message }. Warnings are
 * non-fatal adjustments such as clamping a too-large upload limit.
 */
export function parseArgs (argv) {
  let parsed
  try {
    parsed = nodeParseArgs({ args: argv, options: OPTION_SPEC, strict: true, allowPositionals: false })
  } catch (error) {
    return { ok: false, code: 'bad_arguments', message: error.message }
  }

  const v = parsed.values
  if (v.help) return { ok: true, help: true }
  if (v.version) return { ok: true, version: true }

  const warnings = []

  if (!nonEmpty(v.source)) return { ok: false, code: 'missing_source', message: '--source is required' }
  if (!nonEmpty(v['download-dir'])) return { ok: false, code: 'missing_download_dir', message: '--download-dir is required' }
  if (!nonEmpty(v['cache-dir'])) return { ok: false, code: 'missing_cache_dir', message: '--cache-dir is required' }

  const downloadDir = String(v['download-dir'])
  const cacheDir = String(v['cache-dir'])
  if (!usableDirectory(downloadDir)) {
    return { ok: false, code: 'bad_download_dir', message: '--download-dir must be an absolute path below a drive or share root' }
  }
  if (!usableDirectory(cacheDir)) {
    return { ok: false, code: 'bad_cache_dir', message: '--cache-dir must be an absolute path below a drive or share root' }
  }

  const launcherPid = positiveInt(v['launcher-pid'])
  if (launcherPid === null) {
    return { ok: false, code: 'bad_launcher_pid', message: '--launcher-pid must be a positive integer' }
  }

  const contract = v.contract === undefined ? CONTRACT_VERSION : positiveInt(v.contract)
  if (contract === null) return { ok: false, code: 'bad_contract', message: '--contract must be a positive integer' }

  const options = {
    source: String(v.source).trim(),
    downloadDir,
    cacheDir,
    launcherPid,
    contract,
    forwardWindowMiB: clampInt(v['window-mib'], DEFAULTS.forwardWindowMiB,
      LIMITS.forwardWindowMinMiB, LIMITS.forwardWindowMaxMiB, 'window-mib', warnings),
    uploadLimitKiB: clampInt(v['upload-limit-kib'], DEFAULTS.uploadLimitKiB,
      0, LIMITS.uploadMaxKiB, 'upload-limit-kib', warnings),
    tailWaitSec: clampInt(v['tail-wait-sec'], DEFAULTS.tailWaitSec,
      0, LIMITS.tailWaitMaxSec, 'tail-wait-sec', warnings),
    maxConns: clampInt(v['max-conns'], DEFAULTS.maxConns,
      LIMITS.maxConnsMin, LIMITS.maxConnsMax, 'max-conns', warnings),
    maxHalfOpen: clampInt(v['max-half-open'], DEFAULTS.maxHalfOpen,
      LIMITS.halfOpenMin, LIMITS.halfOpenMax, 'max-half-open', warnings),
    readTimeoutMs: clampInt(v['read-timeout-ms'], DEFAULTS.readTimeoutMs,
      LIMITS.readTimeoutMinMs, LIMITS.readTimeoutMaxMs, 'read-timeout-ms', warnings),
    trackerFile: nonEmpty(v['tracker-file']) ? String(v['tracker-file']) : '',
    selectFile: optionalIndex(v['select-file'], 'select-file', warnings),
    verbose: Boolean(v.verbose),
    logFile: nonEmpty(v['log-file']) ? String(v['log-file']) : ''
  }

  // A launcher asking for more half-open attempts than total connections is
  // contradictory; the smaller number wins.
  if (options.maxHalfOpen > options.maxConns) {
    warnings.push(`max-half-open ${options.maxHalfOpen} exceeds max-conns ${options.maxConns}; using ${options.maxConns}`)
    options.maxHalfOpen = options.maxConns
  }

  return { ok: true, options, warnings }
}

function nonEmpty (value) {
  return typeof value === 'string' && value.trim() !== ''
}

/**
 * An absolute path that is not a filesystem root. The helper deletes the
 * download folder when it exits, so "C:\" or "\\server\share" must never be
 * accepted, whatever the launcher says.
 */
export function usableDirectory (value) {
  if (typeof value !== 'string' || !isAbsolute(value)) return false
  const parsed = parsePath(value)
  const root = parsed.root.replace(/[\\/]+$/, '')
  const rest = value.slice(parsed.root.length).replace(/[\\/]+/g, '')
  return rest.length > 0 && root.length > 0
}

/** Integer >= 1, or null. */
function positiveInt (value) {
  if (value === undefined) return null
  const n = Number(value)
  if (!Number.isInteger(n) || n < 1) return null
  return n
}

/** Optional non-negative integer; invalid values fall back to "not given". */
export function optionalIndex (value, name, warnings = []) {
  if (value === undefined || value === null || value === '') return null
  const n = Number(value)
  if (!Number.isInteger(n) || n < 0) {
    warnings.push(`${name} ${JSON.stringify(value)} is not a non-negative integer; ignoring it`)
    return null
  }
  return n
}

/**
 * Clamps an integer option. A missing or non-numeric value becomes the
 * documented default; out-of-range values become the nearest bound. The
 * clamp is reported so a launcher bug is visible in the log.
 */
export function clampInt (value, def, min, max, name, warnings = []) {
  if (value === undefined || value === null || value === '') return def
  const n = Number(value)
  if (!Number.isFinite(n) || !Number.isInteger(n)) {
    warnings.push(`${name} ${JSON.stringify(value)} is not an integer; using default ${def}`)
    return def
  }
  if (n < min) {
    warnings.push(`${name} ${n} is below ${min}; using ${min}`)
    return min
  }
  if (n > max) {
    warnings.push(`${name} ${n} is above ${max}; using ${max}`)
    return max
  }
  return n
}
