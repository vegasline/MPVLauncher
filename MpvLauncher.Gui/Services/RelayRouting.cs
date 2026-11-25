// Original implementation, MIT licensed.

using System;
using System.Collections.Generic;
using System.IO;

namespace MpvLauncher.Gui.Services
{
    /// <summary>
    /// Where a link has to go.
    ///
    /// The decision has to be made without touching the network: a token URL may
    /// be single use, so a probe request could burn it before mpv ever sees it.
    /// Everything here therefore looks at the URL text and at what the caller
    /// already knows (the click came from a page, or it was typed by hand).
    /// </summary>
    public enum MediaRoute
    {
        /// <summary>magnet:, .torrent or a local file - existing torrent/file path.</summary>
        Torrent,

        /// <summary>A URL that names a media file. Straight to mpv, exactly as before.</summary>
        Direct,

        /// <summary>
        /// A page that plays a stream behind an opaque, browser-only URL. Only the
        /// relay helper can obtain a usable URL, because it is the only part of the
        /// app that runs a page.
        /// </summary>
        Relay
    }

    /// <summary>
    /// Classifies a link without probing it.
    ///
    /// The distinction that matters is "does this URL name a media file?" A
    /// playlist whose path is opaque (/w/&lt;token&gt;) is a page-level stream, not
    /// a media file, so it goes through the relay even though its body is HLS.
    /// </summary>
    public static class RelayRouting
    {
        /// <summary>Sites whose players only work inside the viewer's own session.</summary>
        public static readonly IReadOnlyList<string> DefaultBlacklist = new[] { "youtube.com", "twitch.tv", "kick.com" };

        private static readonly string[] MediaExtensions =
        {
            ".m3u8", ".m3u", ".mpd", ".mp4", ".m4v", ".mkv", ".webm", ".mov",
            ".avi", ".ts", ".m2ts", ".mp3", ".aac", ".flac", ".ogg", ".opus", ".wav"
        };

        /// <summary>
        /// True when the path ends in something a player opens directly. The query
        /// string is ignored on purpose: a signed .mp4?t=... is still an .mp4.
        /// </summary>
        public static bool HasMediaExtension(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            if (!TryGetPath(url!, out string path)) return false;
            foreach (string extension in MediaExtensions)
            {
                if (path.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>
        /// Route for a URL handed over by the browser extension.
        ///
        /// <paramref name="kind"/> is "resolve" when the extension saw a stream
        /// request but the URL itself cannot be reused (used or expired token).
        /// In that case the page URL is what has to be reopened, so the relay
        /// always wins over the direct path.
        /// </summary>
        public static MediaRoute ForExtension(string? url, string? kind, bool relayEnabled, IReadOnlyCollection<string>? blacklist = null)
        {
            if (string.IsNullOrWhiteSpace(url)) return MediaRoute.Direct;
            if (IsTorrent(url!)) return MediaRoute.Torrent;
            // A blacklisted site is refused here as well: the extension cannot
            // know about a list the viewer edits in Settings, and relaying such a
            // site can only return an error page.
            if (IsBlacklisted(url, blacklist)) return MediaRoute.Direct;

            // Only the extension decides when a link needs the relay, and it says
            // so with "resolve". Anything else - a popup list row, a picked CDN
            // manifest, a page that was already resolved - is a URL mpv can open
            // itself. Falling back to the relay here made every list row start a
            // session that could only end in relay_no_stream.
            return string.Equals(kind, "resolve", StringComparison.OrdinalIgnoreCase) && relayEnabled
                ? MediaRoute.Relay
                : MediaRoute.Direct;
        }

        /// <summary>
        /// Route for a link typed into the Player tab.
        ///
        /// A page URL stays on the existing yt-dlp path first: yt-dlp understands
        /// far more sites than the relay does, and the relay is the fallback for
        /// the ones it cannot open. A host that already failed with 403 and then
        /// worked through the relay is remembered, so it goes to the relay first.
        /// </summary>
        public static MediaRoute ForManual(string? url, bool relayEnabled, bool hostNeedsRelay, IReadOnlyCollection<string>? blacklist = null)
        {
            if (string.IsNullOrWhiteSpace(url)) return MediaRoute.Direct;
            if (IsTorrent(url!)) return MediaRoute.Torrent;
            if (IsBlacklisted(url, blacklist)) return MediaRoute.Direct;
            if (HasMediaExtension(url)) return MediaRoute.Direct;
            if (!relayEnabled) return MediaRoute.Direct;
            return hostNeedsRelay ? MediaRoute.Relay : MediaRoute.Direct;
        }

        /// <summary>True when a manual link is a page the relay could try after yt-dlp failed.</summary>
        public static bool CanFallBackToRelay(string? url, bool relayEnabled, IReadOnlyCollection<string>? blacklist = null)
        {
            if (!relayEnabled || string.IsNullOrWhiteSpace(url)) return false;
            if (IsTorrent(url!)) return false;
            if (IsBlacklisted(url, blacklist)) return false;
            if (HasMediaExtension(url)) return false;
            return IsWebPage(url!);
        }

        /// <summary>
        /// True when the URL's host is on the blacklist. An entry matches the host
        /// itself and every subdomain, so "youtube.com" also covers "www.youtube.com".
        /// </summary>
        public static bool IsBlacklisted(string? url, IReadOnlyCollection<string>? blacklist)
        {
            string host = HostKey(url);
            if (host.Length == 0 || blacklist == null || blacklist.Count == 0) return false;
            foreach (string entry in blacklist)
            {
                string needle = (entry ?? "").Trim().TrimStart('.').ToLowerInvariant();
                if (needle.Length == 0) continue;
                if (host == needle || host.EndsWith("." + needle, StringComparison.Ordinal)) return true;
            }
            return false;
        }
        public static string HostKey(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return "";
            if (Uri.TryCreate(url!.Trim(), UriKind.Absolute, out Uri? parsed))
            {
                return parsed.Host.ToLowerInvariant();
            }
            return "";
        }

        private static bool IsTorrent(string url)
        {
            string trimmed = url.Trim();
            return trimmed.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)
                || trimmed.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase)
                || (!trimmed.Contains("://") && File.Exists(trimmed));
        }

        private static bool IsWebPage(string url)
        {
            return Uri.TryCreate(url.Trim(), UriKind.Absolute, out Uri? parsed)
                && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps);
        }

        private static bool TryGetPath(string url, out string path)
        {
            path = "";
            string trimmed = url.Trim();
            if (!Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? parsed)) return false;
            if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) return false;
            path = parsed.AbsolutePath;
            return path.Length > 0;
        }
    }
}
