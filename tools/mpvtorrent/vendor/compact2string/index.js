// Original implementation, MIT licensed.
//
// Stand-in for compact2string@1.4.1, whose package.json declares the bare
// string "BSD" with no clause and no licence file. Rather than ship a package
// whose licence cannot be quoted exactly, this module implements the same
// documented format (BEP 23): 6 bytes per IPv4 peer, 18 bytes per IPv6 peer,
// big-endian port last. Written from the specification, not from the original
// source.

'use strict'

const net = require('node:net')

function one (buf) {
  if (buf.length === 6) {
    return `${buf[0]}.${buf[1]}.${buf[2]}.${buf[3]}:${buf.readUInt16BE(4)}`
  }
  if (buf.length === 18) {
    return `[${buf.subarray(0, 16).toString('hex').replace(/(.{4})/g, '$1:').replace(/:$/, '')}]:${buf.readUInt16BE(16)}`
  }
  throw new Error('Invalid compact IP/port: expected 6 or 18 bytes')
}

function compact2string (buf) {
  if (!Buffer.isBuffer(buf)) throw new TypeError('compact2string expects a Buffer')
  return one(buf)
}

compact2string.multi = function multi (buf) {
  if (!Buffer.isBuffer(buf)) throw new TypeError('compact2string expects a Buffer')
  if (buf.length % 6 !== 0) throw new Error("buffer length isn't a multiple of 6")
  const out = []
  for (let i = 0; i < buf.length; i += 6) out.push(one(buf.subarray(i, i + 6)))
  return out
}

// BEP 23 IPv6 form: 18 bytes per peer (16 address bytes + 2 port bytes).
// bittorrent-tracker's HTTP tracker calls this for the peers6 field.
compact2string.multi6 = function multi6 (buf) {
  if (!Buffer.isBuffer(buf)) throw new TypeError('compact2string expects a Buffer')
  if (buf.length % 18 !== 0) throw new Error("buffer length isn't a multiple of 18")
  const out = []
  for (let i = 0; i < buf.length; i += 18) out.push(one(buf.subarray(i, i + 18)))
  return out
}

// Kept because some callers pass an IP string rather than a buffer.
compact2string.isIP = net.isIP

module.exports = compact2string
