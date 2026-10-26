// Original implementation, MIT licensed.

using System;
using System.Globalization;
using System.Windows.Media;

namespace MpvLauncher.Gui.Services
{
    /// <summary>
    /// Every brush the interface draws with, resolved together so a window can
    /// apply a whole theme in one pass.
    ///
    /// The alternative - reading colours at each call site - is what made the
    /// old theme support partial: a brush nobody remembered to update kept its
    /// hard-coded value and themes looked identical. One palette, one place to
    /// change it.
    /// </summary>
    public sealed class UiPalette
    {
        public Brush Window { get; init; } = Brushes.Transparent;
        public Brush Sidebar { get; init; } = Brushes.Transparent;
        public Brush Card { get; init; } = Brushes.Transparent;
        public Brush CardBorder { get; init; } = Brushes.Transparent;
        public Brush Hairline { get; init; } = Brushes.Transparent;
        public Brush Surface { get; init; } = Brushes.Transparent;
        public Brush Input { get; init; } = Brushes.Transparent;
        public Brush Popup { get; init; } = Brushes.Transparent;
        public Brush WindowBorder { get; init; } = Brushes.Transparent;
        public Brush TextPrimary { get; init; } = Brushes.White;
        public Brush TextSecondary { get; init; } = Brushes.Gray;
        public Brush TextMuted { get; init; } = Brushes.Gray;
        public Brush Accent { get; init; } = Brushes.MediumSlateBlue;
        public Brush AccentHover { get; init; } = Brushes.MediumSlateBlue;
        public Brush OnAccent { get; init; } = Brushes.White;
        public Brush Success { get; init; } = Brushes.SeaGreen;
        public Brush Warning { get; init; } = Brushes.Goldenrod;
        public Brush Danger { get; init; } = Brushes.IndianRed;
        public Brush SecondaryButton { get; init; } = Brushes.DimGray;
        public Brush DangerButton { get; init; } = Brushes.DarkRed;

        /// <summary>0..1 applied to the filled button styles.</summary>
        public double ButtonOpacity { get; init; } = 1.0;
    }

    /// <summary>
    /// Resolves a theme file plus the user's overrides into one palette, and
    /// keeps the result available to windows that build their UI in code.
    ///
    /// Precedence is: a non-empty override in the config, then the theme, then
    /// the built-in default. Theme files are allowed to be incomplete or to
    /// hold a colour nobody can parse, so every read below goes through
    /// <see cref="Parse(string?, Color)"/>.
    /// </summary>
    public static class UiTheme
    {
        private const string DefaultWindow = "#12141A";
        private const string DefaultCard = "#1E222D";
        private const string DefaultAccent = "#6366F1";
        private const string DefaultAccentHover = "#4F46E5";
        private const string DefaultTextPrimary = "#F0F2F5";
        private const string DefaultTextSecondary = "#9AA3B2";
        private const string DefaultTextMuted = "#64748B";
        private const string DefaultBorder = "#33FFFFFF";
        private const string DefaultSuccess = "#10B981";
        private const string DefaultWarning = "#F59E0B";
        private const string DefaultDanger = "#EF4444";
        private const string DefaultSecondaryButton = "#252A38";
        private const string DefaultDangerButton = "#3A1D22";
        private const string DefaultWindowBorder = "#2E384D";

        /// <summary>The palette in effect. Never null, never freed.</summary>
        public static UiPalette Current { get; private set; } = Resolve(new ThemeModel(), new AppConfig());

        /// <summary>
        /// Raised after <see cref="Apply"/> installs a new palette. Open
        /// dialogs listen so a theme change repaints them too.
        /// </summary>
        public static event Action? PaletteChanged;

