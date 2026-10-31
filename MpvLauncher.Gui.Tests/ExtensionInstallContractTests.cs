using System.Text.Json;

namespace MpvLauncher.Gui.Tests;

/// <summary>
/// Holds the Chromium install to the one thing it is allowed to do: write a
/// folder.
///
/// Up to 1.9.16 the installer registered the unpacked extension by rewriting
/// the browsers' URL handlers in HKCU to pass --load-extension. Chrome 137
/// began refusing that switch even with its escape-hatch feature flag set, so
/// the extension never loaded there, while the override itself stayed: a
/// Start-menu launch ran the browser with a literal "%1", and every window
/// carried an "unsupported command-line flag" bar. It took a user reporting
/// broken browsers to find it.
///
/// The fix is a removal, and a removal leaves nothing behind for a test to
/// observe at runtime - so these pin the source and the sentence the user is
/// left with. If someone reintroduces flag rewriting, the first test fails
/// before the second release ships it.
/// </summary>
public class ExtensionInstallContractTests
{
    private static string RepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 10 && dir != null; i++, dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate)) return candidate;

            string nested = Path.Combine(dir.FullName, "mpv-opn", relative);
            if (File.Exists(nested)) return nested;
        }
        throw new FileNotFoundException($"could not find {relative}");
    }

    private static string Installer => File.ReadAllText(RepoFile(Path.Combine(
        "MpvLauncher.Gui", "Services", "ExtensionInstallService.cs")));

    [Fact]
    public void The_installer_does_not_rewrite_browser_launch_commands()
    {
        string[] forbidden =
        {
            "PatchChromiumLaunchCommands",
            "PatchChromiumShortcuts",
            "EnsureChromiumCommandOverrides",
            "InjectExtensionFlags"
        };

        foreach (string name in forbidden)
            Assert.DoesNotContain(name, Installer, StringComparison.Ordinal);
    }

    [Fact]
    public void The_repair_pass_is_called_before_the_window_opens()
    {
        string window = RepoSource.MainWindow();

        Assert.Contains("RemoveLegacyLaunchOverrides();", window, StringComparison.Ordinal);
    }

    [Fact]
    public void The_repair_pass_runs_all_three_cleanup_steps()
    {
        // A machine can carry any subset: registry values from one build,
        // shortcut arguments from another. Skipping one step would leave the
        // breakage in place on exactly the machines that need it least
        // predicted.
        string install = Installer;

        Assert.Contains("RemoveChromiumCommandOverrides()", install, StringComparison.Ordinal);
        Assert.Contains("UnpatchChromiumLaunchCommands()", install, StringComparison.Ordinal);
        Assert.Contains("UnpatchChromiumShortcuts()", install, StringComparison.Ordinal);
    }

    /// <summary>
    /// Manual loading is the only Chromium path left, so the wording is part
    /// of the feature rather than decoration.
    /// </summary>
    [Fact]
    public void The_install_hint_says_where_to_load_the_folder()
    {
        string path = RepoFile(Path.Combine("locales", "en.json"));
        using var doc = JsonDocument.Parse(File.ReadAllText(path));

        string hint = doc.RootElement.GetProperty("translations")
            .GetProperty("install_hint_chromium").GetString() ?? "";

        Assert.Contains("chrome://extensions", hint, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Developer mode", hint, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Load unpacked", hint, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Once a browser reports the extension as loaded, telling the user to load
    /// it again is the worst possible answer: it reads as if the step they just
    /// completed failed.
    /// </summary>
    [Fact]
    public void The_manual_hint_is_guarded_by_the_reported_browser_state()
    {
        string window = RepoSource.MainWindow();

        int guard = window.IndexOf("bool chromiumMissing", StringComparison.Ordinal);
        int hint = window.IndexOf("\"install_hint_chromium\"", StringComparison.Ordinal);

        Assert.True(guard >= 0, "BuildInstallMessage no longer tracks a missing browser");
        Assert.True(hint > guard, "the Chromium hint is shown without checking the browser state");
        Assert.Contains("install_item_browser_missing", window, StringComparison.Ordinal);
        Assert.Contains("install_item_browser_loaded", window, StringComparison.Ordinal);
    }
}
