// Original implementation, MIT licensed.

using System;
using System.IO;
using System.Text.RegularExpressions;

namespace MpvLauncher.Gui.Services
{
    /// <summary>How a torrent source was written.</summary>
    public enum TorrentSourceKind
    {
        Magnet,
        TorrentUrl,
        TorrentFile,
        InfoHash
    }

    /// <summary>A validated torrent source.</summary>
    public sealed record TorrentSource(TorrentSourceKind Kind, string Value, string InfoHash);

    /// <summary>
    /// Recognises the four torrent source shapes the launcher accepts.
    ///
    /// This is a gate, not a downloader: it decides whether a value is a
    /// torrent at all, and extracts the info hash when the value contains one.
    /// It never touches the network or the disk, so it is safe to call from the
    /// URL box, drag/drop and the native-messaging host before anything runs.
    ///
    /// The rules are deliberately narrow. A plain https URL is only a torrent
    /// URL when its path ends in .torrent, because otherwise every web video
    /// would be routed to the torrent helper.
    /// </summary>
    public static class TorrentSourceParser
    {
        private static readonly Regex MagnetHash = new(
            @"[?&]xt=urn:btih:([0-9a-fA-F]{40}|[A-Za-z2-7]{32})",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex HexHash = new(
            "^[0-9a-fA-F]{40}$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex Base32Hash = new(
            "^[A-Za-z2-7]{32}$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>True when the value would be handled by the torrent path.</summary>
        public static bool IsTorrentLike(string? value) => TryParse(value, out _, out _);

        /// <summary>
        /// Validates a source and extracts what can be known without metadata.
        /// For a magnet the info hash is parsed locally; for a .torrent URL or
        /// path it is unknown until the helper reads the file.
        /// </summary>
        public static bool TryParse(string? value, out TorrentSource source, out string error)
        {
            source = null!;
            error = "";
            string text = (value ?? "").Trim();
            if (text.Length == 0) return false;

            if (text.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase))
            {
                var match = MagnetHash.Match(text);
                if (!match.Success)
                {
                    error = "magnet_missing_hash";
                    return false;
                }
                string hash = match.Groups[1].Value.ToLowerInvariant();
                source = new TorrentSource(TorrentSourceKind.Magnet, text, hash);
                return true;
            }

            if (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) ||
                    (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
                    string.IsNullOrEmpty(uri.Host))
                {
                    error = "invalid_url";
                    return false;
                }
                if (!uri.AbsolutePath.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase))
                    return false;

                source = new TorrentSource(TorrentSourceKind.TorrentUrl, uri.AbsoluteUri, "");
                return true;
            }

            // Any other scheme (file:, ftp:, ...) is not a torrent source. It
            // would otherwise look like a local path because it happens to end
            // in .torrent and contains a slash.
            if (text.Contains("://", StringComparison.Ordinal))
            {
                error = "unsupported_source";
                return false;
            }

            if (HexHash.IsMatch(text) || Base32Hash.IsMatch(text))
            {
                source = new TorrentSource(TorrentSourceKind.InfoHash, text.ToLowerInvariant(), text.ToLowerInvariant());
                return true;
            }

            if (text.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase) &&
                (Path.IsPathRooted(text) || text.IndexOfAny(new[] { '/', '\\' }) >= 0))
            {
                source = new TorrentSource(TorrentSourceKind.TorrentFile, text, "");
                return true;
            }

            error = "unsupported_source";
            return false;
        }

        /// <summary>
        /// A log-safe description: the info hash when known, the file name for a
        /// local torrent, and only the host for a URL. A magnet can carry a
        /// private passkey and a URL can carry a signed token, so neither is
        /// ever written to a log in full.
        /// </summary>
        public static string DescribeForLog(TorrentSource source) => source.Kind switch
        {
            TorrentSourceKind.Magnet => $"magnet ({ShortHash(source.InfoHash)})",
            TorrentSourceKind.InfoHash => $"infohash ({ShortHash(source.InfoHash)})",
            TorrentSourceKind.TorrentFile => "torrent file (" + Path.GetFileName(source.Value) + ")",
            TorrentSourceKind.TorrentUrl => Uri.TryCreate(source.Value, UriKind.Absolute, out var uri)
                ? $"torrent url ({uri.Host})"
                : "torrent url",
            _ => "torrent"
        };

        private static string ShortHash(string hash) =>
            string.IsNullOrEmpty(hash) ? "unknown" : hash[..Math.Min(12, hash.Length)];
    }
}
