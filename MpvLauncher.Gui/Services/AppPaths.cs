using System;
using System.IO;

namespace MpvLauncher.Gui.Services
{
    /// <summary>
    /// All application data lives under %APPDATA%\MPVLauncher\ in category folders.
    /// </summary>
    public static class AppPaths
    {
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

        public const string FirefoxAddonId = "{264eb39b-fe04-417b-940e-81df026edbc3}";

        private static string LegacyInstallRoot => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "MPVLauncher");

        private static bool _ensured;

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
            CopyTemplateDir(Path.Combine(baseDir, "locales"), LanguagesDir);
            CopyTemplateDir(Path.Combine(baseDir, "languages"), LanguagesDir);
            CopyTemplateDir(Path.Combine(baseDir, "extension"), ExtensionBundleDir, allFiles: true);

            string projectRoot = Path.GetFullPath(Path.Combine(baseDir, @"..\..\..\.."));
            if (Directory.Exists(Path.Combine(projectRoot, "themes")))
                CopyTemplateDir(Path.Combine(projectRoot, "themes"), ThemesDir);
            if (Directory.Exists(Path.Combine(projectRoot, "locales")))
                CopyTemplateDir(Path.Combine(projectRoot, "locales"), LanguagesDir);
            if (Directory.Exists(Path.Combine(projectRoot, "extension")))
                CopyTemplateDir(Path.Combine(projectRoot, "extension"), ExtensionBundleDir, allFiles: true);
        }

        private static void CopyTemplateDir(string source, string dest, bool allFiles = false)
        {
            if (!Directory.Exists(source)) return;
            Directory.CreateDirectory(dest);
            string pattern = allFiles ? "*.*" : "*.json";
            foreach (string file in Directory.EnumerateFiles(source, pattern))
            {
                string target = Path.Combine(dest, Path.GetFileName(file));
                TryCopyIfMissing(file, target);
            }
        }

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
