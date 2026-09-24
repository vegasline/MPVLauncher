using System;
using System.IO;
using System.Reflection;

namespace MpvLauncher.Gui.Services
{
    /// <summary>
    /// Extracts extension / language / theme files that are compiled into the exe.
    /// </summary>
    public static class ResourceSeeder
    {
        private const string PrefixThemes = "embedded.themes.";
        private const string PrefixLanguages = "embedded.languages.";
        private const string PrefixExtension = "embedded.extension.";
        private const string PrefixAnime4k = "embedded.anime4k.";

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

        public static void ExtractAnime4k(bool overwrite = true)
        {
            AppPaths.EnsureLayout();
            Directory.CreateDirectory(AppPaths.MpvAppDataDir);
            Directory.CreateDirectory(AppPaths.MpvShadersDir);

            var asm = Assembly.GetExecutingAssembly();
            foreach (string name in asm.GetManifestResourceNames())
            {
                if (name.StartsWith(PrefixAnime4k, StringComparison.Ordinal))
                {
                    string relPath = name[PrefixAnime4k.Length..].Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
                    string dest = Path.Combine(AppPaths.MpvAppDataDir, relPath);
                    Write(asm, name, dest, overwrite);
                }
            }

            // Geliştirme ortamında derleme çıktısında veya proje kökünde varsa yedek kopyalama
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

        public static void ExtractMissing()
        {
            Extract(overwriteExtension: false);
        }

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
