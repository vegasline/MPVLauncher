// Original implementation, MIT licensed.

using System.Windows.Media;
using MpvLauncher.Gui.Services;

namespace MpvLauncher.Gui.Tests;

public class UiThemeTests
{
    private static ThemeModel Nord()
    {
        return new ThemeModel
        {
            Id = "nord",
            Background = new ThemeBackground { Color = "#2e3440", OverlayColor = "rgba(46, 52, 64, 0.75)" },
            Colors = new ThemeColors
            {
                CardBg = "rgba(59, 66, 82, 0.70)",
                CardBorder = "rgba(136, 192, 208, 0.3)",
                Accent = "#88c0d0",
                AccentHover = "#81a1c1",
                TextPrimary = "#eceff4",
                TextSecondary = "#d8dee9",
                TextMuted = "#4c566a",
                Success = "#a3be8c",
                Warning = "#ebcb8b",
                Danger = "#bf616a"
            },
            Opacity = new ThemeOpacity { Cards = 0.70, Inputs = 0.60, Buttons = 0.90 }
        };
    }

    private static AppConfig NoOverrides()
    {
        var config = new AppConfig
        {
            CustomBgColor = "",
            CustomSidebarColor = "",
            CustomCardColor = "",
            CustomAccentColor = "",
            CustomBorderColor = "",
            CustomTextPrimary = "",
            CustomTextSecondary = "",
            CustomLogoColor = ""
        };
        return config;
    }

    private static Color Colour(Brush brush) => ((SolidColorBrush)brush).Color;

    [Fact]
    public void A_theme_without_overrides_supplies_every_colour()
    {
        var palette = UiTheme.Resolve(Nord(), NoOverrides());

        Assert.Equal(Color.FromRgb(0x88, 0xC0, 0xD0), Colour(palette.Accent));
        Assert.Equal(Color.FromRgb(0xEC, 0xEF, 0xF4), Colour(palette.TextPrimary));
        Assert.Equal(Color.FromRgb(0xD8, 0xDE, 0xE9), Colour(palette.TextSecondary));
        Assert.Equal(Color.FromRgb(0x4C, 0x56, 0x6A), Colour(palette.TextMuted));
        Assert.Equal(Color.FromRgb(0xA3, 0xBE, 0x8C), Colour(palette.Success));
        Assert.Equal(Color.FromRgb(0xBF, 0x61, 0x6A), Colour(palette.Danger));
    }

    [Fact]
    public void Card_opacity_comes_from_the_slider_and_the_colour_from_the_theme()
    {
        var config = NoOverrides();
        config.CustomCardOpacity = 0.42;

        var palette = UiTheme.Resolve(Nord(), config);
        Color card = Colour(palette.Card);

        Assert.Equal(Color.FromRgb(0x3B, 0x42, 0x52), Color.FromRgb(card.R, card.G, card.B));
        Assert.Equal(107, card.A); // 0.42 * 255, rounded
    }

    [Fact]
    public void Adopting_a_theme_replaces_the_previous_overrides()
    {
        // This is the bug that made theme switching invisible: the old
        // theme's overrides won over every theme file loaded afterwards.
        var config = new AppConfig
        {
            CustomAccentColor = "#FB7185",
            CustomCardColor = "#000000",
            CustomTextPrimary = "#FFFFFF",
            CustomTextSecondary = "#FFFFFF"
        };

        UiTheme.AdoptTheme(Nord(), config);

        Assert.Equal("#88C0D0", config.CustomAccentColor);
        Assert.Equal("#3B4252", config.CustomCardColor);
        Assert.Equal("#ECEFF4", config.CustomTextPrimary);
        Assert.Equal("#D8DEE9", config.CustomTextSecondary);
        Assert.Equal(0.70, config.CustomCardOpacity);
        Assert.Equal(0.60, config.CustomInputOpacity);
        Assert.Equal(0.90, config.CustomButtonOpacity);
        Assert.DoesNotContain("rgba", config.CustomCardColor, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Adopting_a_theme_keeps_a_transparent_border_transparent()
    {
        var config = new AppConfig();

        UiTheme.AdoptTheme(Nord(), config);

        Assert.Equal("#4C88C0D0", config.CustomBorderColor);
    }

    [Theory]
    [InlineData("rgba(26, 30, 39, 0.95)", 26, 30, 39, 242)]
    [InlineData("#12141A", 0x12, 0x14, 0x1A, 255)]
    [InlineData("#8012141A", 0x12, 0x14, 0x1A, 128)]
    [InlineData("nonsense", 0xAB, 0xCD, 0xEF, 255)]
    [InlineData("", 0xAB, 0xCD, 0xEF, 255)]
    public void Parse_reads_css_hex_and_falls_back(string value, byte r, byte g, byte b, byte a)
    {
        Color fallback = Color.FromArgb(0xFF, 0xAB, 0xCD, 0xEF);

        Color parsed = UiTheme.Parse(value, fallback);

        Assert.Equal(Color.FromArgb(a, r, g, b), parsed);
    }
}
