using System;
using System.IO;
using System.Reflection;

namespace MpvLauncher.Gui.Services
{
    /// <summary>
    /// Writes the files that ship inside the executable out to disk.
    ///
    /// Themes, language packs, the browser extension and the Anime4K shader set
    /// are embedded as manifest resources so a build is a single file. This class
    /// unpacks them into %APPDATA% on first run, where the user and the rest of
    /// the app expect to find them.
    ///
    /// Overwrite policy differs per set and is deliberate: themes and languages
    /// are written once so a user's edits survive, while the extension bundle is
    /// refreshed so a repaired install actually picks up the new files.
    /// </summary>
    public static class ResourceSeeder
    {
        // Manifest resource names are prefixed so the four sets can be told apart
        // in one pass over Assembly.GetManifestResourceNames().
        private const string PrefixThemes = "embedded.themes.";
        private const string PrefixLanguages = "embedded.languages.";
        private const string PrefixExtension = "embedded.extension.";
        private const string PrefixAnime4k = "embedded.anime4k.";

        /// <summary>
        /// Unpacks themes, languages and the extension bundle.
        /// </summary>
        /// <param name="overwriteExtension">
        /// When true the extension files are rewritten even if they already exist.
        /// Used by Install/repair, where shipping the current files is the point.
        /// </param>
        public static void Extract(bool overwriteExtension)
        {
            AppPaths.EnsureLayout();
            var asm = Assembly.GetExecutingAssembly();

            foreach (string name in asm.GetManifestResourceNames())
            {
                if (name.StartsWith(PrefixThemes, StringComparison.Ordinal))
                    Write(asm, name, Path.Combine(AppPaths.ThemesDir, name[PrefixThemes.Length..]), overwrite: false);
                else if (name.StartsWith(PrefixLanguages, StringComparison.Ordinal))
                    Write(asm, name, Path.Combine(AppPaths.LanguagesDir, name[PrefixLanguages.Length..]), overwrite: false);
                else if (name.StartsWith(PrefixExtension, StringComparison.Ordinal))
                    Write(asm, name, Path.Combine(AppPaths.ExtensionBundleDir, name[PrefixExtension.Length..]), overwriteExtension);
            }
        }

        /// <summary>
        /// Installs the Anime4K shaders and GLSL scripts into %APPDATA%\mpv,
        /// which is where mpv looks for them at runtime.
        /// </summary>
        public static void ExtractAnime4k(bool overwrite = true)
        {
            AppPaths.EnsureLayout();
            Directory.CreateDirectory(AppPaths.MpvAppDataDir);
            Directory.CreateDirectory(AppPaths.MpvShadersDir);

            var asm = Assembly.GetExecutingAssembly();
            foreach (string name in asm.GetManifestResourceNames())
            {
                if (!name.StartsWith(PrefixAnime4k, StringComparison.Ordinal)) continue;

                // Resource names use forward slashes regardless of platform.
                string relPath = name[PrefixAnime4k.Length..]
                    .Replace('/', Path.DirectorySeparatorChar)
                    .Replace('\\', Path.DirectorySeparatorChar);
                Write(asm, name, Path.Combine(AppPaths.MpvAppDataDir, relPath), overwrite);
            }

            // Development builds also keep an anime4k folder next to the exe (and
            // in the project directory), which is newer than the embedded copy.
            // Only used as a source, never as a destination.
            string baseDir = AppContext.BaseDirectory;
            string localAnime = Path.Combine(baseDir, "anime4k");
            if (!Directory.Exists(localAnime))
            {
                string projectRoot = Path.GetFullPath(Path.Combine(baseDir, @"..\..\..\.."));
                string prjAnime = Path.Combine(projectRoot, "MpvLauncher.Gui", "anime4k");
                if (Directory.Exists(prjAnime)) localAnime = prjAnime;
            }

            if (Directory.Exists(localAnime))
            {
                CopyDirectoryTree(localAnime, AppPaths.MpvAppDataDir, overwrite);
            }
        }

        /// <summary>
        /// Copies a directory tree, recursing into subdirectories. Failures are
        /// swallowed: a partially installed shader set is preferable to none.
        /// </summary>
        private static void CopyDirectoryTree(string sourceDir, string targetDir, bool overwrite)
        {
            try
            {
                Directory.CreateDirectory(targetDir);
                foreach (string file in Directory.EnumerateFiles(sourceDir))
                {
                    string dest = Path.Combine(targetDir, Path.GetFileName(file));
                    if (overwrite || !File.Exists(dest))
                        File.Copy(file, dest, overwrite);
                }
                foreach (string subDir in Directory.EnumerateDirectories(sourceDir))
                {
                    string dirName = Path.GetFileName(subDir);
                    CopyDirectoryTree(subDir, Path.Combine(targetDir, dirName), overwrite);
                }
            }
            catch { }
        }

        /// <summary>
        /// Unpacks only what is missing. Called once at startup so a user who
        /// edited a theme or language file keeps their version.
        /// </summary>
        public static void ExtractMissing()
        {
            Extract(overwriteExtension: false);
        }

        /// <summary>
        /// Copies one embedded resource to disk, creating the parent folder.
        /// The destination path is built from the resource name, which is
        /// controlled by the build, not by anything the user supplies.
        /// </summary>
        private static void Write(Assembly asm, string resourceName, string destPath, bool overwrite)
        {
            try
            {
                if (!overwrite && File.Exists(destPath)) return;
                Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                using Stream? src = asm.GetManifestResourceStream(resourceName);
                if (src == null) return;
                using var dst = File.Create(destPath);
                src.CopyTo(dst);
            }
            catch { }
        }
    }
}
