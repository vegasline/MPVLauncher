// Original implementation, MIT licensed.

using System;
using System.Collections.Generic;
using System.Linq;

namespace MpvLauncher.Gui.Services
{
    /// <summary>
    /// Bounds and defaults for the torrent feature.
    ///
    /// The helper validates everything again, but the launcher clamps first:
    /// the settings file is user-editable, and an out-of-range value must not
    /// reach a command line where it would be silently clamped somewhere the
    /// user cannot see. Every clamp has a documented default to fall back to.
    /// </summary>
    public static class TorrentSettings
    {
        public const int DefaultForwardWindowMiB = 32;
        public const int MinForwardWindowMiB = 8;
        public const int MaxForwardWindowMiB = 128;

        public const int DefaultMpvBufferMiB = 64;
        public const int MinMpvBufferMiB = 16;
        public const int MaxMpvBufferMiB = 512;

        public const int DefaultUploadKiB = 0;
        public const int MaxUploadKiB = 128;

        public const int DefaultMaxConnections = 45;
        public const int MinMaxConnections = 5;
        public const int MaxMaxConnections = 100;

        public const int DefaultMaxHalfOpen = 8;
        public const int MinMaxHalfOpen = 1;
        public const int MaxMaxHalfOpen = 20;

        public const int DefaultCachePauseWaitSeconds = 7;
        public const int MinCachePauseWaitSeconds = 5;
        public const int MaxCachePauseWaitSeconds = 10;

        /// <summary>
        /// Seconds to wait for the end of the file before playback starts.
        /// 0 means "start as soon as the beginning is ready": on a large or
        /// sparsely seeded torrent the last pieces can take minutes while the
        /// first ones arrive in seconds, so playback must not depend on them.
        /// The tail keeps downloading in the background either way.
        /// </summary>
        public const int DefaultTailWaitSeconds = 0;
        public const int MaxTailWaitSeconds = 30;

        /// <summary>
        /// Trackers that answered an announce from the development machine on
        /// 2026-10-25. The list is editable in settings. Nothing is added here
        /// that was not actually reached: a dead default makes magnet startup
        /// look broken for reasons the user cannot see.
        /// </summary>
        public static readonly string[] DefaultTrackers =
        {
            "udp://tracker.opentrackr.org:1337/announce",
            "udp://tracker.torrent.eu.org:451/announce",
            "udp://exodus.desync.com:6969/announce",
            "udp://explodie.org:6969/announce",
            "udp://tracker.dler.org:6969/announce"
        };

        public static string DefaultTrackerList => string.Join(Environment.NewLine, DefaultTrackers);

        public static int NormalizeUploadKiB(int value) =>
            value <= 0 ? 0 : Math.Min(value, MaxUploadKiB);

        public static int NormalizeForwardWindowMiB(int value) =>
            value < MinForwardWindowMiB ? DefaultForwardWindowMiB
            : value > MaxForwardWindowMiB ? MaxForwardWindowMiB
            : value;

        public static int NormalizeMpvBufferMiB(int value) =>
            value < MinMpvBufferMiB ? DefaultMpvBufferMiB
            : value > MaxMpvBufferMiB ? MaxMpvBufferMiB
            : value;

        public static int NormalizeMaxConnections(int value) =>
            value < MinMaxConnections ? DefaultMaxConnections
            : value > MaxMaxConnections ? MaxMaxConnections
            : value;

        public static int NormalizeHalfOpen(int value, int maxConnections) =>
            Math.Min(
                value < MinMaxHalfOpen ? DefaultMaxHalfOpen
                : value > MaxMaxHalfOpen ? MaxMaxHalfOpen
                : value,
                NormalizeMaxConnections(maxConnections));

        public static int NormalizeCachePauseWait(int value) =>
            value < MinCachePauseWaitSeconds ? DefaultCachePauseWaitSeconds
            : value > MaxCachePauseWaitSeconds ? MaxCachePauseWaitSeconds
            : value;

        public static int NormalizeTailWait(int value) =>
            value < 0 ? DefaultTailWaitSeconds
            : value > MaxTailWaitSeconds ? MaxTailWaitSeconds
            : value;

        /// <summary>
        /// Keeps only lines that look like tracker URLs, drops duplicates and
        /// caps the list. Comments and blank lines are preserved so the user's
        /// annotations are not destroyed by pressing Save.
        /// </summary>
        public static string NormalizeTrackerList(string? text, int limit = 50)
        {
            if (string.IsNullOrWhiteSpace(text)) return DefaultTrackerList;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var lines = new List<string>();
            int accepted = 0;
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                {
                    lines.Add(line);
                    continue;
                }
                if (!IsTrackerUrl(line) || !seen.Add(line)) continue;
                lines.Add(line);
                accepted++;
                if (accepted >= limit) break;
            }

            string result = string.Join(Environment.NewLine, lines).Trim();
            return result.Length == 0 ? DefaultTrackerList : result;
        }

        public static IReadOnlyList<string> TrackerUrls(string? text) =>
            (text ?? "")
                .Split('\n')
                .Select(l => l.Trim())
                .Where(l => l.Length > 0 && !l.StartsWith("#", StringComparison.Ordinal) && IsTrackerUrl(l))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(50)
                .ToList();

        public static bool IsTrackerUrl(string value) =>
            Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
            (uri.Scheme == "udp" || uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
            !string.IsNullOrEmpty(uri.Host);
    }
}
