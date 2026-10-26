// Original implementation, MIT licensed.
//
// One torrent session: source -> metadata -> file selection -> local HTTP
// stream. Everything here is scoped to the process lifetime. There is no
// seeding mode, no resume database and no background service: when the session
// closes, the client is destroyed and the download folder is removed.

import { randomBytes } from 'node:crypto'
import { mkdir, readFile, rename, rm, writeFile } from 'node:fs/promises'
import { join } from 'node:path'
import { Readable } from 'node:stream'
import WebTorrent from 'webtorrent'
import parseTorrent from 'parse-torrent'
import { fetchTorrentBuffer, materializeSource } from './source.js'
import { checkFileList, displayName } from './paths.js'
import { planInitialPieces, planWindow, rangeIsComplete, shouldStartPlayback } from './window.js'
import { createStreamServer } from './http.js'
import { createStreamToken } from './host.js'
import { parseTrackerList } from './trackers.js'
import { applyUploadPolicy, watchWire } from './upload.js'

const VIDEO = /\.(mp4|m4v|mkv|webm|avi|mov|ts|m2ts|mpg|mpeg|ogv|flv|wmv|3gp)$/i
const STATUS_MS = 500
const HINT_MS = 45_000
const GIVE_UP_MS = 120_000

// WebTorrent's file iterator selects every piece of the range it is given.
// mpv asks for open-ended ranges (bytes=N-), so handing the whole remainder to
// createReadStream selects the rest of the file and defeats the download
// window. Reading in bounded segments keeps each selection inside the window.
const READ_SEGMENT_BYTES = 16 * 1024 * 1024

/**
 * Streams `from..to` (inclusive) of a torrent file in bounded segments.
 *
 * Each segment gets its own file iterator, so at most one segment of pieces is
 * selected beyond what has already been read. The async generator stops pulling
 * when the HTTP response applies backpressure, which ties the download rate to
 * the player instead of the other way round.
 */
function readInSegments (file, from, to, segmentBytes) {
  const size = Math.max(1, Math.floor(segmentBytes) || READ_SEGMENT_BYTES)

  async function * generate () {
    for (let position = from; position <= to; position += size) {
      const last = Math.min(position + size - 1, to)
      const stream = file.createReadStream({ start: position, end: last })
      for await (const chunk of stream) yield chunk
    }
  }

  return Readable.from(generate())
}
const PRELOAD_GIVE_UP_MS = 300_000
// A range that starts more than this away from the last read is a seek, not
// the next chunk of the same playback.
const SEEK_DEADBAND_BYTES = 4 * 1024 * 1024
const DHT_SAVE_MS = 60_000

// Standard bootstrap routers used by bittorrent-dht itself. They are listed
// here only so cached nodes can be combined with them rather than replace them.
const DHT_BOOTSTRAP = [
  'router.bittorrent.com:6881',
  'router.utorrent.com:6881',
  'dht.transmissionbt.com:6881',
  'dht.aelitis.com:6881'
]

export class TorrentSession {
  constructor ({ options, emit, log, onClose }) {
    this.options = options
    this.emit = emit
    this.log = log
    this.onClose = onClose
    this.client = null
    this.torrent = null
    this.server = null
    this.currentFile = null
    this.fileIndex = -1
    this.position = 0
    this.window = null
    this.urgentRange = null
    this.preload = null
    this.ready = false
    this.metaHandled = false
    this.closing = false
    this.closed = false
    this.infoHash = ''
    this.timers = []
    this.token = createStreamToken(randomBytes)
    this.preloadCritical = []
    this.preloadStartedAt = 0
    this.tailPending = null
    this.tailSelected = false
    this.tailWaitTimer = null
  }

