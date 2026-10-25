// Original implementation, MIT licensed.

import test from 'node:test'
import assert from 'node:assert/strict'
import http from 'node:http'
import { Readable } from 'node:stream'
import { contentTypeFor, createStreamServer } from '../src/http.js'

const TOKEN = 'a'.repeat(48)
const BODY = Buffer.from(Array.from({ length: 100 }, (_, i) => i))

function request (port, { method = 'GET', path = `/s/${TOKEN}`, headers = {} } = {}) {
  return new Promise((resolve, reject) => {
    const req = http.request({
      host: '127.0.0.1',
      port,
      method,
      path,
      headers: { host: `127.0.0.1:${port}`, ...headers }
    }, res => {
      const chunks = []
      res.on('data', chunk => chunks.push(chunk))
      res.on('end', () => resolve({
        status: res.statusCode,
        headers: res.headers,
        body: Buffer.concat(chunks)
      }))
    })
    req.on('error', reject)
    req.end()
  })
}

async function withServer (openStream, fn) {
  const server = createStreamServer({
    token: TOKEN,
    getFileInfo: () => ({ size: BODY.length, name: 'movie.mkv' }),
    openStream,
    timeoutMs: 2000
  })
  await server.listen()
  try {
    return await fn(server.port)
  } finally {
    await server.close()
  }
}

const open = (start, end) => Readable.from(BODY.subarray(start, end + 1))

test('content types follow the file extension', () => {
  assert.equal(contentTypeFor('a.MKV'), 'video/x-matroska')
  assert.equal(contentTypeFor('a.mp4'), 'video/mp4')
  assert.equal(contentTypeFor('a.bin'), 'application/octet-stream')
})

test('HEAD describes the file without opening a stream', async () => {
  let opened = 0
  await withServer((start, end) => { opened++; return open(start, end) }, async port => {
    const res = await request(port, { method: 'HEAD' })
    assert.equal(res.status, 200)
    assert.equal(res.headers['content-length'], '100')
    assert.equal(res.headers['accept-ranges'], 'bytes')
    assert.equal(res.headers['content-type'], 'video/x-matroska')
    assert.equal(res.body.length, 0)
    assert.equal(opened, 0)
  })
})

test('a full GET returns the file', async () => {
  await withServer(open, async port => {
    const res = await request(port)
    assert.equal(res.status, 200)
    assert.deepEqual(res.body, BODY)
  })
})

test('a range GET returns 206 with Content-Range', async () => {
  await withServer(open, async port => {
    const res = await request(port, { headers: { range: 'bytes=10-19' } })
    assert.equal(res.status, 206)
    assert.equal(res.headers['content-range'], 'bytes 10-19/100')
    assert.equal(res.headers['content-length'], '10')
    assert.deepEqual(res.body, BODY.subarray(10, 20))
  })
})

test('suffix and open-ended ranges work', async () => {
  await withServer(open, async port => {
    const suffix = await request(port, { headers: { range: 'bytes=-5' } })
    assert.equal(suffix.status, 206)
    assert.deepEqual(suffix.body, BODY.subarray(95))
    const openEnded = await request(port, { headers: { range: 'bytes=90-' } })
    assert.equal(openEnded.status, 206)
    assert.deepEqual(openEnded.body, BODY.subarray(90))
  })
})

test('HEAD honours a range without a body', async () => {
  await withServer(open, async port => {
    const res = await request(port, { method: 'HEAD', headers: { range: 'bytes=10-19' } })
    assert.equal(res.status, 206)
    assert.equal(res.headers['content-range'], 'bytes 10-19/100')
    assert.equal(res.body.length, 0)
  })
})

test('unsatisfiable ranges get 416, malformed ranges get 400', async () => {
  await withServer(open, async port => {
    const unsat = await request(port, { headers: { range: 'bytes=1000-' } })
    assert.equal(unsat.status, 416)
    assert.equal(unsat.headers['content-range'], 'bytes */100')
    const bad = await request(port, { headers: { range: 'bytes=0-1,4-5' } })
    assert.equal(bad.status, 400)
  })
})

test('foreign Host headers are rejected (DNS rebinding)', async () => {
  await withServer(open, async port => {
    const res = await request(port, { headers: { host: 'attacker.example:80' } })
    assert.equal(res.status, 403)
  })
})

test('wrong token, wrong method and wrong path are rejected', async () => {
  await withServer(open, async port => {
    assert.equal((await request(port, { path: `/s/${'b'.repeat(48)}` })).status, 404)
    assert.equal((await request(port, { path: '/' })).status, 404)
    const post = await request(port, { method: 'POST' })
    assert.equal(post.status, 405)
    assert.equal(post.headers.allow, 'GET, HEAD')
  })
})

test('a failing stream becomes a 500 before headers', async () => {
  await withServer(() => { throw new Error('no peer') }, async port => {
    const res = await request(port)
    assert.equal(res.status, 500)
  })
})
