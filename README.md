<h1 align="center">MPVLauncher</h1>

<p align="center">Play any video from your browser in <a href="https://mpv.io/"><b>mpv</b></a> â€” with one click.</p>

<p align="center">
<a href="LICENSE"><img src="https://img.shields.io/badge/License-MIT-yellow.svg" alt="MIT License"></a>
<a href="https://github.com/vegasline/MPVLauncher/releases/latest"><img src="https://img.shields.io/badge/app-1.9.6-blue.svg" alt="App version 1.9.4"></a>
<a href="https://addons.mozilla.org/firefox/addon/mpv-launcher/"><img src="https://img.shields.io/badge/Firefox_Add--on-FF7139FF?logo=firefox-browser&logoColor=white" alt="Firefox add-on"></a>
</p>

<p align="center">ðŸŒ English Â· <a href="README_TR.md">TÃ¼rkÃ§e</a></p>

> ðŸ¤– This project was written with AI assistance â€” the app, the extension, the
> docs and the tests â€” and reviewed by a human maintainer. The code is open to
> read, run and change.

---

## ðŸ“¸ What it looks like

| ðŸ–¥ï¸ Desktop app | ðŸ§© Browser extension |
| :---: | :---: |
| ![MPVLauncher main window](MPVLauncher.png) | ![MPVLauncher extension popup](Extension.png) |

---

## ðŸŽ¬ What it does

