import DHT from 'bittorrent-dht'

const bootstrap = [
  'router.bittorrent.com:6881',
  'router.utorrent.com:6881',
  'dht.transmissionbt.com:6881',
  'dht.aelitis.com:6881'
]

const dht = new DHT({ bootstrap })
dht.on('ready', () => console.log('READY nodes', dht.nodes.toArray().length))
dht.on('node', () => console.log('node', dht.nodes.toArray().length))
dht.on('warning', e => console.log('WARN', e.message))
dht.on('error', e => console.log('ERR', e.message))
dht.on('listening', () => console.log('LISTENING', dht.address()))

setTimeout(() => {
  console.log('nodes after 15s:', dht.nodes.toArray().length, 'listening', dht.listening)
  try { dht.destroy() } catch {}
  process.exit(0)
}, 15_000)
