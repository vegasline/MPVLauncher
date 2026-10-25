// Original implementation, MIT licensed.
//
// Minimal file/stderr logger. The log never contains a raw torrent source:
// a magnet can carry a private passkey and a URL can carry a token, so callers
// log the source *kind* and the infohash instead. Logging failure is never
// fatal - a read-only log path must not stop playback.

import { appendFileSync, mkdirSync } from 'node:fs'
import { dirname } from 'node:path'

export function createLogger ({ verbose = false, logFile = '' } = {}) {
  let fileOk = Boolean(logFile)

  if (fileOk) {
    try {
      mkdirSync(dirname(logFile), { recursive: true })
    } catch {
      fileOk = false
    }
  }

  const write = (level, message, detail) => {
    const line = `${new Date().toISOString()} ${level} ${message}${detail === undefined ? '' : ' ' + formatDetail(detail)}`
    if (fileOk) {
      try { appendFileSync(logFile, line + '\n') } catch { fileOk = false }
    }
    if (verbose || level === 'error') {
      try { process.stderr.write(line + '\n') } catch { /* stderr closed */ }
    }
  }

  return {
    debug: (message, detail) => { if (verbose) write('debug', message, detail) },
    info: (message, detail) => write('info', message, detail),
    warn: (message, detail) => write('warn', message, detail),
    error: (message, detail) => write('error', message, detail)
  }
}

function formatDetail (detail) {
  if (detail instanceof Error) return `${detail.name}: ${detail.message}`
  if (typeof detail === 'string') return detail
  try { return JSON.stringify(detail) } catch { return String(detail) }
}
