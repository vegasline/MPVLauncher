// Original implementation, MIT licensed.
//
// Entry point. Two hard rules shape this file:
//   1. stdout is a protocol stream - nothing but JSON lines may reach it, so
//      console.log is redirected to stderr before any dependency loads.
//   2. The process must never outlive the launcher. stdin closure, a dead
//      launcher PID, a signal and an uncaught exception all lead to the same
//      close path, which destroys the client and deletes the download folder.

import { rmSync } from 'node:fs'
import { join } from 'node:path'
import { createInterface } from 'node:readline'
import { parseArgs, usage } from './args.js'
import { createEventWriter, errorEvent, helloEvent, parseCommand, versionEvent, CONTRACT_VERSION } from './protocol.js'
import { createLogger } from './logger.js'
import { TorrentSession } from './session.js'
import { isProcessAlive } from './process.js'

console.log = console.error
console.info = console.error

const writer = createEventWriter(process.stdout)
const parsed = parseArgs(process.argv.slice(2))

if (parsed.help) {
  process.stderr.write(usage() + '\n')
  process.exit(0)
}
if (parsed.version) {
  writer.send(versionEvent())
  setTimeout(() => process.exit(0), 50)
} else if (!parsed.ok) {
  writer.send(errorEvent(parsed.code, parsed.message, true))
  process.stderr.write(`mpvtorrent: ${parsed.message}\n\n${usage()}\n`)
  setTimeout(() => process.exit(2), 50)
} else if (parsed.options.contract !== CONTRACT_VERSION) {
  writer.send(errorEvent('contract_mismatch', `unsupported contract version ${parsed.options.contract} (expected ${CONTRACT_VERSION})`, true))
  setTimeout(() => process.exit(2), 50)
} else {
  run(parsed)
}

function run ({ options, warnings }) {
  const log = createLogger({
    verbose: options.verbose,
    logFile: options.logFile || join(options.cacheDir, 'mpvtorrent.log')
  })
  for (const warning of warnings) log.warn('argument adjusted', warning)

  writer.send(helloEvent())

  let session = null
  let exiting = false

  const shutdown = (code) => {
    if (exiting) return
    exiting = true
    writer.close()
    // Give the pipe a moment to flush the final JSON line; nothing else keeps
    // the loop alive because every timer was cleared by close().
    setTimeout(() => process.exit(code), 120)
  }

  // Last-resort synchronous cleanup for paths that bypass close() (an
  // uncaught exception after the async cleanup started, for example).
  process.on('exit', () => {
    try { rmSync(options.downloadDir, { recursive: true, force: true, maxRetries: 2 }) } catch { /* already gone */ }
  })

  const close = (reason, code = 0) => {
    if (!session) return shutdown(code)
    session.close(reason, code).catch(() => shutdown(code))
  }

  session = new TorrentSession({
    options,
    emit: event => writer.send(event),
    log,
    onClose: code => shutdown(code)
  })

  const rl = createInterface({ input: process.stdin, crlfDelay: Infinity })
  rl.on('line', line => {
    const command = parseCommand(line)
    if (!command.ok) {
      writer.send(errorEvent(command.code, 'unrecognised command', false))
      return
    }
    if (command.cmd === 'close') close('command')
    else if (command.cmd === 'select') session.selectFile(command.fileIndex)
  })
  rl.on('close', () => close('stdin_closed'))
  process.stdin.on('error', () => close('stdin_closed'))

  const parentTimer = setInterval(() => {
    if (!isProcessAlive(options.launcherPid)) close('launcher_gone')
  }, 2000)
  parentTimer.unref?.()

  const signal = () => close('signal')
  process.on('SIGINT', signal)
  process.on('SIGTERM', signal)
  process.on('SIGHUP', signal)

  process.on('uncaughtException', error => {
    log.error('uncaught exception', error)
    writer.send(errorEvent('internal', String(error?.message || error), true))
    close('crash', 1)
  })
  process.on('unhandledRejection', reason => {
    log.error('unhandled rejection', reason)
    writer.send(errorEvent('internal', String(reason?.message || reason), true))
    close('crash', 1)
  })

  session.start().catch(error => {
    log.error('start failed', error)
    writer.send(errorEvent(error.code || 'start_failed', String(error.message || error), true))
    close('start_failed', 3)
  })
}