  async start () {
    const { options, log } = this
    const trackers = await this.readTrackers()
    log.info('starting session', { contract: options.contract, trackers: trackers.length })

    await mkdir(options.downloadDir, { recursive: true })
    const source = await materializeSource(options.source, {
      cacheDir: options.cacheDir,
      trackers,
      maxBytes: options.maxTorrentBytes ?? 10 * 1024 * 1024,
      fetchTorrent: (url, opts) => fetchTorrentBuffer(url, opts)
    })
    this.infoHash = source.infoHash
    this.emit({ type: 'source', kind: source.kind, infoHash: source.infoHash })

    const cached = await this.readMetadataCache(source.infoHash)
    let input = source.input
    if (cached) {
      input = cached
      log.info('using cached metadata', { infoHash: source.infoHash })
    }

    const dhtNodes = await this.readDhtNodes()
    const clientOptions = {
      maxConns: options.maxConns,
      utp: false,
      webSeeds: false,
      natUpnp: false,
      natPmp: false,
      // WebTorrent forwards this object straight to bittorrent-dht. Passing
      // `true` forwards nothing, so the bootstrap list must be explicit even
      // when no nodes have been cached yet.
      dht: { bootstrap: [...dhtNodes, ...DHT_BOOTSTRAP] }
    }
    this.client = new WebTorrent(clientOptions)
    this.upload = applyUploadPolicy(this.client, options.uploadLimitKiB)
    log.info('upload policy', this.upload)

    this.client.on('error', error => {
      log.error('client error', error)
      this.emit({ type: 'error', code: 'client_error', message: 'torrent client error', fatal: false })
    })

    this.torrent = this.client.add(input, {
      path: options.downloadDir,
      deselect: true,
      announce: trackers
    })

    this.torrent.on('warning', warning => log.warn('torrent warning', warning))
    this.torrent.on('error', error => {
      log.error('torrent error', error)
      this.emit({ type: 'error', code: 'torrent_error', message: 'torrent error', fatal: false })
    })
    this.torrent.on('wire', wire => watchWire(wire, this.upload.chokeAll, log))
    this.torrent.on('metadata', () => {
      this.handleMetadata().catch(error => this.fail('metadata_failed', error.message))
    })
    this.torrent.on('verified', () => { this.maybeReady(); this.maybeTailComplete() })

    this.startTimers()
    this.emitState()
  }

  startTimers () {
    this.every(STATUS_MS, () => this.emitState())
    this.every(DHT_SAVE_MS, () => { void this.saveDhtNodes() })
    this.later(HINT_MS, () => {
      if (!this.metaHandled) {
        this.emit({ type: 'hint', code: 'still_searching', message: 'still looking for peers and metadata' })
      }
    })
    this.later(GIVE_UP_MS, () => {
      if (this.closing) return
      if (!this.metaHandled) {
        this.fail('metadata_timeout', 'no metadata after 120 seconds')
      } else if (this.torrent && this.torrent.received === 0) {
        this.fail('no_data', 'metadata found but no peer sent data within 120 seconds')
      }
    })
  }

  async handleMetadata () {
    if (this.metaHandled || this.closing) return
    const torrent = this.torrent
    if (!torrent || !torrent.files || torrent.files.length === 0) {
      return this.fail('bad_metadata', 'torrent has no files')
    }

    const check = checkFileList(torrent.files, this.options.maxFileCount)
    if (!check.ok) return this.fail(check.code, check.message)

    this.metaHandled = true
    this.clearLater(HINT_MS)
    this.clearLater(GIVE_UP_MS)

    const pieceLength = torrent.pieceLength
    if (pieceLength > 8 * 1024 * 1024) {
      this.log.warn('piece size is large', { pieceLength })
    }

    this.emit({
      type: 'metadata',
      infoHash: this.infoHash,
      name: torrent.name,
      totalLength: torrent.length,
      pieceLength,
      files: torrent.files.map((file, index) => ({
        index,
        name: file.path,
        displayName: displayName(file.path),
        length: file.length
      }))
    })

    this.saveMetadataCache().catch(error => this.log.warn('metadata cache write failed', error))

    // A multi-file torrent is the user's choice, not ours. The helper waits
    // for a `select` command and only preloads after it arrives; a single-file
    // torrent has nothing to choose, and a remembered index from the launcher
    // is an explicit choice already.
    const files = torrent.files
    const remembered = this.options.selectFile
    if (files.length === 1) {
      await this.beginFile(0, { initial: true })
    } else if (Number.isInteger(remembered) && remembered >= 0 && remembered < files.length) {
      await this.beginFile(remembered, { initial: true })
    } else {
      this.emit({ type: 'select_required', infoHash: this.infoHash, fileCount: files.length })
    }
  }