MPVLauncher is a small launcher for [mpv](https://mpv.io/) plus a browser
extension that sends web video straight to your player.

Normally playing a video from a browser means a download tool, a script, or a
site-specific workaround. This makes it one click.

- ðŸ“¥ **Installs what mpv needs** â€” mpv, yt-dlp and FFmpeg, from their upstream
  releases. No Python, no separate runtime, no system-wide install.
- ðŸ”Œ **Installs the extension** for Chrome, Edge, Brave, Opera, Vivaldi, Yandex
  and Firefox, and repairs it if the app folder moves.
- ðŸŽ¨ **Themes + appearance editor** â€” 4 themes, custom colours, blur, corner
  radii and background image.
- âœ¨ **Anime4K and ModernZ** â€” anime upscaling and a nicer on-screen controller.
- ðŸ—‚ï¸ **Playback history** â€” your last 50 entries, shared with the extension.
- ðŸŒ **12 languages** in the app, **13** in the extension.

### ðŸ” What the extension finds

- **Two detection layers.** A network listener catches every media request the
  page makes; a content script scans the markup for streams that have not loaded
  yet. Results are merged, so you don't miss the one that's still behind a
  lazy-loaded player.
- **The usual suspects** â€” HLS/m3u8, DASH, direct files, `<video>` and
  `<source>`, `object`/`embed` players, JSON-LD, blob and MSE URLs.
- **Iframes** â€” cross-origin embedded players are listed, so a YouTube embed
  on someone else's page works too.
- **Filters** â€” toggle streams, files and iframes, set a minimum size, hide
  hosts you don't care about.
- **Clearer errors** â€” a captcha or a Cloudflare check is reported as such
  instead of as an empty page.

---

## ðŸš€ Quick start

1. Download **MPVLauncher.exe** from the
   [latest release](https://github.com/vegasline/MPVLauncher/releases/latest)
   and open it.
2. Go to the **Dependencies** tab and press **Download & Install** for **mpv**,
   **yt-dlp** and **FFmpeg** â€” three separate buttons, install all three. mpv is
   the player; the other two are what let it open a stream URL. On the same tab,
   **Install Theme+Anime4K** adds the ModernZ interface and the Anime4K shader
   set into `%APPDATA%\mpv`.
3. Still on **Dependencies**, press **Install / repair extensions** to set up the
   browser extension. The same tab has a **ðŸ¦Š Firefox Add-on** button that opens
   the add-on page, and an **Extension Folder** button that opens the folder
   Chromium needs.

**No administrator rights needed.** Everything is written under
`%APPDATA%\MPVLauncher\` and `HKEY_CURRENT_USER`.

> âš ï¸ **Moved `MPVLauncher.exe` to a different folder?** Just open the app once.
> It repairs the native messaging host path by itself on startup, so nothing
> needs reinstalling. **Restart your browser afterwards, though** â€” the browser
> reads that path when it launches the host, so one that was already open keeps
> using the old path and the button will look like it does nothing. This is the
> usual reason for "it worked yesterday".

> ðŸ’¡ Button names follow the app's language setting, so they appear translated
> if you switch the interface to Turkish or one of the other 11 languages.

---

## ðŸŽ® Using it

Two ways in, and both end up in the same mpv.

**ðŸ§© From the browser** â€” click the extension icon on any page. Everything it
found is listed with an **Open in MPV** button: the real stream, direct media
files, and embedded players. Pick one and it opens.

**ðŸ–¥ï¸ From the app** â€” open the **Player** tab, paste a video URL and press
**Play with MPV**. You can also drop a file onto the window, or use
**Select File**.

Either way, the last 50 plays stay in the **Player** tab's history, shared
between both routes.

---

### ðŸ¦Š Firefox

Install the signed add-on from
[addons.mozilla.org](https://addons.mozilla.org/firefox/addon/mpv-launcher/) â€”
it is the easiest path and works out of the box. Firefox 128+ is required for
Manifest V3.

### ðŸŒ Chrome, Edge, Brave, Opera, Vivaldi, Yandex

Run the installer, then open `chrome://extensions`, enable **Developer mode**,
choose **Load unpacked** and pick:

```
%APPDATA%\MPVLauncher\extensions\chromium
```

The **Extension folder** button in the app opens that folder for you.

> ðŸ’¡ Chromium requires an unpacked extension, so it has to be loaded on every
> start. The installer handles this by writing per-user overrides of the browser
> launch command â€” no administrator rights, and nothing the browser does on
> update can undo it. Uninstall removes exactly those values again.

---

## ðŸ”’ Privacy

The extension reads URLs from pages you visit, so the boundaries are worth
stating plainly:

- ðŸ”‘ **No credentials are ever captured.** `Cookie` and `Authorization` are
  dropped before anything else happens. A video behind a login therefore will
  not play in mpv â€” that is the trade, and it is deliberate.
- ðŸš« **No page script is executed.** Packed player scripts are parsed as text,
  never handed to `eval` or `new Function`.
- ðŸ”’ **Only `http` and `https`** reach the player, and every argument is quoted
  properly, so a URL cannot smuggle in extra mpv options.
- ðŸ“¦ **Archive extraction is contained**, downloads are size-capped, and signed
  media URLs keep their access token out of the log.

Full details in [`MpvLauncher.Gui/Services/NativeHostService.cs`](MpvLauncher.Gui/Services/NativeHostService.cs).

---

## âŒ¨ï¸ Anime4K shortcuts (inside mpv)

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

## ðŸ› ï¸ Building from source

Needs the .NET 10 SDK. Node.js only for the extension tests.

```powershell
dotnet build MpvLauncher.Gui\MpvLauncher.Gui.csproj -c Debug
```

The extension is embedded in the executable, so the C# project must be rebuilt
before a change reaches `%APPDATA%`. Then re-run
**Install / repair extensions** in the app.

```powershell
node tools\extension-tests.js   # 147 checks - extension logic and security
node tools\locale-tests.js      #  29 checks - locale files stay consistent
```

---

## ðŸŒ Languages

**App (12):** English, TÃ¼rkÃ§e, Deutsch, EspaÃ±ol, Bahasa Indonesia, æ—¥æœ¬èªž,
í•œêµ­ì–´, Polski, PortuguÃªs (Brasil), Ð ÑƒÑÑÐºÐ¸Ð¹, Tiáº¿ng Viá»‡t, ä¸­æ–‡ (ç®€ä½“)

**Extension (13):** the same list, plus FranÃ§ais.

---

## ðŸ™ Credits

- [mpv](https://mpv.io/) â€” the player this project exists to serve
- [yt-dlp](https://github.com/yt-dlp/yt-dlp) â€” stream extraction
- [FFmpeg](https://ffmpeg.org/) â€” demuxing and conversion
- [Anime4K](https://github.com/bloc97/Anime4K) â€” real-time anime upscaling
- [ModernZ](https://github.com/Samillion/ModernZ) â€” modern OSC for mpv
- [SharpCompress](https://github.com/adamhathcock/sharpcompress) â€” archive
  extraction

## ðŸ“„ License

[MIT](LICENSE)
