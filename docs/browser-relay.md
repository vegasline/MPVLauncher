# Browser relay

The browser relay lets MPV Launcher play streams that only a real browser can
fetch. It is optional, off by default, and only used for sites that refuse to
hand a usable address to anything except their own player.

## Why it exists

Some sites build the stream address inside a script that runs in the page, or
answer only requests that carry the session of the tab that is already playing.
A player that reads the page from outside gets `403`, an error page, or a
playlist that points at a one-time token the site already burned.

The relay solves this without touching the site's protections:

1. The extension asks the tab that is already open for the address of the
   stream it is playing.
2. The tab answers with the address and with the request headers it uses.
3. A small local program (`mpvrelay`) serves those bytes to mpv through
   `http://127.0.0.1`. mpv therefore sees a normal local playlist instead of a
   protected remote one.
4. When playback stops, the local server stops with it.

Nothing is decrypted, bypassed or stored. The relay does not solve DRM, does
not answer CAPTCHAs, and does not work on sites whose player refuses to expose
its stream at all.

## How it is installed

There is nothing to download by hand.

- Turning the relay on in **Settings → Browser relay**, or any later launch
  with the relay enabled, downloads `mpvrelay.exe` from this repository's
  releases.
- The download is checked against its published SHA-256 checksum before it is
  used. A file that fails the check is deleted and never executed.
- The helper lands in `%APPDATA%\MPVLauncher\bin\mpvrelay.exe`, next to the
  other tools the launcher manages.
- Every launch compares the installed helper with the newest `mpvrelay-v*`
  release. When a newer one exists it is downloaded and swapped in, the same
  way mpv and yt-dlp are updated. There is no separate updater and no button to
  press.

## When it is used

The relay is never the first choice for a link that plays directly.

- A link from the extension popup is handed to mpv as-is unless the extension
  explicitly says the page needs a browser fetch.
- A link you type or paste goes to mpv first. The relay is tried only after mpv
  fails to open it, and only if the site is already known to need it.
- Sites that needed the relay before are remembered by host and asked for it
  first next time. **Forget learned sites** clears that list.

## Sites that are never relayed

Some players only work inside the viewer's own session, so a relayed fetch
returns an error page. Those hosts are kept on a blacklist that is checked
before any relay decision:

- Default entries: `youtube.com`, `twitch.tv`, `kick.com`.
- An entry covers the host and every subdomain of it.
- Entries can be added or removed in **Settings → Browser relay**, one host per
  entry. A pasted address is reduced to its host, so
  `https://www.youtube.com/watch?v=...` and `youtube.com` are the same entry.

## Privacy

- The relay reads only the tab you pressed it on, using that tab's own session.
- Cookies, authorization headers and tokens are never read, stored, logged or
  written to disk. They stay inside the browser and inside the local proxy
  request that mpv triggers.
- Only `http` and `https` addresses are ever handed to mpv; every argument is
  quoted; nothing from a token appears in the log.
- The helper listens on `127.0.0.1` only, for as long as a relayed stream is
  playing.

## Out of scope

- DRM-protected streams.
- CAPTCHA and Cloudflare challenge pages.
- Sites whose player keeps the stream inside Media Source Extensions and never
  exposes a fetchable address.
