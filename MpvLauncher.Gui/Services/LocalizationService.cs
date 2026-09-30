using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace MpvLauncher.Gui.Services
{
    /// <summary>One selectable language: its code and its own name.</summary>
    public class LanguageInfo
    {
        public string Code { get; set; } = "";
        public string Name { get; set; } = "";
    }

    /// <summary>
    /// Loads the UI translations from the JSON files in the languages folder.
    ///
    /// Each file is <c>{ language_code, language_name, translations: { key: text } }</c>.
    /// A key that is missing falls back to the key itself, which makes an
    /// incomplete pack visible on screen instead of silently blank.
    ///
    /// Language files also arrive from the network (ImportFromUrlAsync), so the
    /// code inside a file is treated as untrusted input: it becomes a file name,
    /// and it is validated before it is used as one.
    /// </summary>
    public class LocalizationService
    {
        private readonly string _localesDir;
        private Dictionary<string, string> _translations = new();
        public string CurrentLanguage { get; private set; } = "en";

        public event Action? LanguageChanged;

        /// <summary>Set when the language file could not be read (missing, malformed, etc).</summary>
        public string? LoadError { get; private set; }

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
            LoadError = null;

            // The code is turned into a file name, so it must not be able to
            // point outside the language folder.
            if (!IsSafeLanguageCode(code))
            {
                LoadError = $"'{code}' is not a valid language code";
                LanguageChanged?.Invoke();
                return;
            }

            string langFile = Path.Combine(_localesDir, $"{code}.json");
            if (!File.Exists(langFile))
            {
                LoadError = $"'{code}.json' not found in {_localesDir}";
            }
            else
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
                    else
                    {
                        LoadError = $"'{code}.json' has no 'translations' object";
                    }
                }
                catch (Exception ex)
                {
                    // A malformed file used to fail silently and fall back to the key names.
                    LoadError = $"'{code}.json' could not be parsed: {ex.Message}";
                }
            }

            LanguageChanged?.Invoke();
        }

        public string Get(string key, string fallback = "")
        {
            return _translations.TryGetValue(key, out var val) ? val : (string.IsNullOrEmpty(fallback) ? key : fallback);
        }

        /// <summary>
        /// Looks up a key and substitutes positional placeholders into it.
        ///
        /// Placeholders are {0}, {1}, ... in .NET order, and the arguments are
        /// formatted with the invariant culture so a number renders the same in
        /// every language. The values are paths, ids and counts; the sentence
        /// around them comes from the locale file, which is what lets word order
        /// differ per language.
        ///
        /// A translation whose placeholder count is wrong must not crash the
        /// dialog, so a formatting failure falls back to the unformatted text.
        /// </summary>
        public string Format(string key, string fallback, params object?[] args)
        {
            string template = Get(key, fallback);
            if (args == null || args.Length == 0) return template;

            try
            {
                return string.Format(CultureInfo.InvariantCulture, template, args);
            }
            catch (FormatException)
            {
                // Mismatched placeholders in the locale file: show the template
                // rather than losing the whole message.
                return template;
            }
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
                        // A malformed file must not be able to introduce a code
                        // that later resolves to a path outside this folder.
                        if (!IsSafeLanguageCode(code)) continue;
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
                // Only https: an http source can be rewritten in transit, and the
                // response is written to disk verbatim.
                if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri) ||
                    uri.Scheme != Uri.UriSchemeHttps)
                {
                    return (false, "Language packs must be fetched over https.");
                }

                using var client = new HttpClient();
                client.DefaultRequestHeaders.Add("User-Agent", "MPVLauncher/1.0");
                string json = await client.GetStringAsync(rawUrl);

                if (json.Length > MaxPackBytes)
                    return (false, $"Language file is too large (limit {MaxPackBytes / 1024} KB).");

                using var doc = JsonDocument.Parse(json);
                string? code = doc.RootElement.GetProperty("language_code").GetString();
                if (string.IsNullOrEmpty(code) || !IsSafeLanguageCode(code))
                {
                    // The code becomes a file name. Without this check a server
                    // could answer with "../../Startup/run" and have the app
                    // write an arbitrary file outside the language folder.
                    return (false, "Invalid language file: 'language_code' is not a valid language code.");
                }

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

        /// <summary>
        /// A language code has to be usable as a file name on every platform the
        /// app runs on, so only letters, digits, dash and underscore are allowed
        /// and the result is checked against the folder it will be written into.
        /// </summary>
        private static bool IsSafeLanguageCode(string code)
        {
            if (code.Length < 2 || code.Length > 16) return false;
            foreach (char c in code)
            {
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
                          (c >= '0' && c <= '9') || c == '-' || c == '_';
                if (!ok) return false;
            }
            // Belt and braces: the resolved path must stay inside the folder.
            return !code.Contains("..") && !code.Contains(Path.DirectorySeparatorChar);
        }

        /// <summary>Upper bound for a language pack, to cap memory use.</summary>
        private const int MaxPackBytes = 2 * 1024 * 1024;
    }
}
