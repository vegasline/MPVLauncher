// Original implementation, MIT licensed.
//
// Torrent paths are attacker-controlled: a hostile .torrent can contain
// "..\\..\\Startup\\evil.exe", an absolute path, a Windows device name or an
// NTFS alternate data stream. WebTorrent already sanitises some of this, but
// this module is the single place that decides what may be created on disk,
// and it is small enough to test exhaustively.

import { join, resolve, sep, isAbsolute } from 'node:path'

export class PathError extends Error {
  constructor (code, message) {
    super(message)
    this.name = 'PathError'
    this.code = code
  }
}

const RESERVED = /^(con|prn|aux|nul|com[1-9]|lpt[1-9])(\..*)?$/i
const ILLEGAL = /[<>:"|?*\u0000-\u001f]/
const MAX_SEGMENT = 255
const MAX_PATH = 900

/**
 * Converts a WebTorrent file path (string with "/" separators, or an array of
 * segments) into safe path segments.
 * @returns {string[]}
 */
export function safeSegments (torrentPath) {
  const raw = Array.isArray(torrentPath) ? torrentPath.join('/') : String(torrentPath ?? '')
  if (raw.trim() === '') throw new PathError('empty_path', 'torrent file has an empty path')
  if (raw.length > MAX_PATH) throw new PathError('path_too_long', 'torrent file path is too long')
  if (/^[/\\]/.test(raw) || /^[A-Za-z]:/.test(raw) || isAbsolute(raw) || raw.startsWith('\\\\')) {
    throw new PathError('absolute_path', 'torrent file path is absolute')
  }

  const parts = raw.replace(/\\/g, '/').split('/')
  const out = []
  for (const part of parts) {
    const segment = part.normalize('NFC')
    if (segment === '' || segment === '.' || segment === '..') {
      throw new PathError('traversal', 'torrent file path contains a traversal segment')
    }
    if (segment.length > MAX_SEGMENT) throw new PathError('path_too_long', 'torrent path segment is too long')
    if (ILLEGAL.test(segment)) throw new PathError('illegal_character', 'torrent file path contains an illegal character')
    if (/[. ]$/.test(segment)) throw new PathError('illegal_suffix', 'torrent file path ends with a dot or space')
    if (RESERVED.test(segment)) throw new PathError('reserved_name', 'torrent file path uses a reserved Windows name')
    out.push(segment)
  }
  return out
}

/** Joins safe segments under root and asserts the result stays inside root. */
export function safeJoin (root, torrentPath) {
  const segments = safeSegments(torrentPath)
  const base = resolve(root)
  const target = resolve(join(base, ...segments))
  const prefix = base.endsWith(sep) ? base : base + sep
  if (!target.startsWith(prefix)) throw new PathError('traversal', 'torrent file path escapes the download folder')
  return target
}

/** A display name with no directory part, safe to show and to put in JSON. */
export function displayName (torrentPath) {
  try {
    return safeSegments(torrentPath).at(-1)
  } catch {
    return 'download'
  }
}

/** Bounds on what a torrent may declare before we refuse it. */
export function checkFileList (files, maxFiles = 2000) {
  if (!Array.isArray(files)) return { ok: false, code: 'bad_metadata', message: 'torrent has no file list' }
  if (files.length === 0) return { ok: false, code: 'bad_metadata', message: 'torrent has no files' }
  if (files.length > maxFiles) {
    return { ok: false, code: 'too_many_files', message: `torrent has more than ${maxFiles} files` }
  }
  let total = 0
  for (const file of files) {
    if (!Number.isSafeInteger(file.length) || file.length < 0) {
      return { ok: false, code: 'bad_metadata', message: 'torrent file has an invalid length' }
    }
    total += file.length
    if (!Number.isSafeInteger(total)) {
      return { ok: false, code: 'bad_metadata', message: 'torrent size overflows' }
    }
    try { safeSegments(file.path) } catch (error) {
      return { ok: false, code: error.code || 'bad_path', message: `unsafe torrent file path: ${error.message}` }
    }
  }
  return { ok: true, total }
}

/** Length of the piece that overlaps a byte offset (for tail alignment). */
export function byteRangeForPieces (startPiece, endPiece, pieceLength, torrentLength) {
  return {
    start: Math.max(0, startPiece * pieceLength),
    end: Math.min(torrentLength, (endPiece + 1) * pieceLength)
  }
}
