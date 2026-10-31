using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

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
        /// Version of the appearance block above. Bumped when the meaning of a
        /// field changes; the window then re-seeds the overrides from the saved
        /// theme once, so an old config cannot keep a theme's colours pinned to
        /// values from a previous version.
        /// </summary>
        public int AppearanceSchema { get; set; }

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

        // ---- Optional torrent streaming (off unless the user enables it) ----
        // These are launcher settings, not helper options: the helper receives
        // them on its command line and validates them again. Defaults here are
        // the documented ones, so a config.json written by an older build keeps
        // working without a migration step.

        /// <summary>Master switch. Every torrent source is refused while false.</summary>
        public bool TorrentStreamingEnabled { get; set; } = false;

        /// <summary>Set after the user has seen the privacy notice once.</summary>
        public bool TorrentPrivacyAccepted { get; set; } = false;

        /// <summary>
        /// Root for per-session folders. Empty means
        /// %APPDATA%\MPVLauncher\cache\torrent. A custom folder is still
        /// cleaned per session; the launcher only removes folders it created.
        /// </summary>
        public string TorrentDownloadDir { get; set; } = "";

        /// <summary>Remember the chosen file per info hash.</summary>
        public bool TorrentRememberFile { get; set; } = true;

        public int TorrentForwardWindowMiB { get; set; } = TorrentSettings.DefaultForwardWindowMiB;
        public int TorrentMpvBufferMiB { get; set; } = TorrentSettings.DefaultMpvBufferMiB;

        /// <summary>KiB/s. 0 means uploads are off; never more than 128.</summary>
        public int TorrentUploadLimitKiB { get; set; } = TorrentSettings.DefaultUploadKiB;

        public int TorrentMaxConnections { get; set; } = TorrentSettings.DefaultMaxConnections;
        public int TorrentMaxHalfOpen { get; set; } = TorrentSettings.DefaultMaxHalfOpen;

        /// <summary>
        /// Seconds playback waits for the end of the file. 0 starts as soon as
        /// the beginning is ready; the tail downloads in the background.
        /// </summary>
        public int TorrentTailWaitSeconds { get; set; } = TorrentSettings.DefaultTailWaitSeconds;

        /// <summary>
        /// Extra tracker list, one URL per line. Empty means the verified
        /// defaults in <see cref="TorrentSettings.DefaultTrackerList"/>.
        /// </summary>
        public string TorrentTrackerList { get; set; } = "";

        /// <summary>Version tag of the installed helper, for update checks.</summary>
        public string TorrentHelperVersion { get; set; } = "";

        /// <summary>
        /// SHA256 of the installed helper. Checked before every launch, so a
        /// swapped binary is refused even if the version string still matches.
        /// </summary>
        public string TorrentHelperSha256 { get; set; } = "";

        /// <summary>Info hash to last played file index.</summary>
        public Dictionary<string, int> TorrentLastFiles { get; set; } = new();

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

        /// <summary>
        /// Pinned rows survive "Clear history" and sort above the rest: a link
        /// somebody keeps coming back to should not be lost by a tidy-up.
        /// </summary>
        public bool Pinned { get; set; }

        /// <summary>
        /// The title without the URL fallback the host writes: a row that
        /// repeats its own link twice reads badly in the list.
        /// </summary>
        [JsonIgnore]
        public string DisplayTitle =>
            string.IsNullOrWhiteSpace(Title) || Title == Url ? Url : Title;

        /// <summary>
        /// Whether there is a folder to reveal. A torrent or a stream has
        /// none, so its row hides the button.
        /// </summary>
        [JsonIgnore]
        public bool IsLocalFile =>
            Url.StartsWith("file://", StringComparison.OrdinalIgnoreCase) || File.Exists(Url);
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
        /// <summary>
        /// Re-reads the history file. The native host appends to it from its
        /// own process, so a window that has been in the background is
        /// showing a stale list until this runs.
        /// </summary>
        public void RefreshHistoryFromDisk() => MergeHistoryFromFile();

        /// <summary>
        /// Re-reads the history file and folds it into the in-memory list.
        ///
        /// The native host appends from its own process, so the file is the only
        /// shared state. Taking the file only when memory was empty is what made
        /// a playback started from the browser invisible until the app was
        /// restarted: the window already had a list, so the new row was ignored.
        ///
        /// The union is the right answer. Rows the window has never seen are the
        /// host's and are added; a URL present on both sides keeps the newer
        /// timestamp and the better title; and a pin always wins, because only
        /// this window changes pins and the file may lag a moment behind.
        /// </summary>
        private void MergeHistoryFromFile()
        {
            if (!File.Exists(AppPaths.HistoryFile)) return;
            try
            {
                string json = File.ReadAllText(AppPaths.HistoryFile);
                var fromDisk = JsonSerializer.Deserialize<List<HistoryItem>>(json);
                if (fromDisk == null || fromDisk.Count == 0) return;

                Config.PlaybackHistory = MergeHistory(Config.PlaybackHistory, fromDisk, HistoryLimit);
            }
            catch { }
        }

        /// <summary>
        /// The union of what the window holds and what the file says, newest
        /// first and capped. Pure so the rules can be tested without touching
        /// the user's data folder.
        /// </summary>
        internal static List<HistoryItem> MergeHistory(
            IEnumerable<HistoryItem> memory, IEnumerable<HistoryItem> disk, int limit)
        {
            var merged = new List<HistoryItem>();
            var byUrl = new Dictionary<string, HistoryItem>(StringComparer.Ordinal);

            foreach (var item in memory)
            {
                if (string.IsNullOrWhiteSpace(item.Url)) continue;
                if (byUrl.TryAdd(item.Url, item)) merged.Add(item);
            }

            foreach (var item in disk)
            {
                if (string.IsNullOrWhiteSpace(item.Url)) continue;

                if (byUrl.TryGetValue(item.Url, out var mine))
                {
                    if (item.Timestamp > mine.Timestamp) mine.Timestamp = item.Timestamp;
                    if (IsLinkTitle(mine.Title, mine.Url) && !IsLinkTitle(item.Title, item.Url))
                        mine.Title = item.Title;
                    mine.Pinned |= item.Pinned;
                }
                else
                {
                    byUrl[item.Url] = item;
                    merged.Add(item);
                }
            }

            var ordered = merged
                .OrderByDescending(x => x.Pinned)
                .ThenByDescending(x => x.Timestamp)
                .ToList();

            if (limit > 0 && ordered.Count > limit)
                ordered.RemoveRange(limit, ordered.Count - limit);

            return ordered;
        }

        /// <summary>True when a title says nothing more than the link does.</summary>
        private static bool IsLinkTitle(string? title, string url) =>
            string.IsNullOrWhiteSpace(title) || title == url;

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
                WriteAtomic(AppPaths.ConfigFile, JsonSerializer.Serialize(Config, options));
                WriteAtomic(AppPaths.HistoryFile, JsonSerializer.Serialize(Config.PlaybackHistory, options));
            }
            catch { }
        }

        /// <summary>
        /// Writes a file by way of a temporary copy that is then swapped in.
        ///
        /// A direct write leaves a half-written file behind if the process dies
        /// or the machine loses power mid-call, and that costs the user every
        /// setting they ever changed. Replacing through a complete temporary
        /// file means the destination is either the old content or the new one.
        /// </summary>
        private static void WriteAtomic(string path, string json)
        {
            string temp = path + ".tmp";

            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(true);
            }

            if (File.Exists(path))
            {
                try
                {
                    // Replace keeps the destination's ACLs and is atomic on NTFS.
                    File.Replace(temp, path, null);
                    return;
                }
                catch (IOException)
                {
                    // Different volume, or the destination is briefly locked.
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            File.Move(temp, path, overwrite: true);
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

        /// <summary>
        /// Clears everything except the pinned rows: a tidy-up should not
        /// throw away the links somebody asked to keep.
        /// </summary>
        public void ClearHistory()
        {
            Config.PlaybackHistory.RemoveAll(x => !x.Pinned);
            Save();
        }

        /// <summary>Flips the pin on one row and saves.</summary>
        public void TogglePin(string url)
        {
            var item = Config.PlaybackHistory.FirstOrDefault(x => x.Url == url);
            if (item == null) return;

            item.Pinned = !item.Pinned;
            SortPinnedFirst(Config.PlaybackHistory);
            Save();
        }

        /// <summary>Drops one row; the file it points at is left alone.</summary>
        public void RemoveHistory(string url)
        {
            Config.PlaybackHistory.RemoveAll(x => x.Url == url);
            Save();
        }

        /// <summary>
        /// Pinned rows sit above the rest while keeping their relative order.
        /// </summary>
        private static void SortPinnedFirst(List<HistoryItem> items)
        {
            var pinned = new List<HistoryItem>();
            var rest = new List<HistoryItem>();
            foreach (var item in items)
                (item.Pinned ? pinned : rest).Add(item);

            items.Clear();
            items.AddRange(pinned);
            items.AddRange(rest);
        }

        /// <summary>
        /// Records a playback from native-host mode, where no ConfigService
        /// instance exists because no window was ever created.
        /// </summary>
        public static void AddHistoryStatic(string url, string title = "")
        {
            try
            {
                AppPaths.EnsureLayout();
                var items = ReadHistoryFromDisk(null);

                AddHistoryTo(items, url, title);

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
            bool wasPinned = false;
            string previousTitle = "";
            foreach (var existing in items)
            {
                if (existing.Url != url) continue;
                wasPinned = existing.Pinned;
                previousTitle = existing.Title ?? "";
                break;
            }

            items.RemoveAll(x => x.Url == url);

            // A known title outranks a bare link: playing the same thing again
            // - from the launcher, where no page title was fetched yet - must not
            // demote a row that already reads "Paws & Palaces - 4. Bölüm".
            string effectiveTitle = string.IsNullOrEmpty(title) || title == url
                ? (string.IsNullOrEmpty(previousTitle) ? url : previousTitle)
                : title;

            // New rows go under the pinned block, not above it.
            int insertAt = 0;
            while (insertAt < items.Count && items[insertAt].Pinned) insertAt++;

            items.Insert(insertAt, new HistoryItem
            {
                Url = url,
                Title = effectiveTitle,
                Pinned = wasPinned
            });
            if (items.Count > HistoryLimit)
                items.RemoveRange(HistoryLimit, items.Count - HistoryLimit);
        }

        /// <summary>
        /// Fills in a title the row was added without - the launcher learns it
        /// after playback has already started, so the row exists first and is
        /// renamed a moment later.
        /// </summary>
        public bool SetHistoryTitle(string url, string title)
        {
            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(title)) return false;

            var item = Config.PlaybackHistory.FirstOrDefault(x => x.Url == url);
            if (item == null || item.Title == title) return false;

            item.Title = title;
            Save();
            return true;
        }
    }
}
