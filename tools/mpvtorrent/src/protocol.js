// Original implementation, MIT licensed.
//
// The launcher contract: line-delimited JSON on stdout (events) and stdin
// (commands). Every line is one complete JSON object; stdout carries nothing
// else, so a stray console.log from a dependency would corrupt the stream.
// The helper therefore never uses console.log.

export const CONTRACT_VERSION = 2
export const HELPER_VERSION = '1.0.4'

/** Writes newline-delimited JSON events to a stream (stdout by default). */
export function createEventWriter (stream = process.stdout) {
  let closed = false
  return {
    send (event) {
      if (closed || stream.destroyed || !stream.writable) return false
      try {
        stream.write(JSON.stringify(event) + '\n')
        return true
      } catch {
        return false
      }
    },
    close () {
      closed = true
      try { stream.end() } catch { /* already gone */ }
    }
  }
}

/**
 * Parses one stdin line into a command.
 *
 * Only two commands exist. Anything else is rejected with an error value
 * rather than ignored, because the launcher needs to know its command was
 * not understood - silently doing nothing looks like a hang.
 */
export function parseCommand (line) {
  const trimmed = String(line).trim()
  if (trimmed === '') return { ok: false, code: 'empty_command' }

  let value
  try { value = JSON.parse(trimmed) } catch { return { ok: false, code: 'invalid_json' } }
  if (value === null || typeof value !== 'object' || Array.isArray(value)) {
    return { ok: false, code: 'invalid_command' }
  }

  if (value.cmd === 'close') return { ok: true, cmd: 'close' }

  if (value.cmd === 'select') {
    if (!Number.isInteger(value.fileIndex) || value.fileIndex < 0) {
      return { ok: false, code: 'invalid_file_index' }
    }
    return { ok: true, cmd: 'select', fileIndex: value.fileIndex }
  }

  return { ok: false, code: 'unknown_command' }
}

export function versionEvent () {
  return { type: 'version', contract: CONTRACT_VERSION, version: HELPER_VERSION }
}

export function helloEvent () {
  return { type: 'hello', contract: CONTRACT_VERSION, version: HELPER_VERSION }
}

export function errorEvent (code, message, fatal = false) {
  return { type: 'error', code, message, fatal }
}
