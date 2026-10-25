// Original implementation, MIT licensed.

import test from 'node:test'
import assert from 'node:assert/strict'
import http from 'node:http'
import { mkdtemp, rm, writeFile } from 'node:fs/promises'
import { join } from 'node:path'
import { tmpdir } from 'node:os'
import parseTorrent from 'parse-torrent'
import {
  SourceError,
  classifySource,
  fetchTorrentBuffer,
  infoHashOf,
  magnetFor,
  materializeSource
} from '../src/source.js'

const HASH = '0123456789abcdef0123456789abcdef01234567'
const BASE32 = 'abcdefghijklmnopqrstuvwxyz234567'
const MAGNET = `magnet:?xt=urn:btih:${HASH}&dn=Test`

function tinyTorrent () {
  // One 5-byte file, one 16 KiB piece, 20 zero bytes of hashes.
  return Buffer.from(
    'd4:infod6:lengthi5e4:name8:test.bin12:piece lengthi16384e6:pieces20:aaaaaaaaaaaaaaaaaaaaee',
    'utf8'
  )
}

test('sources are classified without I/O', () => {
  assert.equal(classifySource(MAGNET).kind, 'magnet')
  assert.equal(classifySource('https://example.test/a.torrent').kind, 'torrent-url')
  assert.equal(classifySource('http://example.test/a.torrent').kind, 'torrent-url')
  assert.equal(classifySource(HASH).kind, 'infohash')
  assert.equal(classifySource(BASE32).kind, 'infohash')
  assert.equal(classifySource('C:\\tmp\\a.torrent').kind, 'torrent-file')
  assert.throws(() => classifySource('ftp://example.test/a.torrent'), SourceError)
  assert.throws(() => classifySource('a.torrent'), SourceError)
  assert.throws(() => classifySource(''), SourceError)
})

test('info hashes are extracted from magnets only', async () => {
  assert.equal(await infoHashOf(MAGNET), HASH)
  assert.equal(await infoHashOf(HASH), HASH)
  assert.equal(await infoHashOf('https://example.test/a.torrent'), null)
})

test('magnet links carry only the expected trackers', () => {
  const magnet = magnetFor(HASH, ['udp://tracker.example:80/announce'], 'a b')
  assert.match(magnet, new RegExp(`xt=urn:btih:${HASH}`))
  assert.match(magnet, /dn=a%20b/)
  assert.match(magnet, /tr=udp%3A%2F%2Ftracker\.example%3A80%2Fannounce/)
  assert.throws(() => magnetFor('short'), SourceError)
})

test('a .torrent file is read and parsed', async () => {
  const dir = await mkdtemp(join(tmpdir(), 'mpvtorrent-src-'))
  try {
    const path = join(dir, 'tiny.torrent')
    await writeFile(path, tinyTorrent())
    const result = await materializeSource(path)
    assert.equal(result.kind, 'torrent-file')
    assert.equal(result.infoHash, String((await parseTorrent(tinyTorrent())).infoHash).toLowerCase())
    assert.ok(Buffer.isBuffer(result.input))
  } finally {
    await rm(dir, { recursive: true, force: true })
  }
})

test('a malformed torrent file is rejected', async () => {
  const dir = await mkdtemp(join(tmpdir(), 'mpvtorrent-src-'))
  try {
    const path = join(dir, 'bad.torrent')
    await writeFile(path, Buffer.from('d4:info'))
    await assert.rejects(materializeSource(path), error => error.code === 'bad_torrent_file')
  } finally {
    await rm(dir, { recursive: true, force: true })
  }
})

test('a magnet is materialised with extra trackers', async () => {
  const result = await materializeSource(MAGNET, { trackers: ['udp://t.example:1/announce'] })
  assert.equal(result.infoHash, HASH)
  assert.match(result.input, /tr=udp%3A%2F%2Ft\.example%3A1%2Fannounce/)
  assert.match(result.input, new RegExp(HASH))
})

test('torrent URLs are fetched with a size cap', async () => {
  const server = http.createServer((req, res) => {
    res.setHeader('content-type', 'application/x-bittorrent')
    res.end(Buffer.alloc(4096, 0x61))
  })
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve))
  const port = server.address().port
  try {
    await assert.rejects(
      fetchTorrentBuffer(`http://127.0.0.1:${port}/a.torrent`, { maxBytes: 1024 }),
      error => error.code === 'torrent_file_too_large'
    )
    const buffer = await fetchTorrentBuffer(`http://127.0.0.1:${port}/a.torrent`, { maxBytes: 8192 })
    assert.equal(buffer.length, 4096)
  } finally {
    await new Promise(resolve => server.close(resolve))
  }
})

test('a torrent URL returning an error is reported without the URL', async () => {
  const server = http.createServer((req, res) => { res.statusCode = 404; res.end('no') })
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve))
  const port = server.address().port
  try {
    await assert.rejects(
      fetchTorrentBuffer(`http://127.0.0.1:${port}/secret-token/a.torrent`),
      error => error.code === 'torrent_fetch_failed' && !error.message.includes('secret-token')
    )
  } finally {
    await new Promise(resolve => server.close(resolve))
  }
})
