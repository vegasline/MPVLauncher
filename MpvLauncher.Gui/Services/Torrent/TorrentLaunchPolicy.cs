// Original implementation, MIT licensed.

using System;

namespace MpvLauncher.Gui.Services
{
    public enum TorrentLaunchBlock
    {
        None,
        FeatureDisabled,
        HelperMissing
    }

    /// <summary>
    /// Decides whether a torrent source may start.
    ///
    /// The switch is checked here and not only in the UI because the same
    /// method is used by the URL box, drag/drop, file picker and history rows.
    /// With the feature off, no helper process is created and no socket is
    /// opened, even when the executable is already installed.
    /// </summary>
    public static class TorrentLaunchPolicy
    {
        public static TorrentLaunchBlock Check(AppConfig config, bool helperInstalled)
        {
            if (!config.TorrentStreamingEnabled) return TorrentLaunchBlock.FeatureDisabled;
            if (!helperInstalled) return TorrentLaunchBlock.HelperMissing;
            return TorrentLaunchBlock.None;
        }

        /// <summary>
        /// The browser extension path never starts a torrent session, whatever
        /// the setting says. The extension talks to the native host directly,
        /// outside the GUI's own gate, so this check is independent of config.
        /// </summary>
        public static bool IsRejectedFromBrowser(string? url) =>
            TorrentSourceParser.IsTorrentLike(url);

        /// <summary>Default download folder, used until the user picks another one.</summary>
        public static string DefaultDownloadDirectory =>
            System.IO.Path.Combine(AppPaths.TorrentCacheDir, "downloads");

        public static string EffectiveDownloadDirectory(AppConfig config) =>
            string.IsNullOrWhiteSpace(config.TorrentDownloadDir)
                ? DefaultDownloadDirectory
                : config.TorrentDownloadDir.Trim();

        public static string LastFileKey(string infoHash) =>
            string.IsNullOrWhiteSpace(infoHash) ? "" : infoHash.Trim().ToLowerInvariant();
    }
}
