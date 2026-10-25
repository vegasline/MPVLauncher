using System;
using System.IO;

namespace MpvLauncher.Gui.Services
{
    /// <summary>
    /// The shipped application's layout: %APPDATA%\MPVLauncher.
    ///
    /// This is the static face of <see cref="AppLayout"/> and it exists so the
    /// thirteen call sites that ask "where does this file live" keep working
    /// unchanged. The layout itself, the seeding and the migration now live in
    /// <see cref="AppLayout"/>, which takes its root as an argument - so a test
    /// can ask the same questions against a temporary folder.
    ///
    /// Prefer passing an <see cref="AppLayout"/> into a module. Reach for this
    /// only from code with nowhere to put one, such as a static entry point.
    /// </summary>
    public static class AppPaths
    {
        private static readonly AppLayout Default = new AppLayout(
            root: Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "MPVLauncher"),
            mpvAppDataDir: Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "mpv"),
            legacyInstallRoot: Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "MPVLauncher"));

        /// <summary>
        /// Stable Firefox add-on id.
        ///
        /// It is a fixed GUID rather than a generated one because the XPI file
        /// name and the profile/registry entries are keyed on it, and Firefox
        /// refuses to install a second add-on with an id it already has.
        /// </summary>
        public const string FirefoxAddonId = "{264eb39b-fe04-417b-940e-81df026edbc3}";

        /// <summary>Root of all launcher application data.</summary>
        public static string Root => Default.Root;

        public static string SettingsDir => Default.SettingsDir;
        public static string BinDir => Default.BinDir;
        public static string CacheDir => Default.CacheDir;
        public static string LogsDir => Default.LogsDir;
        public static string NativeDir => Default.NativeDir;
        public static string ThemesDir => Default.ThemesDir;
        public static string LanguagesDir => Default.LanguagesDir;
        public static string ExtensionsDir => Default.ExtensionsDir;
        public static string ExtensionBundleDir => Default.ExtensionBundleDir;
        public static string ChromiumExtensionDir => Default.ChromiumExtensionDir;
        public static string FirefoxExtensionDir => Default.FirefoxExtensionDir;

        public static string ConfigFile => Default.ConfigFile;
        public static string HistoryFile => Default.HistoryFile;
        public static string ChromiumKeyFile => Default.ChromiumKeyFile;
        public static string HostLogFile => Default.HostLogFile;
        public static string FirefoxManifest => Default.FirefoxManifest;
        public static string ChromeManifest => Default.ChromeManifest;
        public static string FirefoxXpi => Default.FirefoxXpi;

        public static string MpvExe => Default.MpvExe;
        public static string YtDlpExe => Default.YtDlpExe;
        public static string FfmpegExe => Default.FfmpegExe;

        /// <summary>The optional torrent helper. Downloaded, never shipped.</summary>
        public static string TorrentHelperExe => Path.Combine(Default.BinDir, "mpvtorrent.exe");

        /// <summary>Persistent DHT and metadata cache for the torrent helper.</summary>
        public static string TorrentCacheDir => Path.Combine(Default.CacheDir, "torrent");

        public static string MpvAppDataDir => Default.MpvAppDataDir;
        public static string MpvFontsDir => Default.MpvFontsDir;
        public static string MpvScriptsDir => Default.MpvScriptsDir;
        public static string MpvScriptOptsDir => Default.MpvScriptOptsDir;
        public static string MpvShadersDir => Default.MpvShadersDir;

        /// <summary>
        /// Creates every folder the app uses, once. Safe and cheap to call from
        /// anywhere: the host process calls it before touching its log.
        /// </summary>
        public static void EnsureLayout() => Default.EnsureLayout();

        /// <summary>
        /// Extracts embedded templates, then fills any remaining gaps from files
        /// next to the exe (dev builds).
        /// </summary>
        public static void SeedTemplates(bool overwriteEmbedded = false) =>
            Default.SeedTemplates(overwriteEmbedded);
    }
}