using MpvLauncher.Gui.Services;

namespace MpvLauncher.Gui.Tests;

/// <summary>
/// Pins the on-disk layout, and proves it can be pointed at a scratch
/// directory.
///
/// <see cref="AppPaths"/> is static: its root comes from the OS profile and its
/// "already laid out" flag is a process-wide latch, so nothing downstream can
/// be redirected. That is why the project had no C# tests at all.
/// <see cref="AppLayout"/> is the same layout with the root injected, which is
/// the seam the rest of the work depends on.
/// </summary>
public class AppLayoutTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "mpvlayout-" + Guid.NewGuid().ToString("N"));

    private string Path_(params string[] parts) =>
        Path.Combine(new[] { _root }.Concat(parts).ToArray());

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
        catch { /* a locked file must not fail the run */ }
    }

    [Fact]
    public void Every_folder_lives_under_the_injected_root()
    {
        var layout = new AppLayout(_root);

        Assert.Equal(_root, layout.Root);
        Assert.Equal(Path_("settings"), layout.SettingsDir);
        Assert.Equal(Path_("bin"), layout.BinDir);
        Assert.Equal(Path_("cache"), layout.CacheDir);
        Assert.Equal(Path_("logs"), layout.LogsDir);
        Assert.Equal(Path_("native"), layout.NativeDir);
        Assert.Equal(Path_("themes"), layout.ThemesDir);
        Assert.Equal(Path_("languages"), layout.LanguagesDir);
        Assert.Equal(Path_("extensions"), layout.ExtensionsDir);
        Assert.Equal(Path_("extensions", "bundle"), layout.ExtensionBundleDir);
        Assert.Equal(Path_("extensions", "chromium"), layout.ChromiumExtensionDir);
        Assert.Equal(Path_("extensions", "firefox"), layout.FirefoxExtensionDir);
    }

    [Fact]
    public void Every_named_file_sits_in_the_folder_that_owns_it()
    {
        var layout = new AppLayout(_root);

        Assert.Equal(Path_("settings", "config.json"), layout.ConfigFile);
        Assert.Equal(Path_("settings", "history.json"), layout.HistoryFile);
        Assert.Equal(Path_("settings", "chromium-extension.pem"), layout.ChromiumKeyFile);
        Assert.Equal(Path_("logs", "host.log"), layout.HostLogFile);
        Assert.Equal(Path_("native", "com.mpv.launcher.firefox.json"), layout.FirefoxManifest);
        Assert.Equal(Path_("native", "com.mpv.launcher.chrome.json"), layout.ChromeManifest);
        Assert.Equal(Path_("bin", "mpv.exe"), layout.MpvExe);
        Assert.Equal(Path_("bin", "yt-dlp.exe"), layout.YtDlpExe);
        Assert.Equal(Path_("bin", "ffmpeg.exe"), layout.FfmpegExe);
    }

    /// <summary>
    /// The Firefox add-on file name is keyed on the add-on id, which is a
    /// constant rather than a path, so it stays static.
    /// </summary>
    [Fact]
    public void The_firefox_xpi_is_named_for_the_addon_id()
    {
        var layout = new AppLayout(_root);

        Assert.Equal(
            Path_("extensions", AppPaths.FirefoxAddonId + ".xpi"),
            layout.FirefoxXpi);
    }

    /// <summary>
    /// mpv's own configuration is not under our root. It belongs to mpv, and
    /// writing it into our folder would make the launcher look like it owns it.
    /// </summary>
    [Fact]
    public void The_mpv_data_folder_is_separate_from_the_launcher_root()
    {
        var layout = new AppLayout(_root);

        Assert.Equal(Path.GetFullPath(Path.Combine(_root, "mpv")), Path.GetFullPath(layout.MpvAppDataDir));
        Assert.Equal(Path.Combine(layout.MpvAppDataDir, "fonts"), layout.MpvFontsDir);
        Assert.Equal(Path.Combine(layout.MpvAppDataDir, "scripts"), layout.MpvScriptsDir);
        Assert.Equal(Path.Combine(layout.MpvAppDataDir, "script-opts"), layout.MpvScriptOptsDir);
        Assert.Equal(Path.Combine(layout.MpvAppDataDir, "shaders"), layout.MpvShadersDir);
    }

    [Fact]
    public void EnsureLayout_creates_every_folder_and_is_safe_to_call_repeatedly()
    {
        var layout = new AppLayout(_root);

        layout.EnsureLayout();
        layout.EnsureLayout();

        foreach (string dir in new[]
                 {
                     layout.SettingsDir, layout.BinDir, layout.CacheDir, layout.LogsDir,
                     layout.NativeDir, layout.ThemesDir, layout.LanguagesDir,
                     layout.ExtensionsDir, layout.ExtensionBundleDir,
                     layout.ChromiumExtensionDir, layout.FirefoxExtensionDir
                 })
        {
            Assert.True(Directory.Exists(dir), dir + " was not created");
        }
    }

    /// <summary>
    /// The latch is per instance, not per process. Two layouts pointing at
    /// different roots must both lay out; a shared static flag would let the
    /// first one suppress the second.
    /// </summary>
    [Fact]
    public void The_ensured_latch_is_per_instance_not_per_process()
    {
        string otherRoot = Path.Combine(Path.GetTempPath(), "mpvlayout-" + Guid.NewGuid().ToString("N"));
        try
        {
            var first = new AppLayout(_root);
            first.EnsureLayout();

            var second = new AppLayout(otherRoot);
            second.EnsureLayout();

            Assert.True(Directory.Exists(Path.Combine(otherRoot, "bin")));
        }
        finally
        {
            try { if (Directory.Exists(otherRoot)) Directory.Delete(otherRoot, true); }
            catch { }
        }
    }

    /// <summary>
    /// A file left at the root by an older layout moves into the folder that
    /// now owns it, and is never overwritten if something is already there.
    /// </summary>
    [Fact]
    public void A_legacy_file_at_the_root_moves_into_its_folder()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "config.json"), "{ \"legacy\": true }");

        new AppLayout(_root).EnsureLayout();

        string moved = Path.Combine(_root, "settings", "config.json");
        Assert.True(File.Exists(moved));
        Assert.Equal("{ \"legacy\": true }", File.ReadAllText(moved));
    }

    [Fact]
    public void Migration_never_overwrites_a_newer_file()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "config.json"), "old");
        Directory.CreateDirectory(Path.Combine(_root, "settings"));
        File.WriteAllText(Path.Combine(_root, "settings", "config.json"), "new");

        new AppLayout(_root).EnsureLayout();

        Assert.Equal("new", File.ReadAllText(Path.Combine(_root, "settings", "config.json")));
    }

    /// <summary>
    /// The shipped app still resolves to the OS profile. A layout refactor
    /// that quietly repointed the real application would be a disaster, so the
    /// static surface is pinned here rather than assumed.
    /// </summary>
    [Fact]
    public void The_static_surface_still_points_at_the_os_profile()
    {
        string expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MPVLauncher");

        Assert.Equal(expected, AppPaths.Root);
        Assert.Equal(Path.Combine(AppPaths.Root, "settings", "config.json"), AppPaths.ConfigFile);
        Assert.Equal(Path.Combine(AppPaths.Root, "bin", "mpv.exe"), AppPaths.MpvExe);
    }
}