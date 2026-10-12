using System.Reflection;
using System.Text.Json;
using MpvLauncher.Gui.Services;

namespace MpvLauncher.Gui.Tests;

/// <summary>
/// Holds the launcher's answer in localization keys.
///
/// The update and extension-install cluster already works this way -
/// UpdateStatus carries a DetailKey and DetailArg[], ExtensionInstallDetail
/// carries a key - and half the rest of the codebase did not. PlayMedia
/// returned a finished English sentence, and MainWindow.Play used it verbatim
/// for everything except a loader failure. "MPV started." is the most
/// frequently displayed string in the application and it reached twelve
/// languages untranslated.
///
/// A key is only useful if every language actually has it, so this reads the
/// locale files rather than trusting that somebody remembered. That also
/// catches the key a module invents at runtime, which a source-text grep over
/// _locService.Get("literal") cannot see.
/// </summary>
public class LaunchResultTests
{
    [Fact]
    public void A_successful_launch_answers_with_a_key_not_a_sentence()
    {
        var result = new LaunchResult(true, LaunchResult.Started, Array.Empty<string>(), default);

        Assert.Equal(LaunchResult.Started, result.MessageKey);
        Assert.DoesNotContain(" ", result.MessageKey);
    }

    /// <summary>
    /// Every key a module can hand back is a constant, and every one of them
    /// has to exist in every shipped language.
    /// </summary>
    [Fact]
    public void Every_key_a_launch_can_return_exists_in_every_language()
    {
        var keys = new[]
        {
            LaunchResult.Started,
            LaunchResult.NotFound,
            LaunchResult.EmptyInput,
            LaunchResult.LaunchFailed
        };

        var missing = new List<string>();

        foreach (string file in LocaleFiles())
        {
            var available = ReadTranslations(file);
            foreach (string key in keys)
            {
                if (!available.ContainsKey(key))
                    missing.Add($"{Path.GetFileName(file)} is missing {key}");
            }
        }

        Assert.True(missing.Count == 0,
            string.Join("; ", missing));
    }

    /// <summary>
    /// A template with a {0} and no argument renders as the literal braces,
    /// which reads as a bug to the user. The arity has to match.
    /// </summary>
    [Fact]
    public void Every_template_placeholder_is_supplied_by_the_result()
    {
        var expectedArity = new Dictionary<string, int>
        {
            [LaunchResult.Started] = 0,
            [LaunchResult.NotFound] = 0,
            [LaunchResult.EmptyInput] = 0,
            [LaunchResult.LaunchFailed] = 1
        };

        var problems = new List<string>();
        foreach (string file in LocaleFiles())
        {
            var available = ReadTranslations(file);
            foreach (var (key, arity) in expectedArity)
            {
                if (!available.TryGetValue(key, out string? template)) continue;
                int placeholders = CountPlaceholders(template);
                if (placeholders != arity)
                    problems.Add($"{Path.GetFileName(file)} {key} has {placeholders} placeholder(s), given {arity}");
            }
        }

        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    /// <summary>
    /// The protocol is only worth anything if it is what the module actually
    /// returns. A sentence here would put the bug straight back.
    /// </summary>
    [Fact]
    public void PlayMedia_never_builds_a_sentence_itself()
    {
        string source = StripComments(File.ReadAllText(FindRepoFile(Path.Combine(
            "MpvLauncher.Gui", "Services", "ProcessService.cs"))));

        Assert.DoesNotContain("\"MPV started.\"", source);
        Assert.DoesNotContain("\"mpv was not found.\"", source);
        Assert.DoesNotContain("\"Invalid or empty URL.\"", source);
    }

    /// <summary>
    /// Drops comments, so a doc comment naming the string a module used to
    /// return is not mistaken for the module still returning it.
    /// </summary>
    private static string StripComments(string source)
    {
        string noBlocks = System.Text.RegularExpressions.Regex.Replace(
            source, @"/\*.*?\*/", "",
            System.Text.RegularExpressions.RegexOptions.Singleline);
        return string.Join("\n",
            noBlocks.Split('\n').Where(l => !l.TrimStart().StartsWith("//")));
    }

    [Fact]
    public void The_window_resolves_the_key_rather_than_using_the_message()
    {
        string source = File.ReadAllText(FindRepoFile(Path.Combine(
            "MpvLauncher.Gui", "MainWindow.xaml.cs")));

        // res.Message used to be written straight into the status line.
        Assert.DoesNotContain(": res.Message;", source);
        Assert.Contains("res.MessageKey", source);
    }

    /* ------------------------------------------------------------- helpers */

    private static int CountPlaceholders(string template)
    {
        int n = 0;
        for (int i = 0; i < template.Length - 1; i++)
            if (template[i] == '{' && char.IsDigit(template[i + 1])) n++;
        return n;
    }

    private static IEnumerable<string> LocaleFiles()
    {
        string dir = FindRepoDirectory("locales");
        return Directory.GetFiles(dir, "*.json").OrderBy(f => f);
    }

    /// <summary>Finds a repository directory, not a file.</summary>
    private static string FindRepoDirectory(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 10 && dir != null; i++, dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, name);
            if (Directory.Exists(candidate)) return candidate;
            string nested = Path.Combine(dir.FullName, "mpv-opn", name);
            if (Directory.Exists(nested)) return nested;
        }
        throw new DirectoryNotFoundException($"could not find {name}");
    }

    private static Dictionary<string, string> ReadTranslations(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var result = new Dictionary<string, string>();
        if (doc.RootElement.TryGetProperty("translations", out var translations))
        {
            foreach (var prop in translations.EnumerateObject())
                result[prop.Name] = prop.Value.GetString() ?? "";
        }
        return result;
    }

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
}