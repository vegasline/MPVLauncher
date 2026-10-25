// Original implementation, MIT licensed.
//
// Manual end-to-end check against a legal, well-seeded torrent. It exercises
// the same contract the launcher uses: JSON events on stdout, a ready event
// with a local URL, HEAD and Range requests, a mid-file seek, and clean
// shutdown with the download folder removed.
//
//   node scripts/e2e.js
//   node scripts/e2e.js --exe dist/mpvtorrent.exe --source <url-or-path>
//
// This is intentionally not part of `npm test`: it needs the public internet
// and a healthy swarm.

import { spawn } from 'node:child_process'
import { existsSync } from 'node:fs'
import { mkdtemp, rm, writeFile } from 'node:fs/promises'
import http from 'node:http'
import { tmpdir } from 'node:os'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

const root = dirname(dirname(fileURLToPath(import.meta.url)))
const argv = process.argv.slice(2)
const option = name => {
  const index = argv.indexOf(`--${name}`)
  return index >= 0 ? argv[index + 1] : undefined
}

const source = option('source') ||
  'https://cdimage.debian.org/debian-cd/current/amd64/bt-cd/debian-13.7.0-amd64-netinst.iso.torrent'
const exe = option('exe')
const overallMs = Number(option('seconds') || 420) * 1000
const trackers = [
  'udp://tracker.opentrackr.org:1337/announce',
  'udp://tracker.torrent.eu.org:451/announce',
  'udp://exodus.desync.com:6969/announce',
  'udp://explodie.org:6969/announce',
  'udp://tracker.dler.org:6969/announce'
]

const work = await mkdtemp(join(tmpdir(), 'mpvtorrent-e2e-'))
const downloadDir = join(work, 'download')
const cacheDir = join(work, 'cache')
const trackerFile = join(work, 'trackers.txt')
await writeFile(trackerFile, trackers.join('\n') + '\n')

const helperArgs = [
  '--source', source,
  '--download-dir', downloadDir,
  '--cache-dir', cacheDir,
  '--launcher-pid', String(process.pid),
  '--tracker-file', trackerFile,
  '--window-mib', '8',
  '--log-file', join(work, 'mpvtorrent.log'),
  '--verbose'
]

const helper = exe
  ? spawn(exe, helperArgs, { stdio: ['pipe', 'pipe', 'pipe'] })
  : spawn(process.execPath, [join(root, 'src', 'index.js'), ...helperArgs], { stdio: ['pipe', 'pipe', 'pipe'] })

let ready = null
let exitCode = null
const events = []

helper.stderr.on('data', chunk => process.stderr.write(chunk))

const lines = []
helper.stdout.on('data', chunk => {
  for (const line of chunk.toString().split('\n')) {
    if (line.trim() === '') continue
    lines.push(line)
  }
})

const waitFor = (predicate, timeoutMs, label) => new Promise((resolve, reject) => {
  const started = Date.now()
  const timer = setInterval(() => {
    for (const line of lines.splice(0)) {
      const event = JSON.parse(line)
      events.push(event)
      if (event.type === 'state' && event.phase !== 'playing' && event.peers !== undefined) {
        process.stdout.write(`\r${event.phase} peers=${event.peers} down=${(event.downloadSpeed / 1024).toFixed(0)}KiB/s progress=${(event.progress * 100).toFixed(1)}%   `)
      }
      if (predicate(event)) {
        clearInterval(timer)
        resolve(event)
        return
      }
      if (event.type === 'error' && event.fatal) {
        clearInterval(timer)
        reject(new Error(`${event.code}: ${event.message}`))
        return
      }
    }
    if (Date.now() - started > timeoutMs) {
      clearInterval(timer)
      reject(new Error(`timed out waiting for ${label}`))
    }
  }, 200)
})

const request = (url, headers = {}, method = 'GET') => new Promise((resolve, reject) => {
  const req = http.request(url, { method, headers }, res => {
    const chunks = []
    res.on('data', chunk => chunks.push(chunk))
    res.on('end', () => resolve({ status: res.statusCode, headers: res.headers, body: Buffer.concat(chunks) }))
  })
  req.setTimeout(180_000, () => req.destroy(new Error('request timed out')))
  req.on('error', reject)
  req.end()
})

const overall = setTimeout(() => {
  console.error('\noverall timeout')
  helper.kill()
  process.exit(1)
}, overallMs)

try {
  helper.stdin.write(JSON.stringify({ cmd: 'noop' }) + '\n')
  const meta = await waitFor(e => e.type === 'metadata', 180_000, 'metadata')
  console.log(`\nmetadata: ${meta.files.length} file(s), ${(meta.totalLength / 1024 / 1024).toFixed(0)} MiB`)

  ready = await waitFor(e => e.type === 'ready', 240_000, 'ready')
  console.log(`\nready: ${ready.displayName} (${(ready.size / 1024 / 1024).toFixed(0)} MiB)`)

  const head = await request(ready.url, { range: 'bytes=0-262143' })
  console.log(`range 0-262143 -> ${head.status} (${head.body.length} bytes)`)
  if (head.status !== 206 || head.body.length !== 262144) throw new Error('head range failed')

  const mid = Math.floor(ready.size / 2 / 65536) * 65536
  const seek = await request(ready.url, { range: `bytes=${mid}-${mid + 65535}` })
  console.log(`seek ${mid} -> ${seek.status} (${seek.body.length} bytes)`)
  if (seek.status !== 206 || seek.body.length !== 65536) throw new Error('seek range failed')

  const tail = await request(ready.url, { range: `bytes=${ready.size - 65536}-` })
  console.log(`tail -> ${tail.status} (${tail.body.length} bytes)`)
  if (tail.status !== 206) throw new Error('tail range failed')

  helper.stdin.write(JSON.stringify({ cmd: 'close' }) + '\n')
  exitCode = await new Promise(resolve => helper.on('exit', resolve))
  console.log(`helper exited with ${exitCode}`)
  if (exitCode !== 0) throw new Error(`unexpected exit code ${exitCode}`)
  if (existsSync(downloadDir)) throw new Error('download folder was not removed')

  const closed = await request(ready.url).then(() => false).catch(() => true)
  if (!closed) throw new Error('HTTP endpoint is still reachable after close')
  console.log('download folder removed, endpoint closed')
  console.log('E2E OK')
} catch (error) {
  console.error(`\nE2E FAILED: ${error.message}`)
  helper.kill()
  process.exitCode = 1
} finally {
  clearTimeout(overall)
  await rm(work, { recursive: true, force: true }).catch(() => {})
  void exitCode
}