  /** Largest video file, unless the launcher asked for a specific index. */
  chooseFile () {
    const files = this.torrent.files
    const wanted = this.options.selectFile
    if (Number.isInteger(wanted) && wanted >= 0 && wanted < files.length) return wanted

    let best = -1
    for (let i = 0; i < files.length; i++) {
      const file = files[i]
      if (!VIDEO.test(file.path)) continue
      if (best < 0 || file.length > files[best].length) best = i
    }
    if (best >= 0) return best

    best = 0
    for (let i = 1; i < files.length; i++) {
      if (files[i].length > files[best].length) best = i
    }
    return best
  }

  async beginFile (index, { initial = false } = {}) {
    const files = this.torrent.files
    if (!Number.isInteger(index) || index < 0 || index >= files.length) {
      return this.fail('file_not_found', 'selected file does not exist in this torrent')
    }

    this.deselectAll()
    this.fileIndex = index
    this.currentFile = files[index]
    this.position = 0
    this.ready = false
    this.window = null
    this.preload = null

    if (!this.server) await this.startServer()

    if (initial) {
      const plan = planInitialPieces({
        fileOffset: this.currentFile.offset,
        fileLength: this.currentFile.length,
        pieceLength: this.torrent.pieceLength,
        pieceCount: this.torrent.pieces.length
      })
      this.preload = plan
      this.preloadStartedAt = Date.now()
      this.tailWaitTimer = null
      this.selectRange(plan.head.startPiece, plan.head.endPiece, 2)
      // The tail is a seek convenience, not a precondition: it is selected in
      // armTailWait or becomeReady. Requesting it now would make a single slow
      // peer split its bandwidth between two ranges, doubling the wait for the
      // only part playback actually needs.
      this.tailSelected = false
      this.markCritical(plan.head.startPiece, plan.head.endPiece)
      this.preloadCritical = [
        { start: plan.head.startPiece, end: plan.head.endPiece }
      ]
      this.later(PRELOAD_GIVE_UP_MS, () => {
        if (!this.ready) this.fail('preload_timeout', 'the beginning of the video did not arrive in time')
      })
      this.log.info('preloading head', {
        index,
        head: plan.head,
        tail: plan.tail,
        tailWaitSec: this.options.tailWaitSec
      })
    } else {
      this.applyWindow(this.plan())
    }

    this.emit({
      type: 'selected',
      fileIndex: index,
      fileName: this.currentFile.path,
      displayName: this.currentFile.name,
      size: this.currentFile.length
    })
    this.maybeReady()
  }

  maybeReady () {
    if (this.ready || this.closing || !this.preload || !this.torrent) return
    const bitfield = this.torrent.bitfield
    if (!bitfield) return
    const head = this.preload.head
    const tail = this.preload.tail
    const headComplete = rangeIsComplete(bitfield, head.startPiece, head.endPiece)
    if (!headComplete) return

    const tailComplete = rangeIsComplete(bitfield, tail.startPiece, tail.endPiece)
    const tailWaitMs = Math.max(0, Number(this.options.tailWaitSec) || 0) * 1000
    const waitedMs = Math.max(0, Date.now() - this.preloadStartedAt)
    if (!shouldStartPlayback({ headComplete, tailComplete, tailWaitMs, waitedMs })) {
      this.armTailWait(tailWaitMs - waitedMs)
      return
    }

    this.becomeReady(tail, tailComplete)
  }

  /** One re-check when an optional tail wait has not elapsed yet. */
  armTailWait (remainingMs) {
    if (this.ready || this.tailWaitTimer) return
    // An optional tail wait is pointless unless the tail is being fetched
    // during it, so this is the first moment the extra range is worth asking
    // for: the head is already complete here.
    this.selectTail()
    this.tailWaitTimer = this.later(Math.max(1, Math.round(remainingMs)), () => {
      this.tailWaitTimer = null
      this.maybeReady()
    })
  }

  /** Selects the preload tail once, at low priority. */
  selectTail (tail = this.preload?.tail) {
    if (!tail || this.tailSelected) return
    this.tailSelected = true
    this.selectRange(tail.startPiece, tail.endPiece, 1)
  }

