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
        public void LoadTheme(string themeId)
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
                        return;
                    }
                }
                catch { /* fall through to the built-in defaults */ }
            }
            CurrentTheme = new ThemeModel();
            ThemeChanged?.Invoke();
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
        /// A theme id has to be usable as a plain file name. Anything containing a
        /// separator, a drive letter or a traversal segment is rejected.
        /// </summary>
        private static bool IsSafeThemeId(string? id)
        {
            if (string.IsNullOrEmpty(id) || id.Length > 64) return false;
            if (id.Contains("..") || id.Contains('/') || id.Contains('\\')) return false;
            if (id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
            return !Path.IsPathRooted(id);
        }

        /// <summary>Upper bound for a theme file; real themes are a few KB.</summary>
        private const int MaxThemeBytes = 4 * 1024 * 1024;

        /// <summary>Writes the current theme back out so it survives a restart.</summary>
        public (bool Success, string Message) SaveTheme(ThemeModel theme)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(theme.Id))
                    theme.Id = "custom_" + DateTime.Now.Ticks;

                Directory.CreateDirectory(_themesDir);
                string targetFile = Path.Combine(_themesDir, $"{theme.Id}.json");
                File.WriteAllText(targetFile, JsonSerializer.Serialize(theme, new JsonSerializerOptions { WriteIndented = true }));
                LoadTheme(theme.Id);
                return (true, $"Theme '{theme.Name}' saved.");
            }
            catch (Exception ex)
            {
                return (false, "Theme save failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Sets the in-memory theme without touching the disk. The UI uses this
        /// for the live editor, where the user saves explicitly.
        /// </summary>
        public void ApplyTheme(ThemeModel theme)
        {
            CurrentTheme = theme;
            ThemeChanged?.Invoke();
        }
    }
}
