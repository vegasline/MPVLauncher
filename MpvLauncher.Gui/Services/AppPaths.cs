using System;
using System.IO;

namespace MpvLauncher.Gui.Services
{
    /// <summary>
    /// Every path the application uses, in one place.
    ///
    /// All application data lives under %APPDATA%\MPVLauncher\ in category
    /// folders. Nothing here is user-configurable and nothing is relative to the
    /// working directory, so the app behaves the same however it is launched -
    /// from a shortcut, from a browser, or from a test harness.
    ///
    /// The properties are computed rather than stored because APPDATA is fixed
    /// for the process; they are properties only so there is a single definition
    /// to change if the layout ever moves.
    /// </summary>
    public static class AppPaths
    {
        /// <summary>Root of all application data.</summary>
        public static string Root { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MPVLauncher");

        public static string SettingsDir => Path.Combine(Root, "settings");
        public static string BinDir => Path.Combine(Root, "bin");
        public static string CacheDir => Path.Combine(Root, "cache");
        public static string LogsDir => Path.Combine(Root, "logs");
        public static string NativeDir => Path.Combine(Root, "native");
        public static string ThemesDir => Path.Combine(Root, "themes");
        public static string LanguagesDir => Path.Combine(Root, "languages");
        public static string ExtensionsDir => Path.Combine(Root, "extensions");
        public static string ExtensionBundleDir => Path.Combine(ExtensionsDir, "bundle");
        public static string ChromiumExtensionDir => Path.Combine(ExtensionsDir, "chromium");
        public static string FirefoxExtensionDir => Path.Combine(ExtensionsDir, "firefox");

        public static string ConfigFile => Path.Combine(SettingsDir, "config.json");
        public static string HistoryFile => Path.Combine(SettingsDir, "history.json");
        public static string ChromiumKeyFile => Path.Combine(SettingsDir, "chromium-extension.pem");
        public static string HostLogFile => Path.Combine(LogsDir, "host.log");
        public static string FirefoxManifest => Path.Combine(NativeDir, "com.mpv.launcher.firefox.json");
        public static string ChromeManifest => Path.Combine(NativeDir, "com.mpv.launcher.chrome.json");
        public static string FirefoxXpi => Path.Combine(ExtensionsDir, "{264eb39b-fe04-417b-940e-81df026edbc3}.xpi");

        public static string MpvExe => Path.Combine(BinDir, "mpv.exe");
        public static string YtDlpExe => Path.Combine(BinDir, "yt-dlp.exe");
        public static string FfmpegExe => Path.Combine(BinDir, "ffmpeg.exe");

        public static string MpvAppDataDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "mpv");
        public static string MpvFontsDir => Path.Combine(MpvAppDataDir, "fonts");
        public static string MpvScriptsDir => Path.Combine(MpvAppDataDir, "scripts");
        public static string MpvScriptOptsDir => Path.Combine(MpvAppDataDir, "script-opts");
        public static string MpvShadersDir => Path.Combine(MpvAppDataDir, "shaders");

        /// <summary>
        /// Stable Firefox add-on id.
        ///
        /// It is a fixed GUID rather than a generated one because the XPI file
        /// name and the profile/registry entries are keyed on it, and Firefox
        /// refuses to install a second add-on with an id it already has.
        /// </summary>
        public const string FirefoxAddonId = "{264eb39b-fe04-417b-940e-81df026edbc3}";

        /// <summary>
        /// Pre-AppData location, kept only so <see cref="MigrateLegacyFiles"/> can
        /// move anything left there by an older build.
        /// </summary>
        private static string LegacyInstallRoot => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "MPVLauncher");

        /// <summary>
        /// Guards EnsureLayout so the directory walk and the migration run once
        /// per process rather than on every call.
        /// </summary>
        private static bool _ensured;

        /// <summary>
        /// Creates every folder the app uses, once. Safe and cheap to call from
        /// anywhere: the host process calls it before touching its log.
        /// </summary>
        public static void EnsureLayout()
        {
            if (_ensured) return;
            _ensured = true;

            Directory.CreateDirectory(Root);
            Directory.CreateDirectory(SettingsDir);
            Directory.CreateDirectory(BinDir);
            Directory.CreateDirectory(CacheDir);
            Directory.CreateDirectory(LogsDir);
            Directory.CreateDirectory(NativeDir);
            Directory.CreateDirectory(ThemesDir);
            Directory.CreateDirectory(LanguagesDir);
            Directory.CreateDirectory(ExtensionsDir);
            Directory.CreateDirectory(ExtensionBundleDir);
            Directory.CreateDirectory(ChromiumExtensionDir);
            Directory.CreateDirectory(FirefoxExtensionDir);

            MigrateLegacyFiles();
        }