  becomeReady (tail, tailComplete) {
    this.ready = true
    if (this.tailWaitTimer) {
      clearTimeout(this.tailWaitTimer)
      this.tailWaitTimer = null
    }

    // The head has done its job. Keep the tail selected at low priority until
    // it finishes so seeking near the end still works, but never block on it.
    const head = this.preload.head
    this.deselectRange(head.startPiece, head.endPiece)
    clearCriticalRange(this.torrent, head.startPiece, head.endPiece)
    this.preloadCritical = []
    this.preload = null

    if (tailComplete) {
      this.deselectRange(tail.startPiece, tail.endPiece)
      this.tailPending = null
    } else {
      // Now that the head is complete the tail is the only reason to keep
      // downloading, so it starts here unless an optional wait already did.
      this.selectTail(tail)
      this.tailPending = { start: tail.startPiece, end: tail.endPiece }
    }

    this.applyWindow(this.plan(), { force: true })

    this.emit({
      type: 'ready',
      url: this.server.url,
      fileIndex: this.fileIndex,
      fileName: this.currentFile.path,
      displayName: this.currentFile.name,
      size: this.currentFile.length,
      pieceLength: this.torrent.pieceLength
    })
    this.log.info('ready', { infoHash: this.infoHash, fileIndex: this.fileIndex })
  }

  maybeTailComplete () {
    if (!this.tailPending || !this.torrent) return
    const bitfield = this.torrent.bitfield
    if (!bitfield) return
    if (!rangeIsComplete(bitfield, this.tailPending.start, this.tailPending.end)) return
    this.deselectRange(this.tailPending.start, this.tailPending.end)
    this.tailPending = null
    this.log.debug('tail preload complete')
  }

  async startServer () {
    this.server = createStreamServer({
      token: this.token,
      timeoutMs: this.options.readTimeoutMs,
      getFileInfo: () => this.currentFile
        ? { size: this.currentFile.length, name: this.currentFile.name }
        : null,
      openStream: (start, end) => this.openStream(start, end),
      onReadStart: offset => this.noteSeek(offset),
      onProgress: offset => this.noteRead(offset),
      log: this.log
    })
    await this.server.listen('127.0.0.1')
    this.log.info('stream server listening', { port: this.server.port })
  }

  openStream (start, end) {
    const file = this.currentFile
    if (!file) throw new Error('no file selected')
    const from = Math.max(0, Math.min(start, file.length - 1))
    const to = Math.max(from, Math.min(end, file.length - 1))
    return readInSegments(file, from, to, this.options.readSegmentBytes || READ_SEGMENT_BYTES)
  }

  /**
   * A seek arrives as a fresh GET for a different offset. The window has to
   * move at that moment, because the response blocks until its first piece
   * arrives: waiting for progress reports would leave the old window in place
   * while mpv sits on a range nobody is downloading.
   *
   * Small forward steps are the normal read path and are ignored here, so a
   * seek cannot be confused with sequential playback.
   */
  noteSeek (absoluteFileOffset) {
    if (!this.ready || this.closing || !this.currentFile || !this.torrent) return
    const target = Math.max(0, Math.min(absoluteFileOffset, this.currentFile.length - 1))
    if (Math.abs(target - this.position) < SEEK_DEADBAND_BYTES) return

    this.position = target
    this.applyWindow(this.plan(target), { force: true })
    this.log.debug('seek', { position: target, startPiece: this.window?.startPiece })
  }

  plan (position = this.position) {
    return planWindow({
      position,
      fileOffset: this.currentFile.offset,
      fileLength: this.currentFile.length,
      pieceLength: this.torrent.pieceLength,
      pieceCount: this.torrent.pieces.length,
      forwardMiB: this.options.forwardWindowMiB,
      backMiB: 6,
      urgentMiB: 8
    })
  }

