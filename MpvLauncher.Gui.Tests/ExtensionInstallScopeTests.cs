// Original implementation, MIT licensed.

using System.IO;
using Xunit;

namespace MpvLauncher.Gui.Tests
{
    /// <summary>
    /// What "Install / repair" writes.
    ///
    /// Both browser folders are delivered as files: Chromium's unpacked folder
    /// and Firefox's folder plus an XPI packed from it. What must never come
    /// back is a write into a Firefox profile - the signed add-on on
    /// addons.mozilla.org owns that id, and the two copies fight.
    /// </summary>
    public class ExtensionInstallScopeTests
    {
        private static string InstallService => RepoSource.Read(
            Path.Combine("MpvLauncher.Gui", "Services", "ExtensionInstallService.cs"));

        private static string HostService => RepoSource.Read(
            Path.Combine("MpvLauncher.Gui", "Services", "BrowserIntegrationService.cs"));

        [Fact]
        public void FirefoxProfileIsLeftAlone()
        {
            string src = InstallService;

            Assert.DoesNotContain("SideloadFirefoxXpi", src, System.StringComparison.Ordinal);
            Assert.DoesNotContain("WriteFirefoxProfilePrefs", src, System.StringComparison.Ordinal);
        }

        [Fact]
        public void FirefoxFolderAndXpiAreWritten()
        {
            string src = InstallService;

            Assert.Contains("EnsureExtensionFiles(AppPaths.FirefoxExtensionDir)", src,
                System.StringComparison.Ordinal);
            Assert.Contains("WriteFirefoxManifest(AppPaths.FirefoxExtensionDir)", src,
                System.StringComparison.Ordinal);
            Assert.Contains("PackFirefoxXpi();", src, System.StringComparison.Ordinal);
            Assert.Contains("\"install_item_firefox_xpi\"", src, System.StringComparison.Ordinal);
        }

        [Fact]
        public void ChromiumFolderIsStillWritten()
        {
            Assert.Contains("EnsureExtensionFiles(AppPaths.ChromiumExtensionDir)", InstallService,
                System.StringComparison.Ordinal);
        }

        [Fact]
        public void HostManifestsAreRestoredOnEveryLaunch()
        {
            string window = RepoSource.Read(
                Path.Combine("MpvLauncher.Gui", "MainWindow.Dependencies.cs"));

            Assert.Contains("EnsureNativeHost();", window, System.StringComparison.Ordinal);

            // A missing manifest is written; this is what makes a hand-loaded
            // extension work without ever pressing Install / repair.
            Assert.Contains("if (!File.Exists(AppPaths.ChromeManifest)) InstallChromium();",
                HostService, System.StringComparison.Ordinal);
            Assert.Contains("if (!File.Exists(AppPaths.FirefoxManifest)) InstallFirefox();",
                HostService, System.StringComparison.Ordinal);
        }
    }
}
