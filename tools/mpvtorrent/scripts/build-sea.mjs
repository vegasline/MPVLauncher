// Original implementation, MIT licensed.
//
// Builds dist/mpvtorrent.exe: esbuild turns the ESM sources and their
// dependencies into one CommonJS file, Node's SEA tooling wraps that file into
// a blob, and postject injects the blob into a copy of the running node.exe.
// No Node.js installation is required on the target machine.

import { execFileSync } from 'node:child_process'
import { createHash } from 'node:crypto'
import { copyFileSync, mkdirSync, readFileSync, rmSync, statSync, writeFileSync } from 'node:fs'
import { createRequire } from 'node:module'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { build } from 'esbuild'

const require = createRequire(import.meta.url)
const { inject } = require('postject')

const root = dirname(dirname(fileURLToPath(import.meta.url)))
const buildDir = join(root, 'build')
const distDir = join(root, 'dist')
const bundlePath = join(buildDir, 'mpvtorrent.cjs')
const blobPath = join(buildDir, 'mpvtorrent.blob')
const exePath = join(distDir, 'mpvtorrent.exe')
const seaConfigPath = join(buildDir, 'sea-config.json')

const pkg = JSON.parse(readFileSync(join(root, 'package.json'), 'utf8'))

rmSync(buildDir, { recursive: true, force: true })
rmSync(distDir, { recursive: true, force: true })
mkdirSync(buildDir, { recursive: true })
mkdirSync(distDir, { recursive: true })

console.log(`bundling mpvtorrent ${pkg.version}`)
await build({
  entryPoints: [join(root, 'src', 'index.js')],
  outfile: bundlePath,
  bundle: true,
  platform: 'node',
  format: 'cjs',
  target: 'node24',
  legalComments: 'none',
  sourcemap: false,
  logLevel: 'warning'
})

writeFileSync(seaConfigPath, JSON.stringify({
  main: bundlePath,
  output: blobPath,
  disableExperimentalSEAWarning: true,
  useSnapshot: false,
  useCodeCache: false
}, null, 2))

console.log('creating SEA blob')
execFileSync(process.execPath, ['--experimental-sea-config', seaConfigPath], { stdio: 'inherit' })

copyFileSync(process.execPath, exePath)
console.log('injecting blob into the node runtime')
await inject(exePath, 'NODE_SEA_BLOB', readFileSync(blobPath), {
  sentinelFuse: 'NODE_SEA_FUSE_fce680ab2cc467b6e072b8b5df1996b2'
})

const bytes = statSync(exePath).size
const hash = createHash('sha256').update(readFileSync(exePath)).digest('hex')
writeFileSync(`${exePath}.sha256`, `${hash}  mpvtorrent.exe\n`)

console.log(`dist/mpvtorrent.exe  ${(bytes / 1024 / 1024).toFixed(1)} MiB`)
console.log(`sha256 ${hash}`)
