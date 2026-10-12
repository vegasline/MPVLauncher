using System;
using System.IO;
using System.Linq;

namespace MpvLauncher.Gui.Services
{
    /// <summary>
    /// Every path the application uses, rooted at an injected folder.
    ///
    /// The layout is a value, not a global. That is the only reason a test can
    /// exist for it: <see cref="AppPaths"/> answers the same questions but takes
    /// its root from the OS profile and latches "already laid out" for the
    /// whole process, so nothing downstream could be pointed at a scratch
    /// directory. Two adapters satisfy this one interface - the real profile
    /// for the application, a temporary folder for a test - which is what makes
    /// the seam real rather than hypothetical.
    ///
    /// Nothing here is user-configurable and nothing is relative to the working
    /// directory, so the app behaves the same however it is launched: from a
    /// shortcut, from a browser, or from a test host.
    /// </summary>
    public sealed class AppLayout
    {
        /// <summary>Root of all launcher application data.</summary>
        public string Root { get; }

        /// <summary>
        /// mpv's own data folder. It is not under <see cref="Root"/>: the folder
        /// belongs to mpv, and keeping it outside ours is what stops the
        /// launcher from looking like it owns somebody else's configuration.
        /// </summary>
        public string MpvAppDataDir { get; }

        /// <summary>
        /// Pre-AppData install location, consulted once so
        /// <see cref="EnsureLayout"/> can move anything an older build left.
        /// </summary>
        private readonly string _legacyInstallRoot;

        /// <summary>
        /// Latches the directory walk and the migration. Per instance rather
        /// than per process: a shared static flag would let the first layout
        /// suppress the second, which is exactly the bug a second layout exists
        /// to make possible.
        /// </summary>
        private bool _ensured;

        /// <param name="root">Where launcher data lives.</param>
        /// <param name="mpvAppDataDir">
        /// mpv's own data folder. Defaults to a sibling of <paramref name="root"/>
        /// so a test gets a self-contained tree.
        /// </param>
        /// <param name="legacyInstallRoot">
        /// Old install location to migrate from. Defaults to a folder inside
        /// <paramref name="root"/>, which means the shipped application passes
        /// the real one and a test migrates nothing.
        /// </param>
        public AppLayout(
            string root,
            string? mpvAppDataDir = null,
            string? legacyInstallRoot = null)
        {
            Root = root;
            MpvAppDataDir = mpvAppDataDir ?? Path.Combine(root, "mpv");
            _legacyInstallRoot = legacyInstallRoot ?? Path.Combine(root, "legacy");
        }

        public string SettingsDir => Path.Combine(Root, "settings");
        public string BinDir => Path.Combine(Root, "bin");
        public string CacheDir => Path.Combine(Root, "cache");
        public string LogsDir => Path.Combine(Root, "logs");
        public string NativeDir => Path.Combine(Root, "native");
        public string ThemesDir => Path.Combine(Root, "themes");
        public string LanguagesDir => Path.Combine(Root, "languages");
        public string ExtensionsDir => Path.Combine(Root, "extensions");
        public string ExtensionBundleDir => Path.Combine(ExtensionsDir, "bundle");
        public string ChromiumExtensionDir => Path.Combine(ExtensionsDir, "chromium");
        public string FirefoxExtensionDir => Path.Combine(ExtensionsDir, "firefox");

        public string ConfigFile => Path.Combine(SettingsDir, "config.json");
        public string HistoryFile => Path.Combine(SettingsDir, "history.json");
        public string ChromiumKeyFile => Path.Combine(SettingsDir, "chromium-extension.pem");
        public string HostLogFile => Path.Combine(LogsDir, "host.log");
        public string FirefoxManifest => Path.Combine(NativeDir, "com.mpv.launcher.firefox.json");
        public string ChromeManifest => Path.Combine(NativeDir, "com.mpv.launcher.chrome.json");
        public string FirefoxXpi =>
            Path.Combine(ExtensionsDir, AppPaths.FirefoxAddonId + ".xpi");

