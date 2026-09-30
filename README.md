# MPVLauncher

> **This project was written with AI assistance.** Every part of it - the WPF
> desktop application, the browser extension, the documentation and the tests -
> was produced with AI coding assistance, and reviewed and corrected by a human
> maintainer. You are welcome to read the code, run it, and change it.

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![Version](https://img.shields.io/badge/version-1.9.3-blue.svg)](extension/manifest.json)

**Other language:** [Türkçe](README_TR.md)

---

## What it is

MPVLauncher is a desktop launcher for [mpv](https://mpv.io/), plus a browser
extension that hands web video straight to your local player.

The app downloads and manages the three tools mpv actually needs (mpv, yt-dlp,
FFmpeg), installs a browser extension on both Chromium and Firefox, and adds
themes and Anime4K upscaling. There is no Python, no external runtime, and
nothing is installed system-wide: everything lives under your own user account.

To play a video from the browser you normally need a download tool, a script, or
a page-specific workaround. This project aims to make that one click.

---

## Features

### Desktop app (WPF, .NET 10, Windows 10/11)

* **One-click tool installation.** Downloads mpv, yt-dlp and FFmpeg from their
  upstream GitHub releases, verifies nothing is left half-installed, and
  updates yt-dlp in place.
* **Browser integration installer.** Registers the native messaging host for
  Chrome, Edge, Brave, Opera, Vivaldi, Yandex and Firefox, makes the extension
  load in a normally launched window by writing per-user overrides of the
  browser launch commands, and repairs a host path after the app folder moves.
* **Extension repair and uninstall.** A complete re-install that rewrites every
  file and registry entry, and an uninstall that removes exactly what was
  installed and nothing else - your Firefox `user.js` and an organisation's
  `policies.json` are left intact.
* **Themes and live appearance editor.** Four shipped themes plus per-surface
  corner radii, independent opacity, frosted-glass blur, shadow controls, colour
  palette and a custom background image.
* **Anime4K and ModernZ.** Installs the Anime4K GLSL shader set and the ModernZ
  on-screen controller into `%APPDATA%\mpv`.
* **Playback history.** The last 50 entries, shared with the browser extension.
* **Custom background caching.** A remote background is fetched once and reused,
  so startup never waits on the network.
* **Interface in 12 languages.**

### Browser extension (Chromium and Firefox, Manifest V3)

* **Two independent detection layers.** A `webRequest` listener sees every media
  request the page makes - including ones no DOM node points at - while a content
  script scans markup, player configuration and lazy-loaded attributes for a
  stream the page has not fetched yet. Results are merged and deduplicated.
* **Handles the usual suspects.** HLS/m3u8 and DASH manifests, direct media
  files, `<video>` / `<source>` markup, `object` and `embed` players, JSON-LD
  and player config objects, blob and MSE URLs, and packed (Dean Edwards) inline
  scripts.
* **Iframe detection.** Cross-origin player frames are detected and listed, so
  embeds from another site can be opened directly.
* **Filter panel.** Toggle streams, direct files and iframes separately, set a
  minimum file size, and keep a blacklist of hosts to hide.
* **Challenge-aware.** A reCAPTCHA or Cloudflare frame is recognised and reported
  as such instead of being shown as an empty page.
* **Honest diagnostics.** The status line breaks the results down by source, so
  "nothing found" is distinguishable from "capture is not running".
* **Forwards request headers.** The `User-Agent` and `Referer` the page actually
  used are passed to mpv. Credentials are never captured - see
  [Privacy and security](#privacy-and-security).
* **Interface in 13 languages.**

---

## Recommended: use the Chromium extension

**The Chromium build is currently the more reliable of the two.** Firefox works,
but its Manifest V3 extension runs as a non-persistent event page that the
browser tears down when idle, which loses in-memory capture state and makes
detection less consistent between page loads. Chromium's service worker
survives longer, so captures accumulate more reliably.

Both are supported and both are shipped from the same source tree; the
difference is a platform behaviour, not a different feature set. If you have a
choice, prefer Chrome, Edge or Brave.

---

## Installation

### The application

1. Download and run the launcher.
2. Open the **Dependencies** tab and click **Download & Install** for mpv,
   yt-dlp and FFmpeg. mpv is enough to get started.
3. Open **Settings → Browser extension integration** and click
   **Install / repair extensions**.

No administrator rights are required. Everything is written under
`%APPDATA%\MPVLauncher\` and `HKEY_CURRENT_USER`.

### Chromium (Chrome, Edge, Brave, Opera, Vivaldi, Yandex)

After running the installer, open `chrome://extensions`, turn on **Developer
mode**, choose **Load unpacked** and pick:

```
%APPDATA%\MPVLauncher\extensions\chromium
```

The **Extension Folder** button in the app opens that folder for you.

> Chrome 137 and newer ignore `--load-extension` unless the
> `DisableLoadExtensionCommandLineSwitch` policy is set. The installer writes
> that policy for your user account automatically.
>
> The extension has to be loaded by the browser on **every** start, and the
> only way Chromium accepts that is a command-line switch. Browsers register
> their command under `HKEY_LOCAL_MACHINE`, which a per-user app cannot edit, so
> the installer writes the same keys under `HKEY_CURRENT_USER` instead - Windows
> merges the two, no administrator rights are needed, and nothing the browser
> does on update can undo it. Uninstall removes exactly those values again and
> leaves the machine registrations untouched.

### Firefox

Install the signed add-on from
[addons.mozilla.org](https://addons.mozilla.org/firefox/addon/mpv-launcher/),
or load `extensions\firefox` as a temporary add-on from `about:debugging` →
**This Firefox** → **Load Temporary Add-on**.

Firefox requires 128 or newer for Manifest V3 support.

---

## Privacy and security

This project handles URLs from pages you visit, so the boundaries are worth
stating plainly.

* **Credentials are never captured.** The extension's `webRequest` layer keeps
  only `User-Agent`, `Referer` and `Origin`, and drops `Cookie` and
  `Authorization`. Nothing that could log you in is stored, forwarded, or
  written to disk. A video behind a login will therefore not play in mpv.
* **Nothing is invented on your behalf.** When mpv is launched, the `Referer` is
  the one the browser actually sent. Earlier versions substituted the media
  host's own origin, which made yt-dlp reject some URLs outright
  (`Unsupported URL`); guessing was removed for that reason.
* **No page script is executed.** Packed player scripts are read by parsing the
  packer's dictionary as text. They are never passed to `eval` or
  `new Function`, which would hand a hostile page the extension's own API.
* **The native host is not a general-purpose launcher.** It only accepts `http`
  and `https`. A `file://` or `\\host\share\...` value would make mpv's HTTP
  client answer an NTLM challenge for a remote server.
* **Arguments are passed as arguments.** Every mpv argument is tokenised and
  quoted for `CommandLineToArgvW` rather than pasted into one string, so a URL
  cannot smuggle in extra mpv options such as `--script`.
* **Downloaded archives are contained.** Archive entries that would be written
  outside the extraction folder, and link entries, are refused before anything
  is written.
* **Downloads are bounded.** Tool downloads are size-capped, and the ModernZ
  theme - which includes a Lua script mpv executes - is restricted to https GitHub
  hosts, re-checked after any redirect.
* **Signed media URLs are not logged.** The native host log keeps the host and
  path of a URL but replaces the query string, which is where the access token
  lives, and the log is rotated.
* **The caller is authenticated by the browser.** Both browsers check their own
  allow-list (`allowed_origins` / `allowed_extensions`) before starting the host.

Local files you type into the app are yours and are launched normally; the
restrictions above apply to values that arrive from a web page.

---

## Languages

**Application (12):** English, Türkçe, Deutsch, Español, Bahasa Indonesia,
日本語, 한국어, Polski, Português (Brasil), Русский, Tiếng Việt, 中文 (简体)

**Extension (13):** the same list, plus Français.

---

## Anime4K shortcuts (inside mpv)

| Key | Action |
| --- | --- |
| `Ctrl+0` | Disable Anime4K |
| `Ctrl+1` | Mode A (Fast) |
| `Ctrl+2` | Mode B (High quality) |
| `Ctrl+3` | Mode C (Very fast) |
| `Ctrl+4` | Mode A+A |
| `Ctrl+5` | Mode B+B |
| `Ctrl+6` | Mode C+A |

---

## Building from source

Requires the .NET 10 SDK and Node.js (for the extension tests only).

```powershell
dotnet build MpvLauncher.Gui\MpvLauncher.Gui.csproj -c Debug
```

The extension files in `extension/` are embedded into the executable, so the
C# project must be rebuilt for a change to reach `%APPDATA%`. Re-running
**Install / repair** in the app copies them to the browser folders.

### Tests

```powershell
node tools\extension-tests.js   # 147 checks - extension logic and security
node tools\locale-tests.js      #  29 checks - locale files stay consistent
```

---

## Repository layout

```
MpvLauncher.Gui/     WPF application (.NET 10)
  Services/          installation, native host, downloads, themes, i18n
extension/           browser extension, shared with both browsers
locales/             12 application language files
themes/              theme definitions
tools/               extension and locale test suites
```

---

## Credits

* [mpv](https://mpv.io/) - the player this project exists to serve.
* [yt-dlp](https://github.com/yt-dlp/yt-dlp) - stream extraction.
* [FFmpeg](https://ffmpeg.org/) - demuxing and conversion.
* [Anime4K](https://github.com/bloc97/Anime4K) - real-time anime upscaling.
* [ModernZ](https://github.com/Samillion/ModernZ) - modern OSC for mpv.
* [SharpCompress](https://github.com/adamhathcock/sharpcompress) - archive
  extraction during tool installation.

The detection approach follows the widely used technique of pairing a
`webRequest` listener with a DOM scan; it was implemented independently for this
project rather than copied from any existing extension.

---

## License

[MIT](LICENSE). See [LICENSE](LICENSE) for the full text.
