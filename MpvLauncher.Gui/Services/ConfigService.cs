using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace MpvLauncher.Gui.Services
{
    public class AppConfig
    {
        public string Language { get; set; } = "en";
        public string Theme { get; set; } = "dark";
        public string MpvPath { get; set; } = "";
        public string CustomBackground { get; set; } = "";
        public double CustomOpacity { get; set; } = 0.85;
        public double CustomBlur { get; set; } = 8.0;
        public bool AlwaysOnTop { get; set; } = true;
        public string Geometry { get; set; } = "960x540";
        /// <summary>
        /// True until the first successful GUI setup (templates + browser extensions).
        /// </summary>
        public bool FirstRun { get; set; } = true;
        public List<HistoryItem> PlaybackHistory { get; set; } = new();
    }

    public class HistoryItem
    {
        public string Title { get; set; } = "";
        public string Url { get; set; } = "";
        public DateTime Timestamp { get; set; } = DateTime.Now;
    }

    public class ConfigService
    {
        public AppConfig Config { get; private set; }

        public ConfigService()
        {
            AppPaths.EnsureLayout();
            Config = LoadConfig();
            MergeHistoryFromFile();

            if (string.IsNullOrWhiteSpace(Config.MpvPath) || !File.Exists(Config.MpvPath))
            {
                if (File.Exists(AppPaths.MpvExe))
                {
                    Config.MpvPath = AppPaths.MpvExe;
                    Save();
                }
            }
        }

        private AppConfig LoadConfig()
        {
            if (File.Exists(AppPaths.ConfigFile))
            {
                try
                {
                    string json = File.ReadAllText(AppPaths.ConfigFile);
                    return JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
                }
                catch { }
            }
            return new AppConfig();
        }

        private void MergeHistoryFromFile()
        {
            if (!File.Exists(AppPaths.HistoryFile)) return;
            try
            {
                string json = File.ReadAllText(AppPaths.HistoryFile);
                var items = JsonSerializer.Deserialize<List<HistoryItem>>(json);
                if (items == null || items.Count == 0) return;
                if (Config.PlaybackHistory.Count == 0)
                    Config.PlaybackHistory = items;
            }
            catch { }
        }

        public void Save()
        {
            try
            {
                AppPaths.EnsureLayout();
                var options = new JsonSerializerOptions { WriteIndented = true };
                File.WriteAllText(AppPaths.ConfigFile, JsonSerializer.Serialize(Config, options));
                File.WriteAllText(AppPaths.HistoryFile, JsonSerializer.Serialize(Config.PlaybackHistory, options));
            }
            catch { }
        }

        public void AddHistory(string url, string title = "")
        {
            Config.PlaybackHistory.RemoveAll(x => x.Url == url);
            Config.PlaybackHistory.Insert(0, new HistoryItem
            {
                Url = url,
                Title = string.IsNullOrEmpty(title) ? url : title
            });
            if (Config.PlaybackHistory.Count > 50)
                Config.PlaybackHistory = Config.PlaybackHistory.GetRange(0, 50);
            Save();
        }

        public void ClearHistory()
        {
            Config.PlaybackHistory.Clear();
            Save();
        }

        /// <summary>
        /// Used by native-host mode (no GUI) to record URLs opened from the browser.
        /// </summary>
        public static void AddHistoryStatic(string url)
        {
            try
            {
                AppPaths.EnsureLayout();
                var items = new List<HistoryItem>();
                if (File.Exists(AppPaths.HistoryFile))
                {
                    items = JsonSerializer.Deserialize<List<HistoryItem>>(
                        File.ReadAllText(AppPaths.HistoryFile)) ?? new List<HistoryItem>();
                }
                else if (File.Exists(AppPaths.ConfigFile))
                {
                    var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(AppPaths.ConfigFile));
                    if (cfg?.PlaybackHistory != null)
                        items = cfg.PlaybackHistory;
                }

                items.RemoveAll(x => x.Url == url);
                items.Insert(0, new HistoryItem { Url = url, Title = url });
                if (items.Count > 50)
                    items = items.GetRange(0, 50);

                File.WriteAllText(AppPaths.HistoryFile,
                    JsonSerializer.Serialize(items, new JsonSerializerOptions { WriteIndented = true }));

                if (File.Exists(AppPaths.ConfigFile))
                {
                    var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(AppPaths.ConfigFile)) ?? new AppConfig();
                    cfg.PlaybackHistory = items;
                    File.WriteAllText(AppPaths.ConfigFile,
                        JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true }));
                }
            }
            catch { }
        }
    }
}
