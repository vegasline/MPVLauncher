using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MpvLauncher.Gui.Services
{
    /// <summary>
    /// Finds the mpv executable to launch.
    ///
    /// Two launchers need this: the window, from a URL the user typed, and the
    /// browser extension, from a URL a page supplied. They used to carry a list
    /// each, and the lists had already diverged - the extension knew about
    /// C:\mpv and D:\mpv and the window did not, so the same person on the same
    /// machine was told "mpv was not found" by one and served by the other.
    ///
    /// The search is the one this project already uses for yt-dlp and ffmpeg in
    /// DependencyService: the copy the launcher manages, then somewhere the
    /// user's own mpv turned up. It is deliberately not a list of drive
    /// letters. The launcher downloads mpv into its own bin folder, so a
    /// hardcoded C:\mpv is a guess about where somebody else's install lives,
    /// and the two tools beside it are found relative to mpv rather than by
    /// guessing at paths.
    ///
    /// What each caller does with the answer stays with the caller: the window
    /// falls back to the bare name and lets the shell resolve it, the extension
    /// reports not-found.
    /// </summary>
    public static class MpvLocator
    {
        /// <summary>
        /// The first candidate that exists, or null.
        /// </summary>
        /// <param name="configuredPath">The user's saved setting, if any. Skipped
        /// when it no longer exists - a stale setting points at where mpv used
        /// to be, which is the normal state after moving it.</param>
        /// <param name="layout">Where the launcher's own bin folder is.</param>
        /// <param name="exists">File-existence test. Injectable so the search
        /// order is testable without a disk.</param>
        /// <param name="pathVariable">The PATH to search. Defaults to this
        /// process's PATH.</param>
        public static string? Resolve(
            string? configuredPath,
            AppLayout layout,
            Func<string, bool> exists,
            string? pathVariable = null)
        {
            if (!string.IsNullOrWhiteSpace(configuredPath) && SafeExists(exists, configuredPath))
                return configuredPath;

            // The launcher's own copy comes before anything else. When it
            // manages mpv, the one it installed is the one the user means, and
            // quietly preferring a different mpv they put there by hand would
            // be worse than either answer.
            if (SafeExists(exists, layout.MpvExe))
                return layout.MpvExe;

            return ResolveFromPath(pathVariable ?? Environment.GetEnvironmentVariable("PATH"), exists);
        }

        /// <summary>
        /// The first mpv.exe on PATH.
        ///
        /// Last, because PATH is where an mpv ends up by accident: a leftover
        /// from a package manager, a shim, or a different tool entirely.
        /// </summary>
        public static string? ResolveFromPath(
            string? pathVariable, Func<string, bool> exists)
        {
            if (string.IsNullOrWhiteSpace(pathVariable)) return null;

            foreach (string dir in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                string trimmed = dir.Trim();
                if (trimmed.Length == 0) continue;
                try
                {
                    string candidate = Path.Combine(trimmed, "mpv.exe");
                    if (SafeExists(exists, candidate)) return candidate;
                }
                catch
                {
                    // A malformed PATH entry is skipped rather than allowed to
                    // end the search.
                }
            }

            return null;
        }

        /// <summary>
        /// A path can be malformed enough to throw inside File.Exists - a
        /// reserved device name, or a component that is not valid on this
        /// volume. One bad entry must not end the search.
        /// </summary>
        private static bool SafeExists(Func<string, bool> exists, string path)
        {
            try { return exists(path); }
            catch { return false; }
        }
    }

    /// <summary>
    /// Splits a user-supplied option string into tokens, honouring double
    /// quotes so a value containing a space stays one token.
    ///
    /// Splitting on spaces alone does not merely misparse - it turns one option
    /// into two, and the fragment that falls off the end is read by mpv as an
    /// option of its own. mpv's --script executes code, so a value the user
    /// quoted is a value they meant.
    /// </summary>
    public static class CommandLineTokenize
    {
        public static List<string> Split(string commandLine)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(commandLine)) return result;

            var current = new StringBuilder();
            bool inQuotes = false, any = false;

            foreach (char c in commandLine)
            {
                if (c == '"') { inQuotes = !inQuotes; any = true; continue; }
                if (!inQuotes && char.IsWhiteSpace(c))
                {
                    if (any) { result.Add(current.ToString()); current.Clear(); any = false; }
                    continue;
                }
                current.Append(c);
                any = true;
            }
            if (any) result.Add(current.ToString());
            return result;
        }
    }
}