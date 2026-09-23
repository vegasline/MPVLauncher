using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace MpvLauncher.Gui.Services
{
    public class LanguageInfo
    {
        public string Code { get; set; } = "";
        public string Name { get; set; } = "";
    }

    public class LocalizationService
    {
        private readonly string _localesDir;
        private Dictionary<string, string> _translations = new();
        public string CurrentLanguage { get; private set; } = "en";

        public event Action? LanguageChanged;

        public LocalizationService(string localesDir, string defaultLang = "en")
        {
            _localesDir = localesDir;
            CurrentLanguage = defaultLang;
            LoadLanguage(CurrentLanguage);
        }

        public void LoadLanguage(string code)
        {
            CurrentLanguage = code;
            _translations.Clear();

            string langFile = Path.Combine(_localesDir, $"{code}.json");
            if (File.Exists(langFile))
            {
                try
                {
                    string json = File.ReadAllText(langFile);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("translations", out var transElement))
                    {
                        foreach (var prop in transElement.EnumerateObject())
                        {
                            _translations[prop.Name] = prop.Value.GetString() ?? prop.Name;
                        }
                    }
                }
                catch { }
            }

            LanguageChanged?.Invoke();
        }

        public string Get(string key, string fallback = "")
        {
            return _translations.TryGetValue(key, out var val) ? val : (string.IsNullOrEmpty(fallback) ? key : fallback);
        }

        public List<LanguageInfo> GetAvailableLanguages()
        {
            var list = new List<LanguageInfo>();
            if (Directory.Exists(_localesDir))
            {
                foreach (var file in Directory.GetFiles(_localesDir, "*.json"))
                {
                    try
                    {
                        string json = File.ReadAllText(file);
                        using var doc = JsonDocument.Parse(json);
                        string code = doc.RootElement.GetProperty("language_code").GetString() ?? Path.GetFileNameWithoutExtension(file);
                        string name = doc.RootElement.GetProperty("language_name").GetString() ?? code.ToUpper();
                        list.Add(new LanguageInfo { Code = code, Name = name });
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
                string? code = doc.RootElement.GetProperty("language_code").GetString();
                if (string.IsNullOrEmpty(code))
                    return (false, "Invalid language file: missing 'language_code'.");

                string targetFile = Path.Combine(_localesDir, $"{code}.json");
                await File.WriteAllTextAsync(targetFile, json);

                LoadLanguage(code);
                return (true, $"Language pack '{code}' loaded.");
            }
            catch (Exception ex)
            {
                return (false, "Language download failed: " + ex.Message);
            }
        }
    }
}
