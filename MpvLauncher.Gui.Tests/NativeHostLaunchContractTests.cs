// Original implementation, MIT licensed.

using System.IO;
using Xunit;

namespace MpvLauncher.Gui.Tests
{
    /// <summary>
    /// The native host has no window and launches mpv from another process, so
    /// these are checked against its source rather than by starting a browser.
    ///
    /// Each one pins a complaint that was reported: mpv showed a signed CDN path
    /// instead of the page's name, a player page reached mpv unchanged and did
    /// nothing, and the history list only caught up after a restart.
    /// </summary>
    public class NativeHostLaunchContractTests
    {
        private static string Host => RepoSource.Read(Path.Combine(
            "MpvLauncher.Gui", "Services", "NativeHostService.cs"));

        [Fact]
        public void MpvWindowIsNamedWithThePageTitle()
        {
            string src = Host;

            Assert.Contains("--force-media-title=", src, System.StringComparison.Ordinal);
            Assert.Contains("--title=", src, System.StringComparison.Ordinal);
            // The title comes from the extension's message, not from the URL.
            Assert.Contains("NormaliseTitle(pageTitle)", src, System.StringComparison.Ordinal);
        }

        [Fact]
        public void PlayerPageIsResolvedBeforeMpvStarts()
        {
            Assert.Contains("MediaPageResolver.Resolve(safeUrl", Host, System.StringComparison.Ordinal);
        }

        [Fact]
        public void HistoryListFollowsTheHostsWrites()
        {
            string src = RepoSource.MainWindow();

            Assert.Contains("new FileSystemWatcher(AppPaths.SettingsDir, \"history.json\")",
                src, System.StringComparison.Ordinal);
            Assert.Contains("RefreshHistoryFromDisk();", src, System.StringComparison.Ordinal);
        }
    }
}
