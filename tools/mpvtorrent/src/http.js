// Original implementation, MIT licensed.
//
// Local streaming endpoint. WebTorrent ships an HTTP server for files, but it
// has no Host/token gate, no stall timeout and no hook that lets us slide the
// piece window from the read position, so this server is written here. It
// supports exactly what mpv needs: HEAD, GET and single byte ranges.

import http from 'node:http'
import { extname } from 'node:path'
import { isAllowedHost, isAuthorizedPath } from './host.js'
import { contentRange, parseRange } from './range.js'

const TYPES = new Map([
  ['.mp4', 'video/mp4'],
  ['.m4v', 'video/mp4'],
  ['.mkv', 'video/x-matroska'],
  ['.webm', 'video/webm'],
  ['.avi', 'video/x-msvideo'],
  ['.mov', 'video/quicktime'],
  ['.ts', 'video/mp2t'],
  ['.m2ts', 'video/mp2t'],
  ['.mpg', 'video/mpeg'],
  ['.mpeg', 'video/mpeg'],
  ['.ogv', 'video/ogg'],
  ['.flv', 'video/x-flv'],
  ['.wmv', 'video/x-ms-wmv']
])

export function contentTypeFor (name) {
  return TYPES.get(extname(String(name)).toLowerCase()) || 'application/octet-stream'
}

/**
 * @param {object} options
 * @param {string} options.token          random path token
 * @param {() => {size:number,name:string}} options.getFileInfo
 * @param {(start:number,end:number) => import('node:stream').Readable} options.openStream  end inclusive
 * @param {number} [options.timeoutMs]    destroy a read that yields no bytes for this long
 * @param {(absoluteOffset:number) => void} [options.onProgress]
 * @param {(absoluteOffset:number) => void} [options.onReadStart]  fires before
 *   the stream waits for its first piece, i.e. as soon as the range is known
 * @param {number} [options.timeoutMs]
 * @param {object} [options.log]
 */
export function createStreamServer ({ token, getFileInfo, openStream, timeoutMs = 60_000, log, onProgress, onReadStart }) {
  let port = 0
  const sockets = new Set()

  const server = http.createServer((req, res) => {
    void handle(req, res)
  })

  server.on('connection', socket => {
    sockets.add(socket)
    socket.on('close', () => sockets.delete(socket))
  })

  async function handle (req, res) {
    res.setHeader('Cache-Control', 'no-store')
    res.setHeader('X-Content-Type-Options', 'nosniff')
    res.setHeader('Accept-Ranges', 'bytes')

    if (!isAllowedHost(req.headers.host, port)) {
      log?.warn('rejected request with unexpected Host header')
      return sendText(res, 403, 'forbidden')
    }

    let pathname
    try { pathname = new URL(req.url, 'http://127.0.0.1').pathname }
    catch { return sendText(res, 400, 'bad request') }

    if (!isAuthorizedPath(pathname, token)) return sendText(res, 404, 'not found')

    if (req.method !== 'GET' && req.method !== 'HEAD') {
      res.setHeader('Allow', 'GET, HEAD')
      return sendText(res, 405, 'method not allowed')
    }

    const info = getFileInfo()
    if (!info || !Number.isSafeInteger(info.size) || info.size < 0) return sendText(res, 503, 'not ready')

    const range = parseRange(req.headers.range, info.size)
    if (range.type === 'invalid') {
      return sendText(res, 400, 'bad range')
    }
    if (range.type === 'unsatisfiable') {
      res.statusCode = 416
      res.setHeader('Content-Range', `bytes */${info.size}`)
      res.setHeader('Content-Length', '0')
      return res.end()
    }

    const start = range.type === 'full' ? 0 : range.start
    const end = range.type === 'full' ? info.size - 1 : range.end
    const length = range.type === 'full' ? info.size : range.length

    if (range.type === 'range') {
      res.statusCode = 206
      res.setHeader('Content-Range', contentRange(start, end, info.size))
    } else {
      res.statusCode = 200
    }
    res.setHeader('Content-Type', contentTypeFor(info.name))
    res.setHeader('Content-Length', String(length))

    if (req.method === 'HEAD' || info.size === 0) return res.end()

    onReadStart?.(start)

    let stream
    try {
      stream = await openStream(start, end)
    } catch (error) {
      log?.error('could not open torrent stream', error)
      if (!res.headersSent) sendText(res, 500, 'stream failed')
      else res.destroy()
      return
    }
    if (!stream || typeof stream.pipe !== 'function') {
      return sendText(res, 500, 'stream failed')
    }

    let written = 0
    let closed = false
    let lastActivity = Date.now()

    const cleanup = () => {
      if (closed) return
      closed = true
      clearInterval(watchdog)
      stream.destroy()
    }

    // A peer that disappears mid-piece must not leave the response open
    // forever: mpv would sit on a frozen frame instead of retrying.
    const watchdog = setInterval(() => {
      if (Date.now() - lastActivity > timeoutMs) {
        log?.warn('stream read stalled; closing connection')
        cleanup()
        res.destroy()
      }
    }, Math.min(5000, Math.max(1000, Math.floor(timeoutMs / 4))))
    watchdog.unref?.()

    res.on('close', cleanup)
    stream.on('data', chunk => {
      lastActivity = Date.now()
      written += chunk.length
      onProgress?.(start + written)
    })
    stream.on('error', error => {
      log?.warn('stream error', error)
      cleanup()
      if (!res.headersSent) sendText(res, 500, 'stream failed')
      else res.destroy()
    })
    stream.on('end', () => clearInterval(watchdog))
    stream.pipe(res)
  }

  return {
    get port () { return port },
    get url () {
      // mpv derives its displayed title from the URL when the container has no
      // title tag, so the name rides along as a decorative last segment. The
      // token still does the authorizing; isAuthorizedPath ignores this part.
      return `http://127.0.0.1:${port}/s/${token}${urlName(getFileInfo())}`
    },
    async listen (host = '127.0.0.1') {
      await new Promise((resolve, reject) => {
        server.once('error', reject)
        server.listen(0, host, () => {
          server.removeListener('error', reject)
          port = server.address().port
          resolve()
        })
      })
      server.unref?.()
      return port
    },
    async close () {
      await new Promise(resolve => {
        try { server.close(() => resolve()) } catch { resolve() }
        server.closeAllConnections?.()
        for (const socket of sockets) {
          try { socket.destroy() } catch { /* already gone */ }
        }
      })
    }
  }
}

/**
 * The decorative file-name segment for the stream URL.
 *
 * Players show this when the file itself carries no title tag, which is how
 * mpv ended up showing the bare token. Only the basename is used, control
 * characters are dropped and the length is capped so a hostile torrent name
 * cannot produce an absurd URL.
 */
function urlName (info) {
  const name = info && typeof info.name === 'string' ? info.name : ''
  const base = name.split(/[\\/]/).pop() || ''
  const cleaned = base.replace(/[\u0000-\u001f\u007f]/g, '').slice(0, 200)
  return cleaned ? '/' + encodeURIComponent(cleaned) : ''
}

function sendText (res, status, text) {
  if (res.headersSent) {
    res.destroy()
    return
  }
  res.statusCode = status
  res.setHeader('Content-Type', 'text/plain; charset=utf-8')
  res.setHeader('Content-Length', Buffer.byteLength(text))
  res.end(text)
}
