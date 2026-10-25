import WebTorrent from 'webtorrent'
import { materializeSource } from '../src/source.js'
import { mkdirSync, rmSync } from 'node:fs'
import { join } from 'node:path'

const trackers = [
  'udp://tracker.opentrackr.org:1337/announce',
  'udp://tracker.torrent.eu.org:451/announce',
  'udp://exodus.desync.com:6969/announce',
  'udp://explodie.org:6969/announce',
  'udp://tracker.dler.org:6969/announce'
]
const dir = join(process.env.TEMP, 'mpvtorrent-probe')
rmSync(dir, { recursive: true, force: true })
mkdirSync(dir, { recursive: true })

const source = await materializeSource(
  'https://cdimage.debian.org/debian-cd/current/amd64/bt-cd/debian-13.7.0-amd64-netinst.iso.torrent',
  { trackers, fetchTorrent: (await import('../src/source.js')).fetchTorrentBuffer }
)
console.log('infohash', source.infoHash, 'trackers in magnet/file', trackers.length)

const client = new WebTorrent({ dht: { bootstrap: [
  'router.bittorrent.com:6881', 'router.utorrent.com:6881',
  'dht.transmissionbt.com:6881', 'dht.aelitis.com:6881'
] }, utp: false, webSeeds: false, natUpnp: false, natPmp: false })
client.on('error', e => console.log('CLIENT ERR', e.message))
client.on('warning', e => console.log('CLIENT WARN', e.message))

const torrent = client.add(source.input, { path: dir, deselect: true, announce: trackers })
console.log('announce list', torrent.announce)
torrent.on('warning', e => console.log('TORRENT WARN', e.message))
torrent.on('wire', w => console.log('WIRE', w.remoteAddress, w.remotePort))
torrent.on('peer', p => console.log('PEER', p))
torrent.on('metadata', () => console.log('META', torrent.files.length))
torrent.on('ready', () => console.log('READY', torrent.numPeers))

setInterval(() => {
  console.log('tick peers', torrent.numPeers, 'wires', torrent.wires.length,
    'dhtNodes', client.dht?.nodes.toArray().length, 'dhtListening', client.dht?.listening,
    'announce', torrent.announce.length)
}, 3000)

setTimeout(() => {
  client.destroy()
  setTimeout(() => process.exit(0), 500)
}, 60_000)
