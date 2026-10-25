// Original implementation, MIT licensed.

using System;
using System.Collections.Generic;
using System.Globalization;

namespace MpvLauncher.Gui.Services
{
    /// <summary>
    /// Builds the helper's command line.
    ///
    /// This returns a list, never a string: the process is started with
    /// ProcessStartInfo.ArgumentList, so a torrent path containing spaces or
    /// quotes stays exactly one argument and cannot inject a helper option.
    /// Every number is clamped here as well as in the helper, because the
    /// settings file is user-editable.
    /// </summary>
    public static class TorrentHelperArguments
    {
        public const int ContractVersion = 2;

        public static IReadOnlyList<string> Build(
            string source,
            string downloadDir,
            string cacheDir,
            string trackerFile,
            string logFile,
            int launcherPid,
            int forwardWindowMiB,
            int uploadLimitKiB,
            int maxConnections,
            int maxHalfOpen,
            int tailWaitSeconds = 0,
            int? selectFile = null)
        {
            if (string.IsNullOrWhiteSpace(source)) throw new ArgumentException("source is required", nameof(source));
            if (string.IsNullOrWhiteSpace(downloadDir)) throw new ArgumentException("download folder is required", nameof(downloadDir));
            if (string.IsNullOrWhiteSpace(cacheDir)) throw new ArgumentException("cache folder is required", nameof(cacheDir));
            if (launcherPid <= 0) throw new ArgumentOutOfRangeException(nameof(launcherPid));

            int conns = TorrentSettings.NormalizeMaxConnections(maxConnections);
            int halfOpen = TorrentSettings.NormalizeHalfOpen(maxHalfOpen, conns);

            var args = new List<string>
            {
                "--source", source,
                "--download-dir", downloadDir,
                "--cache-dir", cacheDir,
                "--launcher-pid", launcherPid.ToString(CultureInfo.InvariantCulture),
                "--window-mib", TorrentSettings.NormalizeForwardWindowMiB(forwardWindowMiB).ToString(CultureInfo.InvariantCulture),
                "--upload-limit-kib", TorrentSettings.NormalizeUploadKiB(uploadLimitKiB).ToString(CultureInfo.InvariantCulture),
                "--max-conns", conns.ToString(CultureInfo.InvariantCulture),
                "--max-half-open", halfOpen.ToString(CultureInfo.InvariantCulture),
                "--tail-wait-sec", TorrentSettings.NormalizeTailWait(tailWaitSeconds).ToString(CultureInfo.InvariantCulture),
                "--contract", ContractVersion.ToString(CultureInfo.InvariantCulture)
            };

            if (!string.IsNullOrWhiteSpace(trackerFile)) args.AddRange(new[] { "--tracker-file", trackerFile });
            if (!string.IsNullOrWhiteSpace(logFile)) args.AddRange(new[] { "--log-file", logFile });
            if (selectFile is >= 0)
                args.AddRange(new[] { "--select-file", selectFile.Value.ToString(CultureInfo.InvariantCulture) });

            return args;
        }

        /// <summary>mpv options used only for a torrent session (never written to mpv.conf).</summary>
        public static IReadOnlyList<string> BuildMpvArguments(int bufferMiB, int cachePauseWaitSeconds)
        {
            int buffer = TorrentSettings.NormalizeMpvBufferMiB(bufferMiB);
            int back = Math.Max(8, buffer / 4);
            int wait = TorrentSettings.NormalizeCachePauseWait(cachePauseWaitSeconds);
            return new List<string>
            {
                "--cache=yes",
                "--cache-pause=yes",
                "--cache-pause-initial=yes",
                "--cache-pause-wait=" + wait.ToString(CultureInfo.InvariantCulture),
                "--demuxer-max-bytes=" + buffer.ToString(CultureInfo.InvariantCulture) + "MiB",
                "--demuxer-max-back-bytes=" + back.ToString(CultureInfo.InvariantCulture) + "MiB",
                "--network-timeout=60"
            };
        }
    }
}
