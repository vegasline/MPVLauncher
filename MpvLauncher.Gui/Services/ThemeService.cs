using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json.Serialization;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Media;

namespace MpvLauncher.Gui.Services
{
    /// <summary>
    /// One entry for the theme picker. The metadata is read out of the file
    /// directly rather than by deserialising it, so a malformed theme cannot
    /// break the list.
    /// </summary>
    public class ThemeInfo
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Author { get; set; } = "";
        public bool HasBgImage { get; set; }
    }

    /// <summary>
    /// A complete theme: the palette the UI binds to plus the translucency the
    /// window chrome uses. The JSON names are exactly what a theme file on disk
    /// contains, so they are spelled out instead of inferred from the property.
    /// </summary>
    public class ThemeModel
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "dark";
        [JsonPropertyName("name")]
        public string Name { get; set; } = "Dark";
        [JsonPropertyName("author")]
        public string Author { get; set; } = "System";
        [JsonPropertyName("background")]
        public ThemeBackground Background { get; set; } = new();
        [JsonPropertyName("colors")]
        public ThemeColors Colors { get; set; } = new();
        [JsonPropertyName("opacity")]
        public ThemeOpacity Opacity { get; set; } = new();
    }

    /// <summary>
    /// Background of a theme: either a flat colour or an image, with an overlay
    /// and blur applied when an image is used. Colour strings are CSS-style
    /// "rgba(...)" values that the XAML converter understands.
    /// </summary>
    public class ThemeBackground
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "solid";
        [JsonPropertyName("color")]
        public string Color { get; set; } = "#12141a";
        [JsonPropertyName("image")]
        public string Image { get; set; } = "";
        [JsonPropertyName("blur_radius")]
        public double BlurRadius { get; set; } = 8.0;
        [JsonPropertyName("overlay_color")]
        public string OverlayColor { get; set; } = "rgba(0, 0, 0, 0.4)";

        /// <summary>
        /// Optional background gradient, painted instead of an image. Themes
        /// that want artwork without shipping a binary can use "type":
        /// "gradient" with two or more stops; the angle is in degrees, 90 being
        /// top to bottom.
        /// </summary>
        [JsonPropertyName("gradient")]
        public List<string> Gradient { get; set; } = new();
        [JsonPropertyName("angle")]
        public double Angle { get; set; } = 135.0;
    }

    /// <summary>Palette entries bound to the UI by name.</summary>
    public class ThemeColors
    {
        [JsonPropertyName("card_bg")]
        public string CardBg { get; set; } = "rgba(26, 30, 39, 0.85)";
        [JsonPropertyName("card_border")]
        public string CardBorder { get; set; } = "rgba(255, 255, 255, 0.08)";
        [JsonPropertyName("accent")]
        public string Accent { get; set; } = "#6366f1";
        [JsonPropertyName("accent_hover")]
        public string AccentHover { get; set; } = "#4f46e5";
        [JsonPropertyName("logo")]
        public string Logo { get; set; } = "";
        [JsonPropertyName("text_primary")]
        public string TextPrimary { get; set; } = "#f0f2f5";
        [JsonPropertyName("text_secondary")]
        public string TextSecondary { get; set; } = "#9aa3b2";
        [JsonPropertyName("text_muted")]
        public string TextMuted { get; set; } = "#64748b";
        [JsonPropertyName("success")]
        public string Success { get; set; } = "#10b981";
        [JsonPropertyName("warning")]
        public string Warning { get; set; } = "#f59e0b";
        [JsonPropertyName("danger")]
        public string Danger { get; set; } = "#ef4444";
    }

    /// <summary>
    /// Per-surface alpha values, kept separate from the palette so a theme can
    /// be re-tuned for translucency without touching its colours.
    /// </summary>
    public class ThemeOpacity
    {
        [JsonPropertyName("cards")]
        public double Cards { get; set; } = 0.85;
        [JsonPropertyName("inputs")]
        public double Inputs { get; set; } = 0.80;
        [JsonPropertyName("buttons")]
        public double Buttons { get; set; } = 1.0;
    }

    /// <summary>
    /// Reads, lists and writes the theme files under %APPDATA%\MPVLauncher\themes.
    /// </summary>
    public class ThemeService
    {
        private readonly string _themesDir;

        /// <summary>The theme currently in effect. Never null; defaults to "dark".</summary>
        public ThemeModel CurrentTheme { get; private set; } = new();

        /// <summary>
        /// Raised after any change so the window can rebind its brushes without
        /// polling. Handlers must not throw: an exception here would leave the
        /// UI half-styled.
        /// </summary>
        public event Action? ThemeChanged;

        public ThemeService(string themesDir, string defaultThemeId = "dark")
        {
            _themesDir = themesDir;
            LoadTheme(defaultThemeId);
        }

        /// <summary>
        /// Loads a theme by id, falling back to the built-in defaults when the
        /// file is missing or malformed. A bad theme must never stop the app from
        /// starting, which is why the failure path is silent and just resets.
        /// </summary>
        public ThemeModel LoadTheme(string themeId)
        {
            string file = Path.Combine(_themesDir, $"{themeId}.json");
            if (File.Exists(file))
            {
                try
                {
                    string json = File.ReadAllText(file);
                    var model = JsonSerializer.Deserialize<ThemeModel>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (model != null)
                    {
                        CurrentTheme = model;
                        ThemeChanged?.Invoke();
                        return model;
                    }
                }
                catch { /* fall through to the built-in defaults */ }
            }
            CurrentTheme = new ThemeModel();
            ThemeChanged?.Invoke();
            return CurrentTheme;
        }

        /// <summary>
        /// Lists every theme file. Unparseable files are skipped rather than
        /// aborting the list, so one bad download does not hide the rest.
        /// </summary>
        public List<ThemeInfo> GetAvailableThemes()
        {
            var list = new List<ThemeInfo>();
            if (Directory.Exists(_themesDir))
            {
                foreach (var file in Directory.GetFiles(_themesDir, "*.json"))
                {
                    try
                    {
                        string json = File.ReadAllText(file);
                        using var doc = JsonDocument.Parse(json);
                        string id = doc.RootElement.GetProperty("id").GetString() ?? Path.GetFileNameWithoutExtension(file);
                        string name = doc.RootElement.GetProperty("name").GetString() ?? id;
                        string author = doc.RootElement.TryGetProperty("author", out var a) ? a.GetString() ?? "" : "";
                        bool hasImg = doc.RootElement.TryGetProperty("background", out var bg) && bg.TryGetProperty("image", out var img) && !string.IsNullOrEmpty(img.GetString());
                        
                        list.Add(new ThemeInfo { Id = id, Name = name, Author = author, HasBgImage = hasImg });
                    }
                    catch { }
                }
            }
            return list;
        }

        /// <summary>
        /// Downloads a theme file and stores it. The id inside the document
        /// chooses the file name, which is why it is validated below.
        /// </summary>
        public async Task<(bool Success, string Message)> ImportFromUrlAsync(string rawUrl)
        {
            try
            {
                // The response is written to disk verbatim and its "id" becomes a
                // file name, so both the transport and the id are checked first.
                if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri) ||
                    uri.Scheme != Uri.UriSchemeHttps)
                {
                    return (false, "Themes must be fetched over https.");
                }

                using var client = new HttpClient();
                client.DefaultRequestHeaders.Add("User-Agent", "MPVLauncher/1.0");
                string json = await client.GetStringAsync(rawUrl);

                if (json.Length > MaxThemeBytes)
                    return (false, $"Theme file is too large (limit {MaxThemeBytes / 1024} KB).");

                using var doc = JsonDocument.Parse(json);
                string? id = doc.RootElement.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;

                // Without this a server could answer with id
                // "../../Windows/System32/drivers" and the app would write there.
                string targetId = IsSafeThemeId(id) ? id! : "custom_" + DateTime.Now.Ticks;

                string targetFile = Path.Combine(_themesDir, $"{targetId}.json");
                await File.WriteAllTextAsync(targetFile, json);

                LoadTheme(targetId);
                return (true, $"Theme '{targetId}' loaded.");
            }
            catch (Exception ex)
            {
                return (false, "Theme download failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Writes a theme the user built in the appearance editor. The file
        /// lands next to the shipped ones so it shows up in the picker on the
        /// next refresh, and the id is derived from the name so a second save
        /// with the same name overwrites rather than piling up duplicates.
        /// </summary>
        public string SaveTheme(string name, ThemeModel model)
        {
            string baseId = Slug(name);
            if (string.IsNullOrEmpty(baseId)) baseId = "custom_" + DateTime.Now.Ticks;
            if (!IsSafeThemeId(baseId)) baseId = "custom_" + DateTime.Now.Ticks;

            model.Id = baseId;
            model.Name = string.IsNullOrWhiteSpace(name) ? baseId : name.Trim();

            Directory.CreateDirectory(_themesDir);
            string json = JsonSerializer.Serialize(model, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(_themesDir, $"{baseId}.json"), json);

            return baseId;
        }

        /// <summary>"Neon Sakura" -> "neon_sakura", for a file name.</summary>
        public static string Slug(string name)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char c in (name ?? "").Trim().ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(c)) sb.Append(c);
                else if (c is ' ' or '-' or '_' && sb.Length > 0 && sb[^1] != '_') sb.Append('_');
            }
            return sb.ToString().Trim('_');
        }

        /// <summary>
        /// A theme file that describes what is on screen right now: the
        /// resolved palette plus the background, opacity and radius values.
        /// </summary>
        public ThemeModel CaptureCurrent(string name, AppConfig config)
        {
            var palette = UiTheme.Current;
            var theme = CurrentTheme ?? new ThemeModel();
            var background = theme.Background ?? new ThemeBackground();

            // The wallpaper on screen is either the one the user picked or the
            // one the active theme brought with it; whichever it is has to be
            // written into the copy, or saving an image theme loses its art.
            string image = !string.IsNullOrEmpty(config.CustomBackground)
                ? config.CustomBackground
                : background.Image ?? "";

            // The logo can be recoloured independently of the accent, so the
            // override has to win here as well, or the copy would come back
            // with the accent colour where the user's pick used to be.
            string logo = !string.IsNullOrWhiteSpace(config.CustomLogoColor)
                ? config.CustomLogoColor
                : theme.Colors?.Logo ?? "";

            Color logoColor = string.IsNullOrWhiteSpace(logo)
                ? ToColorOf(palette.Accent, "#6366F1")
                : UiTheme.Parse(logo, ToColorOf(palette.Accent, "#6366F1"));

            return new ThemeModel
            {
                Id = Slug(name),
                Name = name,
                Author = "Custom",
                Background = new ThemeBackground
                {
                    Type = string.IsNullOrEmpty(image) ? background.Type : "image",
                    Color = UiTheme.ToHex(ToColorOf(palette.Window, "#12141A")),
                    Image = image,
                    BlurRadius = config.CustomBlur,
                    OverlayColor = background.OverlayColor,
                    Gradient = string.IsNullOrEmpty(image) ? background.Gradient : new(),
                    Angle = background.Angle
                },
                Colors = new ThemeColors
                {
                    CardBg = UiTheme.ToHex(ToColorOf(palette.Card, "#1E222D")),
                    CardBorder = UiTheme.ToHex(ToColorOf(palette.CardBorder, "#33FFFFFF"), keepAlpha: true),
                    Accent = UiTheme.ToHex(ToColorOf(palette.Accent, "#6366F1")),
                    AccentHover = UiTheme.ToHex(ToColorOf(palette.AccentHover, "#4F46E5")),
                    Logo = UiTheme.ToHex(logoColor),
                    TextPrimary = UiTheme.ToHex(ToColorOf(palette.TextPrimary, "#F0F2F5")),
                    TextSecondary = UiTheme.ToHex(ToColorOf(palette.TextSecondary, "#9AA3B2")),
                    TextMuted = UiTheme.ToHex(ToColorOf(palette.TextMuted, "#64748B")),
                    Success = UiTheme.ToHex(ToColorOf(palette.Success, "#10B981")),
                    Warning = UiTheme.ToHex(ToColorOf(palette.Warning, "#F59E0B")),
                    Danger = UiTheme.ToHex(ToColorOf(palette.Danger, "#EF4444"))
                },
                Opacity = new ThemeOpacity
                {
                    Cards = config.CustomCardOpacity,
                    Inputs = config.CustomInputOpacity,
                    Buttons = config.CustomButtonOpacity
                }
            };
        }

        private static Color ToColorOf(Brush brush, string fallback)
            => brush is SolidColorBrush solid ? solid.Color : UiTheme.Parse(fallback, Colors.Transparent);

        private static bool IsSafeThemeId(string? id)
        {
            if (string.IsNullOrEmpty(id) || id.Length > 64) return false;
            if (id.Contains("..") || id.Contains('/') || id.Contains('\\')) return false;
            if (id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
            return !Path.IsPathRooted(id);
        }

        /// <summary>Upper bound for a theme file; real themes are a few KB.</summary>
        private const int MaxThemeBytes = 4 * 1024 * 1024;
    }
}
