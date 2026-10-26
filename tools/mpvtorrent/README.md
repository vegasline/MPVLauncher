# mpvtorrent

Headless torrent-to-HTTP streamer used by MPVLauncher. It downloads only the
parts of one selected video that the player is about to read, serves them on a
loopback HTTP endpoint, and exits when the launcher or mpv goes away.

This program is original code, MIT licensed. It is built on the MIT-licensed
[`webtorrent`](https://www.npmjs.com/package/webtorrent) library; no code from
copyleft torrent projects was used.

## What it does not do

- No seeding. Upload is off by default, capped at 128 KiB/s when enabled, and
  the process exits with the session.
- No "download the whole torrent". Every file starts deselected; only the
  selected video's current window is selected.
- No WebRTC, no uTP, no native modules. TCP, UDP trackers, DHT, PEX and LSD
  are enough for playback, and dropping the native pieces keeps the binary
  self-contained.
- No magnet protocol registration, no mpv script or hook, no background
  service.

## Build

Requires Node.js 24 and npm. Produces one Windows executable with the Node
runtime embedded (Node SEA via esbuild + postject):

```sh
npm ci
npm test
npm run build
```

Output: `dist/mpvtorrent.exe`, about 91 MiB, plus `dist/mpvtorrent.exe.sha256`.
The executable is published by the `mpvtorrent-release` GitHub workflow and is
never committed to the repository.

Why SEA: it is the only option that needs no third-party runtime, supports the
current WebTorrent release and produces a single file. Bun and Deno are not
installed in the build environment and were not validated with WebTorrent, so
they are not used. SEA's cost is size (the whole Node runtime) and a ~0.6 s
cold start; the helper is started once per playback, so that is acceptable.

## Launcher contract (version 2)

The launcher starts the executable with `ArgumentList` (no shell quoting) and
talks to it with one JSON object per line: events on stdout, commands on stdin.
stdout carries nothing else.

### Arguments

| Argument | Meaning |
| --- | --- |
| `--source <value>` | magnet, http(s) `.torrent` URL, local `.torrent` path or 40-hex / 32-base32 info hash |
| `--download-dir <dir>` | absolute session folder; deleted on exit |
| `--cache-dir <dir>` | DHT nodes and metadata cache |
| `--launcher-pid <pid>` | helper exits when this process disappears |
| `--window-mib <n>` | forward window, default 32, clamped to 4..128 |
| `--upload-limit-kib <n>` | default 0 (off), clamped to 0..128 |
| `--max-conns <n>` | default 45, clamped to 5..200 |
| `--max-half-open <n>` | default 8, clamped to 1..50 and never above `--max-conns` |
| `--tail-wait-sec <n>` | wait for the file tail before `ready`, default 0, clamped to 0..30 |
| `--tracker-file <path>` | extra trackers, one URL per line, `#` comments allowed |
| `--select-file <index>` | preselect a file when metadata arrives |
| `--read-timeout-ms <n>` | destroy an HTTP read that stalls, default 60000 |
| `--contract <n>` | must be 2 |
| `--log-file <path>` | diagnostic log |
| `--verbose` | also log to stderr |

### Events (stdout)

Every event has `type`. Events are emitted at most twice per second.

```json
{"type":"hello","contract":1,"version":"1.0.0"}
{"type":"source","kind":"magnet","infoHash":"..."}
{"type":"state","phase":"metadata|preload|playing","peers":12,"downloadSpeed":345678,
 "uploadSpeed":0,"progress":0.12,"received":12345678,"length":734003200,
 "fileIndex":0,"window":{"startPiece":0,"endPiece":255}}
{"type":"metadata","infoHash":"...","name":"...","totalLength":123,"pieceLength":262144,
 "files":[{"index":0,"name":"dir/video.mkv","displayName":"video.mkv","length":123}]}
{"type":"selected","fileIndex":0,"fileName":"dir/video.mkv","displayName":"video.mkv","size":123}
{"type":"ready","url":"http://127.0.0.1:54321/s/<token>/<url-encoded file name>","fileIndex":0,
 "fileName":"dir/video.mkv","displayName":"video.mkv","size":123,"pieceLength":262144}
{"type":"hint","code":"still_searching","message":"..."}
{"type":"error","code":"metadata_timeout","message":"...","fatal":true}
{"type":"closed","reason":"command"}
```

Error codes include `metadata_timeout`, `no_data`, `preload_timeout`,
`bad_magnet`, `bad_torrent_file`, `torrent_fetch_failed`,
`torrent_fetch_timeout`, `torrent_file_too_large`, `too_many_files`,
`file_not_found`, `contract_mismatch`, `internal`.

### Commands (stdin)

```json
{"cmd":"select","fileIndex":2}
{"cmd":"close"}
```

Closing stdin, a dead launcher PID or a signal triggers the same clean exit:
the HTTP server closes, the client is destroyed and the session folder is
deleted.

## HTTP endpoint

`GET`/`HEAD` on `/s/<random token>` with a `Range` header. The token may be
followed by one decorative segment holding the file name, which players use as a
fallback title; it is ignored for authorization. Replies are `200`, `206` with
`Content-Range`, or `416`. The server binds `127.0.0.1` on a random port,
requires `Host` to be a loopback literal with that port (DNS-rebinding
protection), and is open only for the session.

## Piece selection

Metadata is followed by a head preload (4 MiB, for MP4 indexes and MKV
cues). `ready` is emitted as soon as the **head** is complete; the tail (4 MiB)
is a seek convenience that is only requested once playback starts, and it keeps
downloading in the background at a lower priority, because on a large or
sparsely seeded torrent the last pieces can take minutes while the first ones
arrive in seconds. Waiting for it before playback would make a single slow peer
split its bandwidth between two ranges. `--tail-wait-sec` adds an optional
bounded wait (0..30 s, default 0) for the tail before `ready`; the tail is
requested when that wait starts. At playback the window is:

- 32 MiB forward by default, aligned to pieces, at least 4 pieces, at most
  128 MiB;
- 6 MiB backward;
- the first 8 MiB (or two pieces) ahead of the read head are marked critical;
- everything outside the window is deselected.

A jump of 4 MiB or more moves the window **before** the HTTP response starts
waiting for its first piece. The stream's own piece iterator selects at the
same priority as the window, so without that step a seek made the client keep
downloading the old window while the new position waited for pieces nobody had
asked for - the symptom being hundreds of megabytes downloaded at full speed
and a video that still did not start.

Only pieces inside the selected file are ever requested: the torrent is added
with `deselect: true`, so no other file in a multi-file torrent is downloaded.
Pieces that straddle a file boundary are shared with the neighbouring file, so a
multi-file torrent can show a few megabytes on the files next to the selected
one; their own pieces are never requested.

The HTTP layer never hands an open-ended range to the torrent: mpv asks for
`bytes=N-`, and WebTorrent's file iterator selects every piece of the range it is
given, so passing the rest of the file would select the rest of the file and the
download would run to the end at full speed regardless of playback. Reads are
therefore issued in 16 MiB segments, one at a time, so selection can only ever
reach one segment past the read head. Because the generator is pulled by the
response, a player that stops reading also stops the download.

The planner is a pure function in `src/window.js` and is unit tested; sliding
is driven by the byte offset the HTTP layer has served.

## Licence

MIT. See `LICENSE`. `THIRD_PARTY_NOTICES.md` contains the licence text of every
dependency, including the Node.js runtime embedded in the executable.