        /// <summary>
        /// Extracts embedded templates, then fills any remaining gaps from files next to the exe (dev builds).
        /// </summary>
        public static void SeedTemplates(bool overwriteEmbedded = false)
        {
            EnsureLayout();
            ResourceSeeder.Extract(overwriteExtension: overwriteEmbedded);

            string baseDir = AppContext.BaseDirectory;
            CopyTemplateDir(Path.Combine(baseDir, "themes"), ThemesDir);
            CopyTemplateDir(Path.Combine(baseDir, "locales"), LanguagesDir, overwrite: true);
            CopyTemplateDir(Path.Combine(baseDir, "languages"), LanguagesDir, overwrite: true);
            CopyTemplateDir(Path.Combine(baseDir, "extension"), ExtensionBundleDir, allFiles: true, overwrite: true);

            // Development build: walk up looking for the repository root,
            // recognised by the project file that marks it.
            string? projectRoot = FindProjectRoot(baseDir);
            if (projectRoot != null)
            {
                CopyTemplateDir(Path.Combine(projectRoot, "themes"), ThemesDir);
                CopyTemplateDir(Path.Combine(projectRoot, "locales"), LanguagesDir, overwrite: true);
                // Files on disk can be newer than the embedded copies in a dev build,
            // so this source wins.
                CopyTemplateDir(Path.Combine(projectRoot, "extension"), ExtensionBundleDir, allFiles: true, overwrite: true);
            }
        }

        /// <summary>
        /// Walks up from <paramref name="startDir"/> looking for the repository root
        /// (a directory that contains the csproj and the extension/ folder).
        /// </summary>
        private static string? FindProjectRoot(string startDir)
        {
            try
            {
                var dir = new DirectoryInfo(startDir);
                for (int i = 0; i < 10 && dir != null; i++, dir = dir.Parent)
                {
                    if (File.Exists(Path.Combine(dir.FullName, "MpvLauncher.Gui.csproj")) ||
                        File.Exists(Path.Combine(dir.FullName, "MpvLauncher.Gui", "MpvLauncher.Gui.csproj")))
                    {
                        return File.Exists(Path.Combine(dir.FullName, "extension", "background.js"))
                            ? dir.FullName
                            : Path.Combine(dir.FullName, "MpvLauncher.Gui");
                    }
                }
            }
            catch { }
            return null;
        }

        /**
         * Copies a template folder into the data directory.
         *
         * Only the file name is used to build the destination, so a template
         * folder cannot write outside the target. The "allFiles" switch exists
         * because the extension is not made of JSON alone.
         */
        private static void CopyTemplateDir(string source, string dest, bool allFiles = false, bool overwrite = false)
        {
            if (!Directory.Exists(source)) return;
            Directory.CreateDirectory(dest);
            string pattern = allFiles ? "*.*" : "*.json";
            foreach (string file in Directory.EnumerateFiles(source, pattern))
            {
                string target = Path.Combine(dest, Path.GetFileName(file));
                if (overwrite)
                {
                    try { File.Copy(file, target, overwrite: true); } catch { }
                }
                else
                {
                    TryCopyIfMissing(file, target);
                }
            }
        }

        /// <summary>
        /// Moves anything an older layout left behind into the current folders.
        ///
        /// Runs once from EnsureLayout. Files are moved rather than copied and
        /// never overwrite an existing destination, so a user's newer file is
        /// never clobbered by a leftover.
        /// </summary>
        private static void MigrateLegacyFiles()
        {
            TryMoveFile(Path.Combine(Root, "config.json"), ConfigFile);
            TryMoveFile(Path.Combine(Root, "host.log"), HostLogFile);
            TryMoveFile(Path.Combine(Root, "com.mpv.launcher.firefox.json"), FirefoxManifest);
            TryMoveFile(Path.Combine(Root, "com.mpv.launcher.chrome.json"), ChromeManifest);

            string legacyBin = Path.Combine(LegacyInstallRoot, "bin");
            string legacyCache = Path.Combine(LegacyInstallRoot, "downloads");

            if (Directory.Exists(legacyBin))
            {
                foreach (string file in Directory.EnumerateFiles(legacyBin))
                    TryCopyIfMissing(file, Path.Combine(BinDir, Path.GetFileName(file)));
            }

            if (Directory.Exists(legacyCache))
            {
                foreach (string file in Directory.EnumerateFiles(legacyCache))
                    TryCopyIfMissing(file, Path.Combine(CacheDir, Path.GetFileName(file)));
            }
        }

        /// <summary>Moves a file only when the destination does not exist yet.</summary>
        private static void TryMoveFile(string src, string dest)
        {
            try
            {
                if (!File.Exists(src) || File.Exists(dest)) return;
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Move(src, dest);
            }
            catch { }
        }

        /// <summary>Copies a file only when the destination is missing.</summary>
        private static void TryCopyIfMissing(string src, string dest)
        {
            try
            {
                if (!File.Exists(src) || File.Exists(dest)) return;
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(src, dest, overwrite: false);
            }
            catch { }
        }
    }
}
