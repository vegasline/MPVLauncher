using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace MpvLauncher.Gui.Services
{
    /// <summary>
    /// Everything the user can configure, persisted as JSON in
    /// %APPDATA%\MPVLauncher\settings\config.json.
    ///
    /// Property names are the on-disk format, so renaming one silently discards
    /// the user's saved value. Defaults live on the properties themselves, which
    /// also covers keys added by a newer version: System.Text.Json leaves a
    /// missing property at its initialised default.
    /// </summary>
    public class AppConfig
    {
        /// <summary>Locale code, matched against a file in the languages folder.</summary>
        public string Language { get; set; } = "en";

        /// <summary>Theme id, matched against a file in the themes folder.</summary>
        public string Theme { get; set; } = "dark";

        /// <summary>
        /// Absolute path to mpv.exe. Empty means "not chosen yet"; the launcher
        /// falls back to probing the usual install locations.
        /// </summary>
        public string MpvPath { get; set; } = "";

        /// <summary>Path or URL of a user-supplied window background.</summary>
        public string CustomBackground { get; set; } = "";

        /// <summary>Global window opacity, 0..1.</summary>
        public double CustomOpacity { get; set; } = 0.85;

        /// <summary>Blur radius applied to the window background.</summary>
        public double CustomBlur { get; set; } = 8.0;

        /// <summary>Adds --ontop to every mpv launch.</summary>
        public bool AlwaysOnTop { get; set; } = true;

        /// <summary>mpv --geometry value, for example "960x540".</summary>
        public string Geometry { get; set; } = "960x540";

        /// <summary>
        /// mpv --force-window mode: "immediate" opens a window straight away,
        /// "yes" waits for the first frame and "no" never opens one.
        /// </summary>
        public string ForceWindow { get; set; } = "immediate";

        /// <summary>mpv --profile name, resolved from mpv's own config folder.</summary>
        public string MpvProfile { get; set; } = "";

        /// <summary>
        /// Extra options appended to every launch. This is the user's own command
        /// line, so it is not escaped or filtered the way page-supplied values are.
        /// </summary>
        public string ExtraArgs { get; set; } = "";

        // ---- Appearance overrides, used by the live theme editor ----
        // Each field mirrors one brush in the window. They are stored
        // individually rather than as a ThemeModel so an override survives a
        // theme switch only when the user chose to keep it.
        public double WindowCornerRadius { get; set; } = 8.0;
        public double CardCornerRadius { get; set; } = 12.0;
        public string CustomBgColor { get; set; } = "#12141A";
        public string CustomSidebarColor { get; set; } = "#12141A";
        public string CustomCardColor { get; set; } = "#1E222D";
        public string CustomAccentColor { get; set; } = "#6366F1";
        public string CustomBorderColor { get; set; } = "#33FFFFFF";

        /// <summary>
        /// Logo tint. Empty means "use the theme's own logo colour", which is how
        /// the window decides whether to fall back to the accent colour.
        /// </summary>
        public string CustomLogoColor { get; set; } = "";

        public string CustomTextPrimary { get; set; } = "#F0F2F5";
        public string CustomTextSecondary { get; set; } = "#9AA3B2";
        public double CustomSidebarOpacity { get; set; } = 0.65;
        public double CustomCardOpacity { get; set; } = 0.85;
        public double CustomInputOpacity { get; set; } = 0.20;
        public double CustomButtonOpacity { get; set; } = 1.0;
        public double CustomShadowBlur { get; set; } = 16.0;
        public double CustomShadowOpacity { get; set; } = 0.35;

        /// <summary>
        /// True until the first successful GUI setup (templates plus browser
        /// extensions). Drives the onboarding page; it is not a security flag.
        /// </summary>
        public bool FirstRun { get; set; } = true;

        /// <summary>
        /// Whether to look for a newer build on startup. On by default, because
        /// the alternative is people running a year-old copy and reporting bugs
        /// that were fixed months ago. Downloading still never happens without
        /// the user being told, and applying is always a separate click.
        /// </summary>
        public bool AutoUpdate { get; set; } = true;

        /// <summary>Recent URLs, newest first, capped at 50 by AddHistory.</summary>
        public List<HistoryItem> PlaybackHistory { get; set; } = new();

        /// <summary>
        /// Returns every appearance override to its shipped value.
        ///
        /// Playback settings are deliberately left alone: "reset appearance" must
        /// not change how mpv is launched and must not empty the history. The
        /// values are repeated literally rather than rebuilt from a default
        /// instance so that adding a new field cannot silently skip this method.
        /// </summary>
        public void ResetThemeSettings()
        {
            Theme = "dark";
            CustomBackground = "";
            CustomBlur = 8.0;
            WindowCornerRadius = 8.0;
            CardCornerRadius = 12.0;
            CustomBgColor = "#12141A";
            CustomSidebarColor = "#12141A";
            CustomCardColor = "#1E222D";
            CustomAccentColor = "#6366F1";
            CustomBorderColor = "#33FFFFFF";
            CustomLogoColor = "";
            CustomTextPrimary = "#F0F2F5";
            CustomTextSecondary = "#9AA3B2";
            CustomSidebarOpacity = 0.65;
            CustomCardOpacity = 0.85;
            CustomInputOpacity = 0.20;
            CustomButtonOpacity = 1.0;
            CustomShadowBlur = 16.0;
            CustomShadowOpacity = 0.35;
        }
    }

    /// <summary>One entry in the recently played list.</summary>
    public class HistoryItem
    {
        public string Title { get; set; } = "";
        public string Url { get; set; } = "";
        public DateTime Timestamp { get; set; } = DateTime.Now;
    }

    /// <summary>
    /// Reads and writes <see cref="AppConfig"/> plus the separate history file.
    ///
    /// Both the GUI and the native messaging host write history, and they are
    /// separate processes, so the file is the only shared state. Every read is
    /// therefore defensive and every failure is swallowed: a corrupt or locked
    /// settings file must never stop playback.
    /// </summary>
    public class ConfigService
    {
        /// <summary>How many URLs the history keeps.</summary>
        private const int HistoryLimit = 50;

        /// <summary>The live configuration. Mutate it, then call Save.</summary>
        public AppConfig Config { get; private set; }

        public ConfigService()
        {
            AppPaths.EnsureLayout();
            Config = LoadConfig();
            MergeHistoryFromFile();

            // Adopt the bundled mpv if the saved path no longer exists, which
            // happens after the app folder moves or mpv is updated in place.
            if (string.IsNullOrWhiteSpace(Config.MpvPath) || !File.Exists(Config.MpvPath))
            {
                if (File.Exists(AppPaths.MpvExe))
                {
                    Config.MpvPath = AppPaths.MpvExe;
                    Save();
                }
            }
        }

        /// <summary>
        /// Loads config.json, falling back to defaults. A malformed file is
        /// treated as absent rather than surfaced, so a bad edit cannot lock the
        /// user out of the app.
        /// </summary>
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

        /// <summary>
        /// The history lives in its own file so the native host can update it
        /// without rewriting the user's settings. config.json is only consulted
        /// as a fallback for installs made before the split.
        /// </summary>
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

        /// <summary>
        /// Writes both files. Best-effort: a read-only or locked data folder
        /// means the session still works, it just is not remembered.
        /// </summary>
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

        /// <summary>
        /// Records a playback, newest first and de-duplicated.
        ///
        /// The on-disk history is re-read first: the native host runs as its own
        /// process and may have appended a URL since this instance loaded, and
        /// writing the in-memory copy blindly would discard it.
        /// </summary>
        public void AddHistory(string url, string title = "")
        {
            var items = ReadHistoryFromDisk(Config.PlaybackHistory);
            AddHistoryTo(items, url, title);
            Config.PlaybackHistory = items;
            Save();
        }

        public void ClearHistory()
        {
            Config.PlaybackHistory.Clear();
            Save();
        }

        /// <summary>
        /// Records a playback from native-host mode, where no ConfigService
        /// instance exists because no window was ever created.
        /// </summary>
        public static void AddHistoryStatic(string url)
        {
            try
            {
                AppPaths.EnsureLayout();
                var items = ReadHistoryFromDisk(null);

                AddHistoryTo(items, url, url);

                File.WriteAllText(AppPaths.HistoryFile,
                    JsonSerializer.Serialize(items, new JsonSerializerOptions { WriteIndented = true }));

                // Mirror into config.json too, so an older build that still reads
                // history from there sees the same list.
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

        /// <summary>
        /// Loads the current history from disk, preferring history.json and
        /// falling back to config.json. <paramref name="fallback"/> is used when
        /// neither file can be read, so the in-memory list is never emptied.
        /// </summary>
        private static List<HistoryItem> ReadHistoryFromDisk(List<HistoryItem>? fallback)
        {
            if (File.Exists(AppPaths.HistoryFile))
            {
                try
                {
                    return JsonSerializer.Deserialize<List<HistoryItem>>(
                        File.ReadAllText(AppPaths.HistoryFile)) ?? new List<HistoryItem>();
                }
                catch { }
            }
            else if (File.Exists(AppPaths.ConfigFile))
            {
                try
                {
                    var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(AppPaths.ConfigFile));
                    if (cfg?.PlaybackHistory != null) return cfg.PlaybackHistory;
                }
                catch { }
            }

            return fallback != null ? new List<HistoryItem>(fallback) : new List<HistoryItem>();
        }

        /// <summary>Moves a URL to the front of the list, keeping it under the cap.</summary>
        private static void AddHistoryTo(List<HistoryItem> items, string url, string title)
        {
            items.RemoveAll(x => x.Url == url);
            items.Insert(0, new HistoryItem
            {
                Url = url,
                Title = string.IsNullOrEmpty(title) ? url : title
            });
            if (items.Count > HistoryLimit)
                items.RemoveRange(HistoryLimit, items.Count - HistoryLimit);
        }
    }
}
