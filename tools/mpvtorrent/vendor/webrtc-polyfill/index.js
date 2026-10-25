// Original implementation, MIT licensed.
//
// This package replaces `webrtc-polyfill` (and through it the MPL-2.0
// `node-datachannel` native module) so that no copyleft code is installed or
// shipped. WebRTC is not part of mpvtorrent's feature set: peers are reached
// over TCP and uTP, peers are discovered over DHT, trackers and PEX.
//
// @thaunknown/simple-peer only reads these bindings at module load to set
// `Peer.WEBRTC_SUPPORT`, and throws ERR_WEBRTC_SUPPORT if a WebRTC peer is
// actually constructed. Exports are therefore intentionally undefined rather
// than throwing at import time, which would take the whole torrent client down.

export const RTCPeerConnection = undefined
export const RTCSessionDescription = undefined
export const RTCIceCandidate = undefined

export default { RTCPeerConnection, RTCSessionDescription, RTCIceCandidate }
