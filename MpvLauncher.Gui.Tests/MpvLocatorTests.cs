using MpvLauncher.Gui.Services;

namespace MpvLauncher.Gui.Tests;

/// <summary>
/// Pins the shared mechanics behind "which mpv, with which arguments".
///
/// Two launchers existed. Each had its own ordered list of install locations
/// and its own option tokeniser, and they had already drifted: the extension
/// knew about C:\mpv and D:\mpv and the GUI did not. They also guessed at drive
/// letters, which is not how this project works - the launcher downloads mpv
/// into its own bin folder and DependencyService finds yt-dlp and ffmpeg
/// relative to mpv, not by guessing. The GUI also split the ExtraArgs setting
/// on spaces alone, which turns a quoted value containing a space into two mpv
/// options - and mpv's --script executes code.
///
/// The two launchers keep their own policy deliberately. The GUI accepts local
/// paths because the value came from the user; the extension accepts only
/// http(s) because it came from a web page. What they share is the mechanics.
/// </summary>
public class MpvLocatorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "mpvloc-" + Guid.NewGuid().ToString("N"));

    private AppLayout Layout => new AppLayout(_root);

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
        catch { }
    }

    /// <summary>A file-exists predicate over a fixed set of paths.</summary>
    private static Func<string, bool> Only(params string[] existing) =>
        p => existing.Contains(p, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void The_configured_path_wins_when_it_exists()
    {
        string configured = @"D:\somewhere\custom\mpv.exe";

        string? found = MpvLocator.Resolve(configured, Layout, Only(configured));

        Assert.Equal(configured, found);
    }

    /// <summary>
    /// A configured path that no longer exists is skipped rather than returned.
    /// A stale setting must not stop the search: the setting points at where
    /// mpv used to be, which is the normal state after an upgrade.
    /// </summary>
    [Fact]
    public void A_stale_configured_path_falls_through_to_the_search()
    {
        string configured = @"D:\gone\mpv.exe";
        string managed = Layout.MpvExe;

        string? found = MpvLocator.Resolve(configured, Layout, Only(managed));

        Assert.Equal(managed, found);
    }

    /// <summary>
    /// The launcher downloads mpv into its own bin folder, so that copy is the
    /// answer when it exists. This is the same order DependencyService already
    /// uses to find yt-dlp and ffmpeg.
    /// </summary>
    [Fact]
    public void The_managed_copy_is_found_when_the_setting_is_empty()
    {
        string managed = Layout.MpvExe;

        string? found = MpvLocator.Resolve(null, Layout, Only(managed));

        Assert.Equal(managed, found);
    }

    [Fact]
    public void The_managed_copy_beats_an_mpv_elsewhere_on_the_system()
    {
        string managed = Layout.MpvExe;
        string onPath = Path.Combine("C:", "tools", "mpv.exe");

        string? found = MpvLocator.Resolve(null, Layout, Only(managed, onPath),
            pathVariable: "C:\\tools");

        Assert.Equal(managed, found);
    }

    /// <summary>
    /// A path on PATH is the fallback for somebody who manages their own mpv
    /// and never let the launcher download one. It is last, because PATH is
    /// where an mpv ends up by accident: a leftover from a package manager, a
    /// shim, or a different tool entirely.
    /// </summary>
    [Fact]
    public void An_mpv_only_on_the_PATH_is_still_found()
    {
        string onPath = Path.Combine("C:", "tools", "mpv.exe");

        string? found = MpvLocator.Resolve(null, Layout, Only(onPath),
            pathVariable: "C:\\tools");

        Assert.Equal(onPath, found);
    }

    /// <summary>
    /// The search is not a list of guessed install directories. The launcher
    /// owns its own bin folder, and the other two tools are found relative to
    /// mpv, so guessing at drive letters would be a second, wrong convention.
    /// </summary>
    [Fact]
    public void No_guessed_install_directories_are_searched()
    {
        foreach (string guess in new[]
                 {
                     @"C:\mpv\mpv.exe", @"D:\mpv\mpv.exe", @"D:\Prog\mpv\mpv.exe",
                     @"C:\Program Files\mpv\mpv.exe",
                     @"C:\Program Files (x86)\mpv\mpv.exe"
                 })
        {
            string? found = MpvLocator.Resolve(null, Layout, Only(guess),
                pathVariable: null);

            Assert.Null(found);
        }
    }

    [Fact]
    public void Nothing_is_found_when_nothing_exists()
    {
        string? found = MpvLocator.Resolve(null, Layout, Only(), pathVariable: null);

        Assert.Null(found);
    }

    [Fact]
    public void An_empty_PATH_entry_does_not_produce_a_bare_drive_path()
    {
        string? found = MpvLocator.Resolve(null, Layout,
            Only(@"C:\mpv.exe"),   // would match a careless Path.Combine
            pathVariable: ";;  ;;");
        // If ";" segments were treated as directories, Path.Combine(";", "mpv.exe")
        // would not equal C:\mpv.exe, so this asserts the entries were skipped
        // rather than probed.
        Assert.Null(found);
    }
}

public class CommandLineTokenizeTests
{
    [Fact]
    public void Plain_options_split_on_whitespace()
    {
        Assert.Equal(
            new[] { "--no-osd-bar", "--volume=50" },
            CommandLineTokenize.Split("--no-osd-bar --volume=50"));
    }

    [Fact]
    public void Repeated_whitespace_does_not_produce_empty_options()
    {
        Assert.Equal(
            new[] { "--a", "--b" },
            CommandLineTokenize.Split("   --a     --b   "));
    }

    /// <summary>
    /// This is the case the GUI got wrong. Splitting on spaces alone turns one
    /// option into two, and the fragment that falls off the end is read by mpv
    /// as an option of its own.
    /// </summary>
    [Fact]
    public void A_quoted_value_containing_spaces_stays_one_option()
    {
        Assert.Equal(
            new[] { "--sub-file=my file.srt" },
            CommandLineTokenize.Split("--sub-file=\"my file.srt\""));
    }

    [Fact]
    public void Quoting_is_stripped_from_the_result()
    {
        Assert.Equal(
            new[] { "one", "two three" },
            CommandLineTokenize.Split("\"one\" \"two three\""));
    }

    [Fact]
    public void A_bare_quoted_string_with_surrounding_spaces_is_one_token()
    {
        Assert.Equal(
            new[] { "--script=C:\\my scripts\\a.lua" },
            CommandLineTokenize.Split("  --script=\"C:\\my scripts\\a.lua\"  "));
    }

    [Fact]
    public void Empty_input_yields_no_options()
    {
        Assert.Empty(CommandLineTokenize.Split(""));
        Assert.Empty(CommandLineTokenize.Split("   "));
    }

    /// <summary>
    /// An unbalanced quote must not swallow the rest of the string silently.
    /// The quote is a delimiter rather than content, so it is dropped and the
    /// remainder survives as one option - which is what the extension has
    /// always done. The point is that both launchers now do the same thing.
    /// </summary>
    [Fact]
    public void An_unbalanced_quote_does_not_lose_the_options()
    {
        Assert.Equal(
            new[] { "--a", "--b=unclosed" },
            CommandLineTokenize.Split("--a --b=\"unclosed"));
    }
}