        public static UiPalette Resolve(ThemeModel theme, AppConfig config)
        {
            theme ??= new ThemeModel();
            config ??= new AppConfig();
            var colors = theme.Colors ?? new ThemeColors();
            var background = theme.Background ?? new ThemeBackground();
            var opacity = theme.Opacity ?? new ThemeOpacity();

            // Surfaces keep the alpha the user chose, so the two opacity
            // sliders and the colour boxes control different things and do
            // not fight over one value.
            Color card = Parse(First(config.CustomCardColor, colors.CardBg), DefaultCard);
            card.A = Alpha(Opacity(config.CustomCardOpacity, opacity.Cards));

            Color sidebar = Parse(First(config.CustomSidebarColor, background.Color), DefaultWindow);
            sidebar.A = Alpha(Opacity(config.CustomSidebarOpacity, opacity.Cards));

            Color input = Colors.Black;
            input.A = Alpha(Opacity(config.CustomInputOpacity, opacity.Inputs));

            Color surface = Colors.Black;
            surface.A = (byte)Math.Clamp(input.A * 0.55, 0x08, 0x40);

            Color window = Parse(First(config.CustomBgColor, background.Color), DefaultWindow);
            Color cardBorder = Parse(First(config.CustomBorderColor, colors.CardBorder), DefaultBorder);
            Color accent = Parse(First(config.CustomAccentColor, colors.Accent), DefaultAccent);
            Color accentHover = Parse(colors.AccentHover, DefaultAccentHover);

            // The window outline is a thin frame: forcing it to a fixed alpha
            // keeps it visible when a theme ships a very transparent border.
            // The user's border colour wins here as well as on the cards -
            // without that the "Border" box in the appearance editor only
            // changed the inputs and left the outer frame on the theme colour.
            Color windowBorder = WithAlpha(cardBorder, (byte)Math.Max((int)cardBorder.A, 0x60));

            // Secondary buttons are a slightly lifted card. Deriving them from
            // the card colour is what makes them belong to the theme instead of
            // staying the same slate grey in all four.
            Color popup = Opaque(Parse(colors.CardBg, DefaultCard), 0xFA);
            Color secondaryButton = WithAlpha(Lighten(Opaque(Parse(colors.CardBg, DefaultSecondaryButton), 0xFF), 0.10), 0xF2);
            Color dangerButton = WithAlpha(Parse(colors.Danger, DefaultDanger), 0x33);

            return new UiPalette
            {
                Window = Freeze(window),
                Sidebar = Freeze(sidebar),
                Card = Freeze(card),
                CardBorder = Freeze(cardBorder),
                Hairline = Freeze(WithAlpha(Colors.White, 0x1F)),
                Surface = Freeze(surface),
                Input = Freeze(input),
                Popup = Freeze(popup),
                WindowBorder = Freeze(windowBorder),
                TextPrimary = Freeze(Parse(First(config.CustomTextPrimary, colors.TextPrimary), DefaultTextPrimary)),
                TextSecondary = Freeze(Parse(First(config.CustomTextSecondary, colors.TextSecondary), DefaultTextSecondary)),
                TextMuted = Freeze(Parse(colors.TextMuted, DefaultTextMuted)),
                Accent = Freeze(accent),
                AccentHover = Freeze(accentHover),
                OnAccent = Freeze(Brushes.White),
                Success = Freeze(Parse(colors.Success, DefaultSuccess)),
                Warning = Freeze(Parse(colors.Warning, DefaultWarning)),
                Danger = Freeze(Parse(colors.Danger, DefaultDanger)),
                SecondaryButton = Freeze(secondaryButton),
                DangerButton = Freeze(dangerButton),
                ButtonOpacity = Math.Clamp(Opacity(config.CustomButtonOpacity, opacity.Buttons), 0.2, 1.0)
            };
        }

        /// <summary>Resolves and remembers a palette, ignoring a load failure.</summary>
        public static UiPalette Apply(ThemeModel theme, AppConfig config)
        {
            Current = Resolve(theme, config);
            PaletteChanged?.Invoke();
            return Current;
        }

        /// <summary>
        /// Copies a theme's palette into the config overrides. Called when the
        /// user picks a theme: without it the previous theme's overrides would
        /// win forever and switching themes would only change the wallpaper.
        /// </summary>
        public static void AdoptTheme(ThemeModel theme, AppConfig config)
        {
            if (theme?.Colors == null) return;
            var colors = theme.Colors;
            var opacity = theme.Opacity ?? new ThemeOpacity();

            // The custom background URL wins over the theme image, so a theme
            // change has to clear it; otherwise every gradient theme would keep
            // showing the wallpaper the user picked for the previous one.
            config.CustomBackground = "";

            config.CustomBgColor = ToHex(Parse(theme.Background?.Color, DefaultWindow));
            config.CustomSidebarColor = ToHex(Parse(theme.Background?.Color, DefaultWindow));
            config.CustomCardColor = ToHex(Parse(colors.CardBg, DefaultCard));
            config.CustomAccentColor = ToHex(Parse(colors.Accent, DefaultAccent));
            config.CustomBorderColor = ToHex(Parse(colors.CardBorder, DefaultBorder), keepAlpha: true);
            config.CustomTextPrimary = ToHex(Parse(colors.TextPrimary, DefaultTextPrimary));
            config.CustomTextSecondary = ToHex(Parse(colors.TextSecondary, DefaultTextSecondary));
            config.CustomLogoColor = string.IsNullOrWhiteSpace(colors.Logo)
                ? ToHex(Parse(colors.Accent, DefaultAccent))
                : ToHex(Parse(colors.Logo, DefaultAccent));

            config.CustomCardOpacity = Math.Clamp(opacity.Cards <= 0 ? 0.85 : opacity.Cards, 0.10, 1.0);
            config.CustomInputOpacity = Math.Clamp(opacity.Inputs <= 0 ? 0.20 : opacity.Inputs, 0.05, 1.0);
            config.CustomButtonOpacity = Math.Clamp(opacity.Buttons <= 0 ? 1.0 : opacity.Buttons, 0.20, 1.0);
            config.CustomSidebarOpacity = Math.Clamp(config.CustomCardOpacity, 0.10, 1.0);
        }

