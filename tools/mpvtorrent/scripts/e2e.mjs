// Development harness: runs the helper against a real torrent and keeps stdin
// open like the launcher does. Not part of the shipped executable.

import { spawn } from 'node:child_process'
import { mkdirSync, rmSync } from 'node:fs'
import { join, dirname } from 'node:path'
import { fileURLToPath } from 'node:url'

const root = dirname(dirname(fileURLToPath(import.meta.url)))
const exe = process.env.MPVTORRENT_EXE
const source = process.env.MPVTORRENT_SOURCE ||
  'https://cdimage.debian.org/debian-cd/current/amd64/bt-cd/debian-13.7.0-amd64-netinst.iso.torrent'
const seconds = Number(process.env.MPVTORRENT_SECONDS || '120')
const work = join(process.env.TEMP || '.', 'mpvtorrent-e2e')

rmSync(work, { recursive: true, force: true })
mkdirSync(join(work, 'download'), { recursive: true })
mkdirSync(join(work, 'cache'), { recursive: true })

const args = [
  '--source', source,
  '--download-dir', join(work, 'download'),
  '--cache-dir', join(work, 'cache'),
  '--launcher-pid', String(process.pid),
  '--verbose',
  '--log-file', join(work, 'helper.log'),
  '--contract', '1'
]

const command = exe || process.execPath
const commandArgs = exe ? args : [join(root, 'src', 'index.js'), ...args]
const child = spawn(command, commandArgs, { stdio: ['pipe', 'pipe', 'pipe'], windowsHide: true })

let lastState = 0
child.stdout.setEncoding('utf8')
child.stdout.on('data', chunk => {
  for (const line of chunk.split('\n')) {
    if (!line.trim()) continue
    let event
    try { event = JSON.parse(line) } catch { console.log('RAW', line); continue }
    if (event.type === 'state') {
      if (Date.now() - lastState < 10_000) continue
      lastState = Date.now()
    }
    console.log(JSON.stringify(event))
  }
})
child.stderr.on('data', chunk => process.stderr.write(chunk))

const started = Date.now()
const timer = setTimeout(() => {
  console.log(`--- sending close after ${Math.round((Date.now() - started) / 1000)}s ---`)
  try { child.stdin.write('{"cmd":"close"}\n') } catch { /* gone */ }
}, seconds * 1000)

const force = setTimeout(() => {
  console.log('--- forcing exit ---')
  child.kill()
  process.exit(1)
}, (seconds + 25) * 1000)

child.on('exit', code => {
  clearTimeout(timer)
  clearTimeout(force)
  console.log(`--- helper exited with ${code} after ${Math.round((Date.now() - started) / 1000)}s ---`)
  process.exit(0)
})
