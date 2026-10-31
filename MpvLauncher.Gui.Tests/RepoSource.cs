// Original implementation, MIT licensed.

using System;
using System.IO;
using System.Linq;

namespace MpvLauncher.Gui.Tests
{
    /// <summary>
    /// Reads the launcher's own sources for the contract tests.
    ///
    /// MainWindow lives in several partial files, so a test that reads only
    /// MainWindow.xaml.cs silently passes on code it never looked at. This
    /// concatenates every part instead.
    /// </summary>
    internal static class RepoSource
    {
        public static string MainWindow()
        {
            string dir = Path.Combine(Root(), "MpvLauncher.Gui");
            return string.Concat(Directory
                .GetFiles(dir, "MainWindow*.cs")
                .OrderBy(f => f, StringComparer.Ordinal)
                .Select(File.ReadAllText));
        }

        /// <summary>One repository file, for contract tests outside the window.</summary>
        public static string Read(string relativePath) =>
            System.IO.File.ReadAllText(Path.Combine(Root(), relativePath));

        private static string Root()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (int i = 0; i < 10 && dir != null; i++, dir = dir.Parent)
            {
                // The solution is MpvLauncher.slnx; check for the project file too
                // so a checkout without the solution still resolves.
                if (File.Exists(Path.Combine(dir.FullName, "MpvLauncher.slnx")) ||
                    File.Exists(Path.Combine(dir.FullName, "MpvLauncher.Gui", "MpvLauncher.Gui.csproj")))
                    return dir.FullName;
            }

            throw new InvalidOperationException("Repository root not found from " + AppContext.BaseDirectory);
        }
    }
}
