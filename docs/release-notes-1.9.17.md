# MPV Launcher 1.9.17

## Browser relay, now with nothing to install by hand

Some sites only give a usable stream address to their own player, inside the
page, with the session of the tab that is already open. The optional browser
relay handles exactly those sites: the extension asks your open tab for the
address it is playing, and a small local helper (`mpvrelay`) serves those bytes
to mpv through `127.0.0.1`.

What changed in this release:

- **No download button.** Turning the relay on, or any later launch with it on,
  installs the helper automatically. The file is verified against its published
  SHA-256 checksum before it is used.
- **Updates itself with the app.** Every launch compares the installed helper
  with the newest `mpvrelay-v*` release and swaps in a newer one, exactly like
  mpv and yt-dlp.
- **Per-site blacklist.** Sites whose players only work inside the viewer's own
  session are never relayed. `youtube.com`, `twitch.tv` and `kick.com` are on
  the list by default, and you can add or remove hosts in
  **Settings → Browser relay**. An entry covers every subdomain of that host.
- **Full explanation in Settings.** The relay card now says what the helper
  does, how it is installed, and what it never touches.
- **12 languages** cover the new settings card.

The relay is off by default. It is only tried when the site actually needs it,
it never reads or stores cookies or tokens, and nothing keeps running after
playback stops. DRM, CAPTCHA and Cloudflare challenges remain out of scope.

Full details: [docs/browser-relay.md](https://github.com/vegasline/MPVLauncher/blob/main/docs/browser-relay.md)
