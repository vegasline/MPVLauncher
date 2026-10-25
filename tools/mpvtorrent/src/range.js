// Original implementation, MIT licensed.
//
// HTTP Range header parsing. Only a single byte range is supported: mpv asks
// for one range at a time, and multipart/byteranges replies are a common
// source of off-by-one bugs. A syntactically valid but out-of-bounds range is
// reported separately from a malformed one so the server can answer 416
// instead of 400.

/** @returns {{type:'full'}|{type:'range',start:number,end:number,length:number}|{type:'unsatisfiable'}|{type:'invalid',reason:string}} */
export function parseRange (header, size) {
  const total = Number.isSafeInteger(size) && size >= 0 ? size : 0
  if (header === undefined || header === null || String(header).trim() === '') return { type: 'full' }

  const text = String(header).trim()
  if (!/^bytes=/i.test(text)) return { type: 'invalid', reason: 'unsupported_unit' }

  const spec = text.slice(6).trim()
  if (spec.includes(',')) return { type: 'invalid', reason: 'multiple_ranges' }
  const match = /^(\d*)-(\d*)$/.exec(spec)
  if (!match) return { type: 'invalid', reason: 'malformed' }
  const [, rawStart, rawEnd] = match
  if (rawStart === '' && rawEnd === '') return { type: 'invalid', reason: 'malformed' }
  if (total === 0) return { type: 'unsatisfiable' }

  if (rawStart === '') {
    // Suffix range: the last N bytes.
    const suffix = toSafeInt(rawEnd)
    if (suffix === null || suffix <= 0) return { type: 'unsatisfiable' }
    const start = Math.max(0, total - suffix)
    return { type: 'range', start, end: total - 1, length: total - start }
  }

  const start = toSafeInt(rawStart)
  if (start === null) return { type: 'invalid', reason: 'malformed' }
  if (start >= total) return { type: 'unsatisfiable' }

  const end = rawEnd === '' ? total - 1 : toSafeInt(rawEnd)
  if (end === null) return { type: 'invalid', reason: 'malformed' }
  if (end < start) return { type: 'unsatisfiable' }

  const clampedEnd = Math.min(end, total - 1)
  return { type: 'range', start, end: clampedEnd, length: clampedEnd - start + 1 }
}

function toSafeInt (value) {
  if (!/^\d+$/.test(value)) return null
  const n = Number(value)
  return Number.isSafeInteger(n) ? n : null
}

/** Content-Range value for a 206 response. */
export function contentRange (start, end, size) {
  return `bytes ${start}-${end}/${size}`
}
