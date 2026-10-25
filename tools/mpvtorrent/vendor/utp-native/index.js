// Original implementation, MIT licensed.
//
// Replaces utp-native so the build contains no native addons. WebTorrent
// detects uTP support with `Object.keys(utp).length > 0`, so an empty export
// makes it fall back to TCP, which is what mpvtorrent wants. Nothing here is
// ever called: `client.utp` is false, so no uTP connection is attempted.

'use strict'

module.exports = {}
