using System.Text.Json;
using MpvLauncher.Gui.Services;

namespace MpvLauncher.Gui.Tests;

/// <summary>
/// Holds the three extension manifests to each other.
///
/// Three files wrote one manifest: the shipped extension/manifest.json that
/// goes to addons.mozilla.org, and the two the application writes into a
/// browser profile. Nothing parsed any of them, so they drifted: the shipped
/// one declared a background service worker, which Firefox refuses to load at
/// all, and it promised Firefox 128 while asking for a key that only exists
/// from 140. The application also requested a permission no file used.
///
/// The application skips this file when it copies the bundle
/// (ExtensionInstallService.cs:787,810) and writes its own, so extension/
/// manifest.json is the Firefox manifest and nothing else.
/// </summary>
public class ManifestTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "mpvmani-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }
        catch { }
    }

    /// <summary>The manifest that is zipped and submitted to the store.</summary>
    private static JsonElement Shipped()
    {
        string path = FindRepoFile(Path.Combine("extension", "manifest.json"));
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.Clone();
    }

    /// <summary>Walks up from the test binaries to the repository root.</summary>
    private static string FindRepoFile(string relative)
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

    private JsonElement Written(bool firefox)
    {
        Directory.CreateDirectory(_dir);
        if (firefox) ExtensionInstallService.WriteFirefoxManifest(_dir);
        else ExtensionInstallService.WriteChromiumManifest(_dir, "PUBLIC-KEY-FOR-TEST");
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, "manifest.json")));
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// A canonical rendering of a JSON value: whitespace and key order removed.
    ///
    /// Comparing the raw text would compare formatting instead of content -
    /// the shipped file is hand-indented and the application writes with
    /// WriteIndented, so two manifests saying exactly the same thing come out
    /// as different strings.
    /// </summary>
    private static string Canonical(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(",",
            e.EnumerateObject()
             .OrderBy(p => p.Name, StringComparer.Ordinal)
             .Select(p => JsonSerializer.Serialize(p.Name) + ":" + Canonical(p.Value))) + "}",
        JsonValueKind.Array => "[" + string.Join(",",
            e.EnumerateArray().Select(Canonical)) + "]",
        JsonValueKind.String => JsonSerializer.Serialize(e.GetString()),
        _ => e.GetRawText()
    };

    /// <summary>Fields every copy must state identically.</summary>
    public static TheoryData<string> SharedFields => new()
    {
        "manifest_version", "name", "version", "description",
        "permissions", "host_permissions", "icons"
    };

    [Theory]
    [MemberData(nameof(SharedFields))]
    public void The_chromium_manifest_agrees_with_the_shipped_one(string field)
    {
        var shipped = Shipped();
        var written = Written(firefox: false);

        Assert.Equal(Canonical(shipped.GetProperty(field)),
                     Canonical(written.GetProperty(field)));
    }

    [Theory]
    [MemberData(nameof(SharedFields))]
    public void The_firefox_manifest_agrees_with_the_shipped_one(string field)
    {
        var shipped = Shipped();
        var written = Written(firefox: true);

        Assert.Equal(Canonical(shipped.GetProperty(field)),
                     Canonical(written.GetProperty(field)));
    }

    /// <summary>
    /// A permission nothing uses is one the user is asked to grant. Both
    /// application manifests requested webNavigation; no file called it.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void No_manifest_requests_a_permission_nobody_uses(bool firefox)
    {
        var written = Written(firefox);

        var granted = written.GetProperty("permissions").EnumerateArray()
            .Select(p => p.GetString()!).OrderBy(x => x, StringComparer.Ordinal).ToArray();

        Assert.DoesNotContain("webNavigation", granted);
        Assert.Equal(
            new[] { "activeTab", "nativeMessaging", "scripting", "storage", "tabs", "webRequest" },
            granted);
    }

    /// <summary>
    /// Firefox refuses an add-on that declares a service worker, so the two
    /// paths must not disagree about which shape this is.
    /// </summary>
    [Fact]
    public void The_shipped_manifest_is_loadable_by_firefox()
    {
        var shipped = Shipped();
        var background = shipped.GetProperty("background");

        Assert.False(background.TryGetProperty("service_worker", out _),
            "Firefox MV3 rejects service workers and will not load the add-on");
        Assert.True(background.TryGetProperty("scripts", out var scripts),
            "Firefox needs background.scripts");
        Assert.Equal("background.js", scripts.EnumerateArray().Last().GetString());
    }

    [Fact]
    public void The_firefox_manifest_is_loadable_by_firefox()
    {
        var written = Written(firefox: true);
        var background = written.GetProperty("background");

        Assert.False(background.TryGetProperty("service_worker", out _));
        Assert.True(background.TryGetProperty("scripts", out _));
    }

    /// <summary>
    /// Chromium is the opposite: it rejects background.scripts outright, even
    /// alongside a valid service_worker.
    /// </summary>
    [Fact]
    public void The_chromium_manifest_uses_a_service_worker()
    {
        var written = Written(firefox: false);
        var background = written.GetProperty("background");

        Assert.True(background.TryGetProperty("service_worker", out var worker));
        Assert.Equal("background.js", worker.GetString());
        Assert.False(background.TryGetProperty("scripts", out _));
    }

    /// <summary>
    /// gecko.data_collection_permissions only exists from Firefox 140, so a
    /// lower strict_min_version asks the browser for a key it cannot read and
    /// the store rejects the submission.
    /// </summary>
    [Fact]
    public void The_minimum_firefox_version_covers_the_keys_being_used()
    {
        var shipped = Shipped();
        var gecko = shipped.GetProperty("browser_specific_settings").GetProperty("gecko");

        Assert.True(gecko.TryGetProperty("data_collection_permissions", out _));
        Assert.True(double.Parse(gecko.GetProperty("strict_min_version").GetString()!) >= 140.0);
    }

    [Fact]
    public void The_addon_id_is_the_one_the_application_installs_under()
    {
        var shipped = Shipped();
        var gecko = shipped.GetProperty("browser_specific_settings").GetProperty("gecko");

        Assert.Equal(AppPaths.FirefoxAddonId, gecko.GetProperty("id").GetString());
    }

    /// <summary>
    /// The manifest is only worth a check if it parses. Three of these used to
    /// be generated as C# dictionaries that nothing ever read back.
    /// </summary>
    [Fact]
    public void A_written_manifest_is_valid_json()
    {
        var written = Written(firefox: true);

        Assert.Equal(3, written.GetProperty("manifest_version").GetInt32());
        Assert.Equal("MPV Launcher", written.GetProperty("name").GetString());
    }

    /// <summary>
    /// The Chromium manifest is the only one that carries the public key; the
    /// browser refuses an unpacked extension whose key does not match its id.
    /// </summary>
    [Fact]
    public void Only_the_chromium_manifest_carries_a_key()
    {
        Assert.True(Written(firefox: false).TryGetProperty("key", out _));
        Assert.False(Written(firefox: true).TryGetProperty("key", out _));
    }
}