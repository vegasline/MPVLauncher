// Original implementation, MIT licensed.

using System.IO;
using Xunit;

namespace MpvLauncher.Gui.Tests
{
    /// <summary>
    /// What "Install / repair" is allowed to touch.
    ///
    /// Firefox's add-on is signed and installed from addons.mozilla.org, so the
    /// app must not plant a second, unsigned copy in the profile: the two fight
    /// over the same add-on id. Chromium has no store listing, so its unpacked
    /// folder is still written - and the native messaging host, which is the
    /// only thing the browser actually needs to reach the app, is kept alive on
    /// every launch instead of only when the button is pressed.
    /// </summary>
    public class ExtensionInstallScopeTests
    {
        private static string InstallService => RepoSource.Read(
            Path.Combine("MpvLauncher.Gui", "Services", "ExtensionInstallService.cs"));

        private static string HostService => RepoSource.Read(
            Path.Combine("MpvLauncher.Gui", "Services", "BrowserIntegrationService.cs"));

        [Fact]
        public void FirefoxIsLeftToTheStore()
        {
            string src = InstallService;

            Assert.DoesNotContain("SideloadFirefoxXpi", src, System.StringComparison.Ordinal);
            Assert.DoesNotContain("WriteFirefoxProfilePrefs", src, System.StringComparison.Ordinal);
            Assert.DoesNotContain("EnsureExtensionFiles(AppPaths.FirefoxExtensionDir)",
                src, System.StringComparison.Ordinal);
            Assert.DoesNotContain("\"install_item_firefox", src, System.StringComparison.Ordinal);
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
