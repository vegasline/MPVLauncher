using System.Text.RegularExpressions;

namespace MpvLauncher.Gui.Tests;

/// <summary>
/// Holds two agreements the window makes in two places.
///
/// A XAML button names its target control with Tag="TxtColorFoo" and the
/// code-behind matches that string against a list of the same names. Nothing
/// connects the two lists, so a button can name a control the switch does not
/// know: the logo colour swatch had eight buttons and seven arms, and the
/// eighth button opened nothing at all. No build error, no test failure, no
/// log line - the picker simply did nothing.
///
/// The status line is the other one. Twenty-nine places write it, and the
/// localization sweep ended by writing "Ready" over whatever was there - so
/// changing the language wiped "Version 1.9.12 is ready to install", a
/// download error, or a relaunch failure. The status line is transient state,
/// not a label: whoever has something to say sets it, and a translation pass
/// has nothing to say about it.
///
/// These read the source rather than driving the window, because constructing
/// a WPF window is not something a headless test can do. What they pin is a
/// naming convention across two files, which is exactly the thing a runtime
/// test could not see either.
/// </summary>
public class MainWindowContractTests
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

    private static string Xaml => File.ReadAllText(RepoFile(Path.Combine(
        "MpvLauncher.Gui", "MainWindow.xaml")));

    private static string Code => File.ReadAllText(RepoFile(Path.Combine(
        "MpvLauncher.Gui", "MainWindow.xaml.cs")));

    /// <summary>Every control a colour picker button claims to edit.</summary>
    private static string[] TargetsNamedInXaml() =>
        Regex.Matches(Xaml, "Tag=\"(TxtColor[0-9A-Za-z_]+)\"")
             .Select(m => m.Groups[1].Value)
             .Distinct()
             .OrderBy(s => s, StringComparer.Ordinal)
             .ToArray();

    /// <summary>Every control the code-behind knows how to edit.</summary>
    private static string[] TargetsKnownToCode() =>
        Regex.Matches(Code, "\"(TxtColor[0-9A-Za-z_]+)\"\\s*=>")
             .Select(m => m.Groups[1].Value)
             .Distinct()
             .OrderBy(s => s, StringComparer.Ordinal)
             .ToArray();

    [Fact]
    public void Every_colour_picker_button_has_a_target_the_code_knows()
    {
        var named = TargetsNamedInXaml();
        var known = TargetsKnownToCode().ToHashSet(StringComparer.Ordinal);

        var missing = named.Where(n => !known.Contains(n)).ToArray();

        Assert.True(missing.Length == 0,
            "these buttons name a control the switch cannot reach, so the picker opens nothing: "
            + string.Join(", ", missing));
    }

    [Fact]
    public void The_code_never_knows_a_target_the_xaml_does_not_name()
    {
        var named = TargetsNamedInXaml().ToHashSet(StringComparer.Ordinal);

        var extra = TargetsKnownToCode().Where(k => !named.Contains(k)).ToArray();

        Assert.True(extra.Length == 0,
            "these arms can never be reached, because no button names them: "
            + string.Join(", ", extra));
    }

    [Fact]
    public void The_xaml_actually_declares_colour_pickers()
    {
        // Guards the two tests above from passing because the pattern stopped
        // matching - an empty list agrees with an empty list.
        Assert.True(TargetsNamedInXaml().Length >= 7,
            $"found only {TargetsNamedInXaml().Length} picker targets");
    }

    /// <summary>
    /// The status line must not be written by the translation sweep. It is
    /// overwritten on load with "Ready", which is where that state belongs.
    /// </summary>
    [Fact]
    public void The_translation_sweep_does_not_reset_the_status_line()
    {
        string sweep = ExtractMethod("UpdateLocalization");

        Assert.DoesNotMatch(@"TxtStatus\.Text\s*=", sweep);
    }

    [Fact]
    public void The_window_still_sets_an_initial_status_on_load()
    {
        // Removing the sweep's assignment must not leave the line blank on
        // startup, so the initial value has to exist somewhere.
        Assert.Matches(@"TxtStatus\.Text\s*=\s*_locService\.Get\(\s*""status_ready""", Code);
    }

    /// <summary>
    /// Pulls one method body out of the source by brace counting, so the check
    /// is about that method rather than about the whole file.
    /// </summary>
    private static string ExtractMethod(string name)
    {
        string code = Code;
        int signature = code.IndexOf("void " + name, StringComparison.Ordinal);
        if (signature < 0) signature = code.IndexOf(name + "(", StringComparison.Ordinal);
        Assert.True(signature >= 0, $"could not find {name}");

        int open = code.IndexOf('{', signature);
        Assert.True(open > 0, $"{name} has no body");

        int depth = 0;
        for (int i = open; i < code.Length; i++)
        {
            if (code[i] == '{') depth++;
            else if (code[i] == '}' && --depth == 0)
                return code[open..(i + 1)];
        }
        throw new InvalidOperationException($"{name} is not closed");
    }
}