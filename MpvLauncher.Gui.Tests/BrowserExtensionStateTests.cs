// Original implementation, MIT licensed.

using MpvLauncher.Gui.Services;

namespace MpvLauncher.Gui.Tests;

/// <summary>
/// Reads a browser profile the way the browser writes it.
///
/// The point of this check is to stop the launcher from claiming an install it
/// cannot see. It is read-only, so the interesting cases are the ones where a
/// profile says "no": a missing entry, a disabled entry, and a profile whose
/// preferences do not parse must all come back as not loaded rather than as an
/// exception or an optimistic true.
/// </summary>
public class BrowserExtensionStateTests : IDisposable
{
    private const string Id = "hbllekfpmendimkmjdcmcgiopnbopacd";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "mpvstate-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
        catch { }
    }

    private string Profile(string root, string name, string settingsJson)
    {
        string dir = Path.Combine(root, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Preferences"), "{\"profile\":{\"name\":\"x\"}}");
        File.WriteAllText(Path.Combine(dir, "Secure Preferences"),
            "{\"extensions\":{\"settings\":" + settingsJson + "}}");
        return dir;
    }

    private static (string, string) Root(string path) => ("Brave", path);

    [Fact]
    public void An_enabled_entry_counts_as_loaded()
    {
        string root = Path.Combine(_root, "User Data");
        Profile(root, "Default",
            "{\"" + Id + "\":{\"location\":4,\"path\":\"C:\\\\x\",\"state\":1}}");

        var states = BrowserExtensionState.Scan(Id, new[] { Root(root) });

        Assert.Single(states);
        Assert.Equal("Brave", states[0].Name);
        Assert.Equal(1, states[0].Profiles);
        Assert.True(states[0].Loaded);
    }

    [Fact]
    public void A_disabled_entry_is_not_loaded()
    {
        string root = Path.Combine(_root, "User Data");
        Profile(root, "Default", "{\"" + Id + "\":{\"location\":4,\"state\":0}}");

        var states = BrowserExtensionState.Scan(Id, new[] { Root(root) });

        Assert.False(states[0].Loaded);
        Assert.Equal(0, states[0].LoadedProfiles);
    }

    [Fact]
    public void One_loaded_profile_among_several_is_enough()
    {
        string root = Path.Combine(_root, "User Data");
        Profile(root, "Default", "{\"" + Id + "\":{\"state\":1}}");
        Profile(root, "Profile 1", "{}");

        var states = BrowserExtensionState.Scan(Id, new[] { Root(root) });

        Assert.Equal(2, states[0].Profiles);
        Assert.Equal(1, states[0].LoadedProfiles);
        Assert.True(states[0].Loaded);
    }

    [Fact]
    public void A_profile_without_our_id_is_reported_as_not_loaded()
    {
        string root = Path.Combine(_root, "User Data");
        Profile(root, "Default", "{\"abcdefghijklmnopabcdefghijklmnop\":{\"state\":1}}");

        var states = BrowserExtensionState.Scan(Id, new[] { Root(root) });

        Assert.Equal(1, states[0].Profiles);
        Assert.False(states[0].Loaded);
    }

    [Fact]
    public void An_unreadable_profile_preferences_file_does_not_throw()
    {
        string root = Path.Combine(_root, "User Data");
        string dir = Path.Combine(root, "Default");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Preferences"), "{}");
        File.WriteAllText(Path.Combine(dir, "Secure Preferences"),
            "{not json but mentions " + Id + "}");

        var states = BrowserExtensionState.Scan(Id, new[] { Root(root) });

        Assert.False(states[0].Loaded);
    }

    [Fact]
    public void A_missing_browser_folder_is_left_out_entirely()
    {
        var states = BrowserExtensionState.Scan(Id,
            new[] { ("Chrome", Path.Combine(_root, "nope")) });

        Assert.Empty(states);
    }

    [Fact]
    public void The_system_profile_is_ignored()
    {
        string root = Path.Combine(_root, "User Data");
        Profile(root, "System Profile", "{\"" + Id + "\":{\"state\":1}}");
        Profile(root, "Default", "{}");

        var states = BrowserExtensionState.Scan(Id, new[] { Root(root) });

        Assert.Equal(1, states[0].Profiles);
        Assert.False(states[0].Loaded);
    }

}
