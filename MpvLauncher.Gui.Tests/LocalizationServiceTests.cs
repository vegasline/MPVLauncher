using MpvLauncher.Gui.Services;

namespace MpvLauncher.Gui.Tests;

/// <summary>
/// Pins the behaviour <see cref="LocalizationService"/> has today.
///
/// This exists to make the harness itself trustworthy: if these pass, a test
/// can construct a module from the application and observe its output, which
/// is the precondition for every later change being verifiable.
///
/// <see cref="LocalizationService"/> is chosen because it already takes its
/// directory as a constructor parameter. Most modules do not, and making them
/// testable is candidate 2 in the architecture review.
/// </summary>
public class LocalizationServiceTests
{
    /// <summary>Writes a locale file into a throwaway directory.</summary>
    private static string TempDirWith(string code, string json)
    {
        string dir = Path.Combine(Path.GetTempPath(), "mpvloc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, code + ".json"), json);
        return dir;
    }

    [Fact]
    public void Get_returns_the_translation_for_a_known_key()
    {
        string dir = TempDirWith("en", """
            { "translations": { "status_ready": "Ready" } }
            """);

        var loc = new LocalizationService(dir);

        Assert.Equal("Ready", loc.Get("status_ready"));
    }

    [Fact]
    public void Get_falls_back_to_the_supplied_default_for_a_missing_key()
    {
        string dir = TempDirWith("en", """
            { "translations": { "status_ready": "Ready" } }
            """);

        var loc = new LocalizationService(dir);

        Assert.Equal("Ready", loc.Get("status_ready"));
        Assert.Equal("Ready (tr)", loc.Get("status_ready_tr", "Ready (tr)"));
    }

    /// <summary>
    /// A missing key with no fallback returns the key itself. This is what
    /// makes an incomplete language pack visible on screen instead of blank,
    /// and it is the behaviour that lets an untranslated string masquerade as
    /// a translation elsewhere in the codebase.
    /// </summary>
    [Fact]
    public void Get_returns_the_key_itself_when_there_is_no_fallback()
    {
        string dir = TempDirWith("en", """
            { "translations": { } }
            """);

        var loc = new LocalizationService(dir);

        Assert.Equal("nope", loc.Get("nope"));
    }

    [Fact]
    public void A_malformed_file_is_reported_rather_than_swallowed()
    {
        string dir = TempDirWith("en", "{ this is not json");

        var loc = new LocalizationService(dir);

        Assert.NotNull(loc.LoadError);
    }

    /// <summary>
    /// The code becomes a file name, so it must not be able to point outside
    /// the language folder.
    /// </summary>
    [Theory]
    [InlineData("../secrets")]
    [InlineData("..\\secrets")]
    [InlineData("with/slash")]
    [InlineData("with\\backslash")]
    public void A_traversing_language_code_is_refused(string code)
    {
        string dir = TempDirWith("en", """
            { "translations": { } }
            """);

        var loc = new LocalizationService(dir);
        loc.LoadLanguage(code);

        Assert.NotNull(loc.LoadError);
    }
}