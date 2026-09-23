using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Media;

namespace MpvLauncher.Gui.Services
{
    public class ThemeInfo
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Author { get; set; } = "";
        public bool HasBgImage { get; set; }
    }

    public class ThemeModel
    {
        public string Id { get; set; } = "dark";
        public string Name { get; set; } = "Dark";
        public string Author { get; set; } = "System";
        public ThemeBackground Background { get; set; } = new();
        public ThemeColors Colors { get; set; } = new();
        public ThemeOpacity Opacity { get; set; } = new();
    }

    public class ThemeBackground
    {
        public string Type { get; set; } = "solid";
        public string Color { get; set; } = "#12141a";
        public string Image { get; set; } = "";
        public double BlurRadius { get; set; } = 8.0;
        public string OverlayColor { get; set; } = "rgba(0, 0, 0, 0.4)";
    }

    public class ThemeColors
    {
        public string CardBg { get; set; } = "rgba(26, 30, 39, 0.85)";
        public string CardBorder { get; set; } = "rgba(255, 255, 255, 0.08)";
        public string Accent { get; set; } = "#6366f1";
        public string AccentHover { get; set; } = "#4f46e5";
        public string TextPrimary { get; set; } = "#f0f2f5";
        public string TextSecondary { get; set; } = "#9aa3b2";
        public string TextMuted { get; set; } = "#64748b";
        public string Success { get; set; } = "#10b981";
        public string Danger { get; set; } = "#ef4444";
    }

    public class ThemeOpacity
    {
        public double Cards { get; set; } = 0.85;
        public double Inputs { get; set; } = 0.80;
        public double Buttons { get; set; } = 1.0;
    }

    public class ThemeService
    {
        private readonly string _themesDir;
        public ThemeModel CurrentTheme { get; private set; } = new();
        public event Action? ThemeChanged;

        public ThemeService(string themesDir, string defaultThemeId = "dark")
        {
            _themesDir = themesDir;
            LoadTheme(defaultThemeId);
        }

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
                catch { }
            }
            CurrentTheme = new ThemeModel();
            ThemeChanged?.Invoke();
        }

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

        public async Task<(bool Success, string Message)> ImportFromUrlAsync(string rawUrl)
        {
            try
            {
                using var client = new HttpClient();
                client.DefaultRequestHeaders.Add("User-Agent", "MPVLauncher/1.0");
                string json = await client.GetStringAsync(rawUrl);

                using var doc = JsonDocument.Parse(json);
                string? id = doc.RootElement.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
                if (string.IsNullOrEmpty(id))
                    id = "custom_" + DateTime.Now.Ticks;

                string targetFile = Path.Combine(_themesDir, $"{id}.json");
                await File.WriteAllTextAsync(targetFile, json);

                LoadTheme(id);
                return (true, $"Theme '{id}' loaded.");
            }
            catch (Exception ex)
            {
                return (false, "Theme download failed: " + ex.Message);
            }
        }
    }
}