  noteRead (absoluteFileOffset) {
    if (!this.ready || this.closing || !this.currentFile) return
    this.position = Math.max(0, Math.min(absoluteFileOffset, this.currentFile.length - 1))
    const next = this.plan()
    if (!this.window || next.startPiece !== this.window.startPiece || next.endPiece !== this.window.endPiece) {
      this.applyWindow(next)
      return
    }
    // The window did not move a whole piece, but the urgent tier follows the
    // read head, so update it even inside a stationary window.
    const headPiece = Math.floor((this.currentFile.offset + this.position) / this.torrent.pieceLength)
    if (!this.urgentRange || this.urgentRange.start !== headPiece || this.urgentRange.end !== next.urgentEndPiece) {
      this.clearCritical()
      this.markCritical(headPiece, next.urgentEndPiece)
      this.urgentRange = { start: headPiece, end: next.urgentEndPiece }
    }
  }

  applyWindow (plan, { force = false } = {}) {
    if (!force && this.window &&
        this.window.startPiece === plan.startPiece &&
        this.window.endPiece === plan.endPiece) return

    if (this.window) this.deselectRange(this.window.startPiece, this.window.endPiece)
    this.clearCritical()
    this.selectRange(plan.startPiece, plan.endPiece, 1)

    const headPiece = Math.max(plan.startPiece,
      Math.floor((this.currentFile.offset + this.position) / this.torrent.pieceLength))
    this.markCritical(headPiece, plan.urgentEndPiece)
    this.urgentRange = { start: headPiece, end: plan.urgentEndPiece }
    this.window = plan
    this.log.debug('window', { start: plan.startPiece, end: plan.endPiece, urgentEnd: plan.urgentEndPiece })
  }

  selectRange (start, end, priority) {
    try { this.torrent.select(start, end, priority) } catch (error) { this.log.warn('select failed', error) }
  }

  deselectRange (start, end) {
    try { this.torrent.deselect(start, end) } catch (error) { this.log.warn('deselect failed', error) }
  }

  deselectAll () {
    if (this.tailPending) {
      this.deselectRange(this.tailPending.start, this.tailPending.end)
      this.tailPending = null
    }
    if (this.tailWaitTimer) {
      clearTimeout(this.tailWaitTimer)
      this.tailWaitTimer = null
    }
    if (this.window) this.deselectRange(this.window.startPiece, this.window.endPiece)
    if (this.preload) {
      this.deselectRange(this.preload.head.startPiece, this.preload.head.endPiece)
      this.deselectRange(this.preload.tail.startPiece, this.preload.tail.endPiece)
    }
    this.window = null
    this.preload = null
    this.tailSelected = false
    this.clearCritical()
    for (const range of this.preloadCritical) {
      clearCriticalRange(this.torrent, range.start, range.end)
    }
    this.preloadCritical = []
  }

  markCritical (start, end) {
    try { this.torrent.critical(start, end) } catch (error) { this.log.warn('critical failed', error) }
  }

  clearCritical () {
    if (!this.urgentRange) return
    clearCriticalRange(this.torrent, this.urgentRange.start, this.urgentRange.end)
    this.urgentRange = null
  }

  selectFile (index) {
    if (!this.metaHandled) return this.emit({ type: 'error', code: 'not_ready', message: 'metadata is not available yet', fatal: false })
    // A choice from the picker is the initial file for this session, so it
    // must go through the same preload-and-ready path as a single-file torrent.
    // Without `initial` the session selects the window but never emits ready.
    this.beginFile(index, { initial: true }).catch(error => this.fail('file_select_failed', error.message))
  }

  emitState () {
    if (this.closing || !this.torrent) return
    const torrent = this.torrent
    this.emit({
      type: 'state',
      phase: this.ready ? 'playing' : (this.fileIndex >= 0 ? 'preload' : (this.metaHandled ? 'select' : 'metadata')),
      peers: torrent.numPeers,
      downloadSpeed: Math.max(0, Math.round(torrent.downloadSpeed || 0)),
      uploadSpeed: Math.max(0, Math.round(torrent.uploadSpeed || 0)),
      progress: Number((torrent.progress || 0).toFixed(4)),
      received: torrent.received || 0,
      length: torrent.length || 0,
      fileIndex: this.fileIndex,
      window: this.window ? { startPiece: this.window.startPiece, endPiece: this.window.endPiece } : null
    })
  }

  async readTrackers () {
    if (!this.options.trackerFile) return []
    try {
      return parseTrackerList(await readFile(this.options.trackerFile, 'utf8'))
    } catch {
      return []
    }
  }