        /// <summary>Parses "#RRGGBB", "#AARRGGBB" and "rgba(r, g, b, a)".</summary>
        public static Color Parse(string? value, Color fallback)
        {
            if (string.IsNullOrWhiteSpace(value)) return fallback;
            string text = value.Trim();

            if (text.StartsWith("rgba", StringComparison.OrdinalIgnoreCase))
            {
                int open = text.IndexOf('(');
                int close = text.LastIndexOf(')');
                if (open < 0 || close <= open) return fallback;

                string[] parts = text[(open + 1)..close].Split(',');
                if (parts.Length < 3) return fallback;
                if (!byte.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out byte r) ||
                    !byte.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out byte g) ||
                    !byte.TryParse(parts[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out byte b))
                {
                    return fallback;
                }

                double a = 1.0;
                if (parts.Length > 3)
                {
                    // CSS allows both 0.5 and 50% for alpha.
                    string raw = parts[3].Trim();
                    if (raw.EndsWith('%') && double.TryParse(raw[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out double pct))
                    {
                        a = pct / 100.0;
                    }
                    else if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out a))
                    {
                        a = 1.0;
                    }
                }

                return Color.FromArgb(Alpha(a), r, g, b);
            }

            try
            {
                if (text.StartsWith('#'))
                {
                    if (text.Length == 7)
                    {
                        return Color.FromRgb(
                            byte.Parse(text[1..3], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                            byte.Parse(text[3..5], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                            byte.Parse(text[5..7], NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                    }
                    if (text.Length == 9)
                    {
                        return Color.FromArgb(
                            byte.Parse(text[1..3], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                            byte.Parse(text[3..5], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                            byte.Parse(text[5..7], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                            byte.Parse(text[7..9], NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                    }
                }

                if (ColorConverter.ConvertFromString(text) is Color parsed) return parsed;
            }
            catch
            {
                // A bad colour must never take the window down.
            }

            return fallback;
        }

        public static Color Parse(string? value, string fallback) => Parse(value, Parse(fallback, Colors.Transparent));

        /// <summary>"#RRGGBB", or "#AARRGGBB" when the alpha is meaningful.</summary>
        public static string ToHex(Color color, bool keepAlpha = false)
            => keepAlpha && color.A != 0xFF
                ? $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}"
                : $"#{color.R:X2}{color.G:X2}{color.B:X2}";

        private static string First(string? preferred, string? fallback)
            => !string.IsNullOrWhiteSpace(preferred) ? preferred! : fallback ?? "";

        private static double Opacity(double configured, double themed)
            => configured > 0 ? configured : (themed > 0 ? themed : 1.0);

        private static byte Alpha(double value)
            => (byte)Math.Clamp(Math.Round(value * 255), 8, 255);

        private static Color WithAlpha(Color color, byte alpha)
            => Color.FromArgb(alpha, color.R, color.G, color.B);

        /// <summary>Forces a solid colour so a popup does not inherit transparency.</summary>
        private static Color Opaque(Color color, byte alpha)
            => WithAlpha(color, alpha);

        private static Color Lighten(Color color, double amount)
            => Color.FromRgb(
                (byte)Math.Clamp(color.R + 255 * amount, 0, 255),
                (byte)Math.Clamp(color.G + 255 * amount, 0, 255),
                (byte)Math.Clamp(color.B + 255 * amount, 0, 255));

        private static Brush Freeze(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        private static Brush Freeze(Brush brush)
        {
            if (brush.CanFreeze) brush.Freeze();
            return brush;
        }
    }
}
