// Diagnostic only: not part of the build.
// Checks whether this machine can open outgoing TCP connections to real
// BitTorrent peers, independent of the streaming helper.
import net from 'node:net'
import parseTorrent from 'parse-torrent'
import Client from 'bittorrent-tracker'

const url = 'https://cdimage.debian.org/debian-cd/current/amd64/bt-cd/debian-13.7.0-amd64-netinst.iso.torrent'
const res = await fetch(url)
const torrent = await parseTorrent(Buffer.from(await res.arrayBuffer()))
console.log('infohash', torrent.infoHash, 'announce', torrent.announce)

const client = new Client({
  infoHash: torrent.infoHash,
  announce: [
    ...torrent.announce,
    'udp://tracker.opentrackr.org:1337/announce',
    'udp://tracker.torrent.eu.org:451/announce',
    'udp://explodie.org:6969/announce'
  ],
  peerId: Buffer.from('-MP0001-' + '012345678901'),
  port: 6881
})

const peers = new Map()
client.on('peer', peer => {
  const addr = toAddr(peer)
  if (!addr) return
  const key = `${addr.host}:${addr.port}`
  if (!peers.has(key)) {
    peers.set(key, addr)
    console.log('peer', key)
  }
})
client.on('warning', err => console.log('warning', err.message))
client.on('error', err => console.log('error', err.message))
client.start()

await new Promise(resolve => setTimeout(resolve, 12000))
client.stop()

const targets = [...peers.values()].slice(0, 5)
console.log('testing', targets.length, 'tcp connects')
for (const peer of targets) {
  const host = peer.host
  const started = Date.now()
  await new Promise(resolve => {
    const socket = net.connect({ host, port: peer.port })
    const done = (label, detail = '') => {
      console.log(`${host}:${peer.port} ${label} ${Date.now() - started}ms ${detail}`)
      socket.destroy()
      resolve()
    }
    socket.setTimeout(6000)
    socket.once('connect', () => done('CONNECTED'))
    socket.once('timeout', () => done('TIMEOUT'))
    socket.once('error', err => done('ERROR', err.code || err.message))
  })
}
process.exit(0)

function toAddr (peer) {
  if (typeof peer === 'string') {
    const i = peer.lastIndexOf(':')
    if (i <= 0) return null
    return { host: peer.slice(0, i), port: Number(peer.slice(i + 1)) }
  }
  if (peer && (peer.host || peer.ip) && peer.port) {
    return { host: peer.host || peer.ip, port: Number(peer.port) }
  }
  return null
}
