// Original implementation, MIT licensed.
//
// Torrent source detection and normalisation. The launcher already rejects
// unsupported schemes, but the helper is runnable on its own, so it repeats
// the check. Only magnet links, http(s) .torrent URLs, local .torrent files
// and bare info hashes (40 hex or 32 base32) are accepted.

import { isAbsolute } from 'node:path'
import { fileURLToPath } from 'node:url'
import parseTorrent from 'parse-torrent'

const HEX40 = /^[0-9a-fA-F]{40}$/
const BASE32_32 = /^[A-Za-z2-7]{32}$/

export class SourceError extends Error {
  constructor (code, message) {
    super(message)
    this.name = 'SourceError'
    this.code = code
  }
}

/**
 * Classifies a raw source string without touching the network or the disk.
 * Returns { kind, value } where kind is one of
 * magnet | torrent-url | torrent-file | infohash.
 */
export function classifySource (raw) {
  if (typeof raw !== 'string') throw new SourceError('bad_source', 'torrent source must be a string')
  const value = raw.trim()
  if (value === '') throw new SourceError('empty_source', 'torrent source is empty')

  if (/^magnet:\?/i.test(value)) return { kind: 'magnet', value }
  if (/^https?:\/\//i.test(value)) return { kind: 'torrent-url', value }
  if (/^file:\/\//i.test(value)) {
    let path
    try { path = fileURLToPath(value) } catch { throw new SourceError('bad_source', 'invalid file URL') }
    return { kind: 'torrent-file', value: path }
  }
  if (HEX40.test(value)) return { kind: 'infohash', value: value.toLowerCase() }
  if (BASE32_32.test(value)) return { kind: 'infohash', value: value.toLowerCase() }
  if (isAbsolute(value) && /\.torrent$/i.test(value)) return { kind: 'torrent-file', value }

  throw new SourceError('unsupported_source',
    'source must be a magnet link, an http(s) .torrent URL, a .torrent file path or an info hash')
}

/** Info hash as 40 lowercase hex characters, or null. */
export async function infoHashOf (raw) {
  const { kind, value } = classifySource(raw)
  if (kind === 'infohash') return value
  if (kind === 'magnet') {
    try {
      const parsed = await parseTorrent(value)
      return typeof parsed.infoHash === 'string' ? parsed.infoHash.toLowerCase() : null
    } catch {
      return null
    }
  }
  return null
}

/** A magnet link that announces to exactly the given trackers. */
export function magnetFor (infoHash, trackers = [], name = '') {
  const hash = String(infoHash).toLowerCase()
  if (!HEX40.test(hash)) throw new SourceError('bad_infohash', 'info hash must be 40 hex characters')
  const params = [`xt=urn:btih:${hash}`]
  if (name) params.push(`dn=${encodeURIComponent(name)}`)
  for (const tracker of trackers) params.push(`tr=${encodeURIComponent(tracker)}`)
  return `magnet:?${params.join('&')}`
}

/**
 * Turns a source into what WebTorrent's add() accepts: a magnet string (so
 * trackers can be attached), a Buffer for a .torrent file, or a Buffer of a
 * previously cached .torrent.
 */
export async function materializeSource (raw, { fetchTorrent, cacheDir, timeoutMs = 30_000, maxBytes = 10 * 1024 * 1024, trackers = [] } = {}) {
  const { kind, value } = classifySource(raw)

  if (kind === 'infohash') {
    return { kind, infoHash: value, input: magnetFor(value, trackers), name: '' }
  }

  if (kind === 'magnet') {
    let parsed
    try { parsed = await parseTorrent(value) }
    catch (error) { throw new SourceError('bad_magnet', `invalid magnet link: ${error.message}`) }
    if (!parsed.infoHash) throw new SourceError('bad_magnet', 'magnet link has no info hash')
    const listed = Array.isArray(parsed.announce) ? parsed.announce : []
    return {
      kind,
      infoHash: String(parsed.infoHash).toLowerCase(),
      input: magnetFor(parsed.infoHash, dedupe([...listed, ...trackers]), parsed.name || ''),
      name: parsed.name || ''
    }
  }

  if (kind === 'torrent-url') {
    const buffer = await fetchTorrent(value, { timeoutMs, maxBytes })
    return await torrentBufferResult(kind, buffer)
  }

  // torrent-file: read from disk, with a hard size cap so a huge file cannot
  // exhaust memory. Errors are reported without echoing the full path.
  const { readFile, stat } = await import('node:fs/promises')
  let info
  try { info = await stat(value) } catch { throw new SourceError('torrent_file_missing', 'torrent file was not found') }
  if (!info.isFile()) throw new SourceError('torrent_file_missing', 'torrent source is not a file')
  if (info.size > maxBytes) throw new SourceError('torrent_file_too_large', 'torrent file is larger than the limit')
  const buffer = await readFile(value)
  return await torrentBufferResult(kind, buffer)
}

async function torrentBufferResult (kind, buffer) {
  if (!Buffer.isBuffer(buffer) || buffer.length < 20 || buffer[0] !== 0x64) {
    throw new SourceError('bad_torrent_file', 'source is not a bencoded .torrent file')
  }
  let parsed
  try { parsed = await parseTorrent(buffer) }
  catch (error) { throw new SourceError('bad_torrent_file', `invalid .torrent file: ${error.message}`) }
  if (!parsed.infoHash) throw new SourceError('bad_torrent_file', 'torrent file has no info hash')
  return { kind, infoHash: String(parsed.infoHash).toLowerCase(), input: buffer, name: parsed.name || '' }
}

/**
 * Downloads a .torrent URL with a size cap. Uses global fetch (undici) and
 * streams the body so a malicious server cannot make us allocate gigabytes.
 */
export async function fetchTorrentBuffer (url, { timeoutMs = 30_000, maxBytes = 10 * 1024 * 1024 } = {}) {
  const controller = new AbortController()
  const timer = setTimeout(() => controller.abort(), timeoutMs)
  try {
    const response = await fetch(url, {
      redirect: 'follow',
      signal: controller.signal,
      headers: { 'user-agent': 'mpvtorrent/1.0' }
    })
    if (!response.ok) throw new SourceError('torrent_fetch_failed', `torrent URL returned HTTP ${response.status}`)
    const declared = Number(response.headers.get('content-length') || '0')
    if (declared > maxBytes) throw new SourceError('torrent_file_too_large', 'torrent file is larger than the limit')

    const chunks = []
    let total = 0
    for await (const chunk of response.body) {
      total += chunk.length
      if (total > maxBytes) {
        throw new SourceError('torrent_file_too_large', 'torrent file is larger than the limit')
      }
      chunks.push(chunk)
    }
    return Buffer.concat(chunks)
  } catch (error) {
    if (error instanceof SourceError) throw error
    if (error.name === 'AbortError') throw new SourceError('torrent_fetch_timeout', 'torrent URL timed out')
    throw new SourceError('torrent_fetch_failed', `could not download torrent file: ${error.message}`)
  } finally {
    clearTimeout(timer)
  }
}

function dedupe (values) {
  return [...new Set(values.filter(Boolean))]
}
