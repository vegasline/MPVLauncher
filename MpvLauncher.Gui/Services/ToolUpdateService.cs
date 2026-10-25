// Original implementation, MIT licensed.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MpvLauncher.Gui.Services
{
    public enum ToolUpdateState
    {
        UpToDate,
        Available,
        NotInstalled,
        Failed
    }

    /// <summary>
    /// One row of the update check: what is installed, what upstream publishes,
    /// and whether those two differ.
    ///
    /// Installed and Latest are display strings, not parsed values: they carry
    /// the tool's own version text, which is the only thing the user can compare
    /// against upstream. State is the decision the UI colours.
    /// </summary>
    public sealed record ToolUpdateInfo
    {
        public ToolKind Kind { get; init; }
        public string Name { get; init; } = "";
        public string Installed { get; init; } = "";
        public string Latest { get; init; } = "";
        public ToolUpdateState State { get; init; }
        public string Detail { get; init; } = "";
    }

    /// <summary>
    /// Compares the three external tools against their upstream releases.
    ///
    /// Two kinds of comparison are needed because the tools version themselves
    /// differently. yt-dlp publishes dated versions on every release, so the tag
    /// and the installed version are directly comparable. mpv and FFmpeg publish
    /// continuous daily builds whose binaries carry no date in their version
    /// string, so the only honest comparison is the build's file timestamp
    /// against the release date - and even then a build from last week is not
    /// worth replacing, hence the staleness window.
    ///
    /// Nothing here installs or downloads beyond reading the release metadata;
    /// the installer is the same one the per-tool buttons use.
    /// </summary>
    public class ToolUpdateService
    {
        /// <summary>
        /// How old an installed mpv/FFmpeg build has to be before a newer daily
        /// build is reported. Both projects publish continuously, so without a
        /// window every check would announce an update one day after every
        /// install.
        /// </summary>
        public const int StaleBuildDays = 30;

        private readonly ProcessService _processService;
        private readonly DependencyService _dependencies;
        private readonly DownloadInstallService _downloads;

        public ToolUpdateService(
            ProcessService processService,
            DependencyService dependencies,
            DownloadInstallService downloads)
        {
            _processService = processService;
            _dependencies = dependencies;
            _downloads = downloads;
        }

        /// <summary>
        /// Checks all three tools concurrently and never throws: a tool whose
        /// release page cannot be read comes back as Failed with the reason, so
        /// one network problem does not hide the other two results.
        /// </summary>
        public async Task<IReadOnlyList<ToolUpdateInfo>> CheckAllAsync(CancellationToken ct = default)
        {
            var mpv = CheckMpvAsync(ct);
            var ytdlp = CheckYtdlpAsync(ct);
            var ffmpeg = CheckFfmpegAsync(ct);

            return await Task.WhenAll(mpv, ytdlp, ffmpeg);
        }

        private async Task<ToolUpdateInfo> CheckYtdlpAsync(CancellationToken ct)
        {
            try
            {
                var release = await _downloads.GetLatestReleaseAsync(
                    DownloadInstallService.YtDlpRepo, DownloadInstallService.IsYtDlpAsset, ct);

                string path = _dependencies.FindYtdlPath();
                if (!File.Exists(path))
                {
                    return new ToolUpdateInfo
                    {
                        Kind = ToolKind.YtDlp,
                        Name = "yt-dlp",
                        Installed = "",
                        Latest = release.Tag,
                        State = ToolUpdateState.NotInstalled
                    };
                }

                string installed = _dependencies.CheckYtdlStatus();

                ToolUpdateState state;
                if (TryParseVersion(installed, out var have) && TryParseVersion(release.Tag, out var want))
                {
                    state = want > have ? ToolUpdateState.Available : ToolUpdateState.UpToDate;
                }
                else
                {
                    // Unparseable version text is still useful to show, but it
                    // cannot support an "up to date" claim.
                    state = string.Equals(installed.Trim(), release.Tag.Trim(), StringComparison.OrdinalIgnoreCase)
                        ? ToolUpdateState.UpToDate
                        : ToolUpdateState.Available;
                }

                return new ToolUpdateInfo
                {
                    Kind = ToolKind.YtDlp,
                    Name = "yt-dlp",
                    Installed = installed,
                    Latest = release.Tag,
                    State = state
                };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Failed(ToolKind.YtDlp, "yt-dlp", ex);
            }
        }

        private async Task<ToolUpdateInfo> CheckMpvAsync(CancellationToken ct)
        {
            try
            {
                var release = await _downloads.GetLatestReleaseAsync(
                    DownloadInstallService.MpvRepo, DownloadInstallService.IsMpvAsset, ct);

                string path = _processService.FindMpvPath();
                if (!File.Exists(path))
                {
                    return new ToolUpdateInfo
                    {
                        Kind = ToolKind.Mpv,
                        Name = "MPV",
                        Installed = "",
                        Latest = DescribeBuild(release),
                        State = ToolUpdateState.NotInstalled
                    };
                }

                return BuildInfo(ToolKind.Mpv, "MPV", path, _dependencies.CheckMpvStatus(), release);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Failed(ToolKind.Mpv, "MPV", ex);
            }
        }

        private async Task<ToolUpdateInfo> CheckFfmpegAsync(CancellationToken ct)
        {
            try
            {
                var release = await _downloads.GetLatestReleaseAsync(
                    DownloadInstallService.FfmpegRepo, DownloadInstallService.IsFfmpegAsset, ct);

                string path = _dependencies.FindFfmpegPath();
                if (!File.Exists(path))
                {
                    return new ToolUpdateInfo
                    {
                        Kind = ToolKind.Ffmpeg,
                        Name = "FFmpeg",
                        Installed = "",
                        Latest = DescribeBuild(release),
                        State = ToolUpdateState.NotInstalled
                    };
                }

                return BuildInfo(ToolKind.Ffmpeg, "FFmpeg", path, _dependencies.CheckFfmpegStatus(), release);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Failed(ToolKind.Ffmpeg, "FFmpeg", ex);
            }
        }

        private static ToolUpdateInfo BuildInfo(
            ToolKind kind,
            string name,
            string path,
            string installedVersion,
            DownloadInstallService.UpstreamRelease release)
        {
            DateTimeOffset installedAt = File.GetLastWriteTimeUtc(path);

            return new ToolUpdateInfo
            {
                Kind = kind,
                Name = name,
                Installed = $"{CompactVersion(installedVersion)} ({installedAt.ToLocalTime():yyyy-MM-dd})",
                Latest = DescribeBuild(release),
                State = CompareInstalled(
                    installedVersion, release.Tag, release.PublishedAt, installedAt, DateTimeOffset.UtcNow)
            };
        }

        /// <summary>
        /// Decides the state for a tool that may or may not carry a comparable
        /// version number.
        ///
        /// FFmpeg is both cases depending on where the build came from: gyan.dev
        /// publishes versioned releases ("9.0.2") whose version string matches
        /// the tag, and continuous git builds ("N-121xxx") that do not. When both
        /// sides parse, the versions decide - a month-old install of the current
        /// release is still current. Only the unparseable case falls back to the
        /// build date, which is what MPV always uses because its shinchiro tags
        /// are dates.
        /// </summary>
        public static ToolUpdateState CompareInstalled(
            string? installedVersion,
            string? releaseTag,
            DateTimeOffset? releasedAt,
            DateTimeOffset installedAt,
            DateTimeOffset now,
            int staleDays = StaleBuildDays)
        {
            if (TryParseVersion(installedVersion, out var have)
                && TryParseVersion(releaseTag, out var want))
            {
                return want > have ? ToolUpdateState.Available : ToolUpdateState.UpToDate;
            }

            return IsBuildStale(installedAt, releasedAt, now, staleDays)
                ? ToolUpdateState.Available
                : ToolUpdateState.UpToDate;
        }

        private static ToolUpdateInfo Failed(ToolKind kind, string name, Exception ex) => new()
        {
            Kind = kind,
            Name = name,
            Installed = "",
            Latest = "",
            State = ToolUpdateState.Failed,
            Detail = ex.Message
        };

        private static string DescribeBuild(DownloadInstallService.UpstreamRelease release)
        {
            if (release.PublishedAt is { } published)
            {
                return string.IsNullOrEmpty(release.Tag)
                    ? published.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                    : $"{release.Tag} ({published.ToLocalTime():yyyy-MM-dd})";
            }

            return string.IsNullOrEmpty(release.Tag) ? release.AssetName : release.Tag;
        }

        /// <summary>
        /// True when a date-versioned build is old enough to be worth replacing
        /// and upstream has published something since it was installed.
        ///
        /// The one-day allowance absorbs time zones and the gap between a build
        /// being produced and its release being published: a build installed the
        /// same day as the release it came from must not count as stale.
        /// </summary>
        public static bool IsBuildStale(
            DateTimeOffset installedAt,
            DateTimeOffset? releasedAt,
            DateTimeOffset now,
            int staleDays = StaleBuildDays)
        {
            if (releasedAt is not { } released) return false;
            if ((now - installedAt).TotalDays < staleDays) return false;
            return released > installedAt.AddDays(1);
        }

        /// <summary>
        /// Reduces a tool's full version banner to the one token that carries
        /// the version. "mpv v0.41.0-1227-gabc Copyright ..." becomes
        /// "v0.41.0-1227-gabc"; text without any digit is cut to a readable
        /// length instead of filling the dialog with a sentence.
        /// </summary>
        public static string CompactVersion(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "-";

            string trimmed = text.Trim().ReplaceLineEndings(" ");
            foreach (string token in trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (LooksLikeVersion(token) && token.Length <= 48) return token;
            }

            return trimmed.Length <= 48 ? trimmed : trimmed[..47] + "…";
        }

        private static bool LooksLikeVersion(string token)
        {
            if (!token.Any(char.IsDigit)) return false;
            if (token.Contains('.', StringComparison.Ordinal)) return true;

            // A bare date tag ("20261021") is still a version; a lone "1" from
            // an error message is not.
            return token.All(char.IsDigit) && token.Length >= 6;
        }

        /// <summary>
        /// Extracts the first version-shaped number from a tool's version text:
        /// "2025.10.22", "mpv v0.41.0-1227-gabc" and "ffmpeg version 9.0.2-essentials"
        /// all become comparable versions. A run of digits without a dot is not a
        /// version ("exit code 1"), so that case is rejected rather than guessed.
        /// </summary>
        public static bool TryParseVersion(string? text, out Version version)
        {
            version = new Version(0, 0);

            if (string.IsNullOrWhiteSpace(text)) return false;

            string value = text.Trim();

            int start = 0;
            while (start < value.Length && !char.IsDigit(value[start])) start++;
            if (start == value.Length) return false;

            int end = start;
            while (end < value.Length && (char.IsDigit(value[end]) || value[end] == '.')) end++;

            string numeric = value[start..end].Trim('.');
            if (numeric.Length == 0 || !numeric.Contains('.', StringComparison.Ordinal)) return false;
            if (!Version.TryParse(numeric, out var parsed)) return false;

            version = parsed;
            return true;
        }
    }
}