        public string MpvExe => Path.Combine(BinDir, "mpv.exe");
        public string YtDlpExe => Path.Combine(BinDir, "yt-dlp.exe");
        public string FfmpegExe => Path.Combine(BinDir, "ffmpeg.exe");

        public string MpvFontsDir => Path.Combine(MpvAppDataDir, "fonts");
        public string MpvScriptsDir => Path.Combine(MpvAppDataDir, "scripts");
        public string MpvScriptOptsDir => Path.Combine(MpvAppDataDir, "script-opts");
        public string MpvShadersDir => Path.Combine(MpvAppDataDir, "shaders");

        /// <summary>
        /// Creates every folder this layout owns, once. Safe and cheap to call
        /// from anywhere: the host process calls it before touching its log.
        /// </summary>
        public void EnsureLayout()
        {
            if (_ensured) return;
            _ensured = true;

            foreach (string dir in new[]
                     {
                         Root, SettingsDir, BinDir, CacheDir, LogsDir, NativeDir,
                         ThemesDir, LanguagesDir, ExtensionsDir, ExtensionBundleDir,
                         ChromiumExtensionDir, FirefoxExtensionDir
                     })
            {
                Directory.CreateDirectory(dir);
            }

            MigrateLegacyFiles();
        }

        /// <summary>
        /// Extracts embedded templates, then fills any remaining gaps from files
        /// next to the exe (dev builds).
        /// </summary>
        public void SeedTemplates(bool overwriteEmbedded = false)
        {
            EnsureLayout();
            ResourceSeeder.Extract(overwriteExtension: overwriteEmbedded);

            string baseDir = AppContext.BaseDirectory;
            CopyTemplateDir(Path.Combine(baseDir, "themes"), ThemesDir);
            CopyTemplateDir(Path.Combine(baseDir, "locales"), LanguagesDir, overwrite: true);
            CopyTemplateDir(Path.Combine(baseDir, "languages"), LanguagesDir, overwrite: true);
            CopyTemplateDir(Path.Combine(baseDir, "extension"), ExtensionBundleDir,
                allFiles: true, overwrite: true);

            // Development build: walk up looking for the repository root,
            // recognised by the project file that marks it.
            string? projectRoot = FindProjectRoot(baseDir);
            if (projectRoot != null)
            {
                CopyTemplateDir(Path.Combine(projectRoot, "themes"), ThemesDir);
                CopyTemplateDir(Path.Combine(projectRoot, "locales"), LanguagesDir, overwrite: true);
                // Files on disk can be newer than the embedded copies in a dev
                // build, so this source wins.
                CopyTemplateDir(Path.Combine(projectRoot, "extension"), ExtensionBundleDir,
                    allFiles: true, overwrite: true);
            }
        }

        /// <summary>
        /// Moves anything an older layout left behind into the current folders.
        ///
        /// Files are moved rather than copied and never overwrite an existing
        /// destination, so a user's newer file is never clobbered by a leftover.
        /// </summary>
        private void MigrateLegacyFiles()
        {
            TryMoveFile(Path.Combine(Root, "config.json"), ConfigFile);
            TryMoveFile(Path.Combine(Root, "host.log"), HostLogFile);
            TryMoveFile(Path.Combine(Root, "com.mpv.launcher.firefox.json"), FirefoxManifest);
            TryMoveFile(Path.Combine(Root, "com.mpv.launcher.chrome.json"), ChromeManifest);

            string legacyBin = Path.Combine(_legacyInstallRoot, "bin");
            string legacyCache = Path.Combine(_legacyInstallRoot, "downloads");

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

        /// <summary>
        /// Walks up from <paramref name="startDir"/> looking for the repository
        /// root (a directory that contains the csproj and the extension/ folder).
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

        /// <summary>
        /// Copies a template folder into the data directory.
        ///
        /// Only the file name is used to build the destination, so a template
        /// folder cannot write outside the target. The "allFiles" switch exists
        /// because the extension is not made of JSON alone.
        /// </summary>
        private static void CopyTemplateDir(
            string source, string dest, bool allFiles = false, bool overwrite = false)
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