  metadataPath () {
    return join(this.options.cacheDir, 'metadata', `${this.infoHash}.torrent`)
  }

  async readMetadataCache (infoHash) {
    try {
      const buffer = await readFile(join(this.options.cacheDir, 'metadata', `${infoHash}.torrent`))
      if (buffer.length > 10 * 1024 * 1024 || buffer[0] !== 0x64) return null
      const parsed = await parseTorrent(buffer)
      return String(parsed.infoHash).toLowerCase() === infoHash ? buffer : null
    } catch {
      return null
    }
  }

  async saveMetadataCache () {
    if (!this.torrent?.torrentFile || !this.infoHash) return
    const dir = join(this.options.cacheDir, 'metadata')
    await mkdir(dir, { recursive: true })
    const target = this.metadataPath()
    const temp = `${target}.${process.pid}.tmp`
    await writeFile(temp, Buffer.from(this.torrent.torrentFile))
    await rename(temp, target)
  }

  async readDhtNodes () {
    try {
      const value = JSON.parse(await readFile(join(this.options.cacheDir, 'dht-nodes.json'), 'utf8'))
      if (!Array.isArray(value)) return []
      return value
        .filter(node => node && typeof node.host === 'string' && Number.isInteger(node.port))
        .slice(0, 500)
        .map(node => `${node.host}:${node.port}`)
    } catch {
      return []
    }
  }

  async saveDhtNodes () {
    try {
      const json = this.client?.dht?.toJSON?.()
      if (!json || !Array.isArray(json.nodes)) return
      const nodes = json.nodes
        .filter(node => node && typeof node.host === 'string' && Number.isInteger(node.port))
        .slice(0, 500)
        .map(node => ({ host: node.host, port: node.port }))
      await mkdir(this.options.cacheDir, { recursive: true })
      await writeFile(join(this.options.cacheDir, 'dht-nodes.json'), JSON.stringify(nodes))
    } catch (error) {
      this.log.debug('dht save skipped', error)
    }
  }

  every (ms, fn) {
    const timer = setInterval(() => { try { fn() } catch (error) { this.log.warn('timer failed', error) } }, ms)
    timer.unref?.()
    this.timers.push({ timer, kind: 'interval', ms })
    return timer
  }

  later (ms, fn) {
    const timer = setTimeout(() => { try { fn() } catch (error) { this.log.warn('timer failed', error) } }, ms)
    timer.unref?.()
    this.timers.push({ timer, kind: 'timeout', ms })
    return timer
  }

  clearLater (ms) {
    for (const entry of this.timers) {
      if (entry.kind === 'timeout' && entry.ms === ms) clearTimeout(entry.timer)
    }
  }

  fail (code, message) {
    this.emit({ type: 'error', code, message: String(message || code), fatal: true })
    this.log.error('fatal', { code, message })
    void this.close('error', 3)
  }

  async close (reason = 'closed', exitCode = 0) {
    if (this.closing || this.closed) return
    this.closing = true
    this.log.info('closing session', { reason })

    for (const entry of this.timers) {
      if (entry.kind === 'interval') clearInterval(entry.timer)
      else clearTimeout(entry.timer)
    }
    this.timers = []

    try { await this.server?.close() } catch (error) { this.log.debug('server close failed', error) }
    try { await this.saveDhtNodes() } catch { /* best effort */ }

    try {
      if (this.client && !this.client.destroyed) {
        await new Promise(resolve => {
          try { this.client.destroy(() => resolve()) } catch { resolve() }
        })
      }
    } catch (error) {
      this.log.warn('client destroy failed', error)
    }

    // Removing the session folder is part of the contract: no partial data may
    // survive a crashed or killed launcher.
    try {
      await rm(this.options.downloadDir, { recursive: true, force: true, maxRetries: 3 })
    } catch (error) {
      this.log.warn('download folder cleanup failed', error)
    }

    this.closed = true
    this.emit({ type: 'closed', reason })
    try { this.onClose?.(exitCode) } catch { /* exiting anyway */ }
  }
}

function clearCriticalRange (torrent, start, end) {
  const critical = torrent?._critical
  if (!Array.isArray(critical)) return
  for (let i = start; i <= end; i++) critical[i] = false
}
