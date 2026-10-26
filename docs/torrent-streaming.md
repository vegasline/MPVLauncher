# Torrent streaming — manual end-to-end check

Original implementation, MIT licensed.

The automated suites cover source parsing, the feature gate, argument building,
Range handling and the helper's window maths. This document is the part they
cannot cover: real mpv, a real swarm and real cleanup. It is written to be
repeated before a release, on a Windows machine with mpv installed and working
internet access.

Use a legal, well-seeded torrent. The examples below use the Debian network
installer, which is published for exactly this purpose:

```
https://cdimage.debian.org/debian-cd/current/amd64/bt-cd/debian-13.7.0-amd64-netinst.iso.torrent
```

## Prerequisites

- Windows 10/11, .NET 10 SDK, Node.js 24 (for building the helper).
- A working mpv installation, discoverable by the launcher.
- `tools/mpvtorrent` built once with `npm ci && npm test && npm run build`.
  The release build produces `dist/mpvtorrent.exe` and its `.sha256`.
- A clean `%APPDATA%\MPVLauncher` or a backup of it.

## A. The feature is off by default

1. Start the launcher with a fresh settings file (or turn
   **Settings → Torrent streaming** off).
2. Paste a magnet link into the URL box and press Play.
   Expected: the torrent card says streaming is off, offers **Open torrent
   settings**, and no `mpvtorrent.exe` appears in Task Manager.
3. Repeat with a `.torrent` URL, a dropped `.torrent` file, and a torrent entry
   in the history list. Expected: the same refusal from every entry point.
4. With the browser extension loaded, send a `.torrent` URL to the native host.
   Expected: the host rejects the message; mpv does not start and no helper
   process is created. This must stay true even after the feature is enabled.

## B. Installing the helper

5. Open **Dependencies → Torrent helper** and press **Download & install**.
   Expected: progress, then `Installed (v…)`. The file exists at
   `%APPDATA%\MPVLauncher\mpvtorrent.exe` and its SHA-256 matches the published
   `.sha256`.
6. Quit the launcher, append one byte to the executable, and start it again.
   Expected: playback is refused with a hash-mismatch/helper error; the altered
   binary is never executed. Reinstalling fixes it.

## C. Playback, seek and cleanup

7. Enable torrent streaming and accept the privacy notice once.
8. Paste the Debian `.torrent` URL above and press Play.
   Expected status sequence: starting → searching → metadata → preload →
   playing. With the end-of-file wait at its default of `0`, mpv opens as soon
   as the beginning of the file is buffered; the last 4 MiB keep downloading in
   the background for seeking near the end. Peers, speed and progress update no
   more than twice per second.
9. mpv opens with the torrent/video name as its title. Seek forward and back.
   Expected: playback resumes after a short refill, no "unseekable" error, and
   the session folder under `%APPDATA%\MPVLauncher\cache\torrent\sessions`
   stays close to the configured forward window instead of growing to the full
   ISO size. Pause mpv (or let the buffer fill) and watch the download counter:
   it must stop, not keep climbing to the size of the file. On a multi-file
   torrent, check that only the selected file (plus piece-boundary spill on its
   neighbours) grows.
10. Close mpv normally. Expected: the helper exits within the grace period
    (about 8 s worst case), the session folder is removed and no
    `mpvtorrent.exe` remains.
11. Repeat, but end mpv from Task Manager. Expected: the launcher notices the
    process exit and performs the same helper shutdown and cleanup.
12. Repeat, then close the launcher while playback is running. Expected: the
    helper is stopped and its session folder removed; no orphan process.
13. Repeat, then end the launcher from Task Manager. Expected: the helper's
    parent-PID watchdog exits by itself within a few seconds and removes its
    session folder. The next launcher start also sweeps stale session folders.

## D. Multi-file torrents

14. Play a multi-file torrent. Expected: a picker lists the files before
    playback; choosing one starts the stream. With **Remember the last played
    file** enabled, the same file is preselected next time. Cancelling the
    picker stops the session instead of leaving it running.

## E. Limits

15. Set upload to `0` and confirm there is no sustained upload beyond protocol
    overhead (choke/unchoke and handshakes are not payload). Set `64` and
    confirm the helper stays at or below the cap.
16. Set the forward window to `8`, then `128`. Expected: accepted and clamped
    values are shown; after restarting the app the advanced values are back at
    their documented defaults.
17. Set the end-of-file wait to `10` and play a multi-file torrent whose last
    pieces are slow. Expected: playback starts within about ten seconds even if
    the end of the file has not arrived; a value of `31` is clamped to `30` and
    a negative value falls back to `0`.

## F. Magnet start and timeouts

18. Play a magnet link for a torrent fetched earlier. Expected: metadata appears
    from the local cache almost instantly. For a cold magnet, a hint appears
    after about 45 s and a clear error after about 120 s without peers; the
    helper is stopped and cleaned up in both cases.

## Where to look when something fails

- `%APPDATA%\MPVLauncher\logs\mpvtorrent.log` — helper log (`--verbose` adds
  per-connection detail).
- Helper stdout is consumed by the launcher; a stuck "starting" state usually
  means the executable was replaced or the contract version does not match.
- `%APPDATA%\MPVLauncher\settings\config.json` — the torrent settings described
  above; removing the `Torrent*` properties returns the feature to its defaults.

## Not covered here

- The development machine used for the automated work had no mpv installation,
  so steps 7–18 were not executed there. They require a machine with mpv.
- Browser-extension behaviour is covered by its own suite; this document only
  checks that its native host rejects torrent sources.
