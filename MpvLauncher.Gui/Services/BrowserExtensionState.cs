// Original implementation, MIT licensed.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace MpvLauncher.Gui.Services
{
    /// <summary>What one installed Chromium browser says about our extension.</summary>
    public sealed class ChromiumBrowserState
    {
        /// <summary>Display name, e.g. "Brave".</summary>
        public string Name { get; init; } = "";

        /// <summary>Profiles that exist on disk, e.g. Default plus Profile 1.</summary>
        public int Profiles { get; init; }

        /// <summary>Profiles where the extension is present and enabled.</summary>
        public int LoadedProfiles { get; init; }

        public bool Loaded => LoadedProfiles > 0;

        /// <summary>
        /// The folder the browser actually loaded the extension from, as it
        /// recorded it. Empty when nothing loaded.
        ///
        /// An install carried over from an older release can still point at a
        /// folder that has since moved, and then the browser keeps running the
        /// old copy - no amount of rewriting the current folder reaches it. The
        /// browser rows say so instead of claiming everything is fine.
        /// </summary>
        public string LoadedPath { get; init; } = "";
    }

    /// <summary>
    /// Reads whether the unpacked extension is actually loaded in each browser.
    ///
    /// The installer used to report success on the strength of the files it had
    /// written, which said nothing about the browser: Chrome 137+ refuses
    /// --load-extension, so a machine could be told "installed" while every
    /// browser had nothing. This asks the browser instead - Chromium records
    /// every loaded extension in the profile's preferences, and an unpacked one
    /// is no different - so the summary can name the browsers that still need
    /// the manual step.
    ///
    /// Read-only by design. The extension entries carry a MAC, so writing them
    /// from outside the browser would be discarded on the next start; there is
    /// nothing here that a launcher can legitimately install for the user.
    /// </summary>
    public static class BrowserExtensionState
    {
        /// <summary>
        /// Where each browser keeps its profiles, paired with the name to show.
        /// Missing folders are simply not reported: a browser that is not
        /// installed has no opinion about our extension.
        /// </summary>
        private static IEnumerable<(string Name, string Root)> DefaultRoots()
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

            yield return ("Chrome", Path.Combine(local, @"Google\Chrome\User Data"));
            yield return ("Brave", Path.Combine(local, @"BraveSoftware\Brave-Browser\User Data"));
            yield return ("Edge", Path.Combine(local, @"Microsoft\Edge\User Data"));
            yield return ("Chromium", Path.Combine(local, @"Chromium\User Data"));
            yield return ("Vivaldi", Path.Combine(local, @"Vivaldi\User Data"));
            yield return ("Opera", Path.Combine(roaming, @"Opera Software\Opera Stable"));
        }

        /// <summary>Every installed browser, whether or not the extension is loaded there.</summary>
        public static IReadOnlyList<ChromiumBrowserState> Scan(string extensionId) =>
            Scan(extensionId, DefaultRoots());

        internal static IReadOnlyList<ChromiumBrowserState> Scan(
            string extensionId, IEnumerable<(string Name, string Root)> roots)
        {
            var states = new List<ChromiumBrowserState>();
            if (string.IsNullOrWhiteSpace(extensionId)) return states;

            foreach (var (name, root) in roots)
            {
                try
                {
                    if (!Directory.Exists(root)) continue;

                    var profiles = ProfileDirectories(root).ToList();
                    if (profiles.Count == 0) continue;

                    var loaded = profiles
                        .Select(dir => IsLoaded(dir, extensionId))
                        .Where(path => path != null)
                        .Select(path => path!)
                        .ToList();

                    states.Add(new ChromiumBrowserState
                    {
                        Name = name,
                        Profiles = profiles.Count,
                        LoadedProfiles = loaded.Count,
                        LoadedPath = loaded.FirstOrDefault() ?? ""
                    });
                }
                catch
                {
                    // An unreadable profile is not a fact about the extension;
                    // leaving the browser out is more honest than guessing.
                }
            }

            return states;
        }

        /// <summary>
        /// Profile folders, recognised the same way the browsers create them:
        /// a directory holding a Preferences file. The System and Guest profiles
        /// are skipped because nobody installs an extension there on purpose.
        /// </summary>
        private static IEnumerable<string> ProfileDirectories(string root)
        {
            foreach (string dir in Directory.EnumerateDirectories(root))
            {
                string leaf = Path.GetFileName(dir);
                if (leaf.Equals("System Profile", StringComparison.OrdinalIgnoreCase) ||
                    leaf.Equals("Guest Profile", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (File.Exists(Path.Combine(dir, "Preferences")))
                    yield return dir;
            }
        }

        /// <summary>
        /// The folder this profile loaded the extension from, or null when the
        /// profile does not list it or has disabled it.
        /// </summary>
        private static string? IsLoaded(string profileDir, string extensionId)
        {
            // Secure Preferences is the authoritative copy; Preferences holds
            // the same settings for profiles that never diverged.
            foreach (string file in new[] { "Secure Preferences", "Preferences" })
            {
                string path = Path.Combine(profileDir, file);
                try
                {
                    if (!File.Exists(path)) continue;

                    string text = File.ReadAllText(path);

                    // The id is 32 characters and the profile file can be
                    // megabytes; a text check avoids parsing what cannot match.
                    if (!text.Contains(extensionId, StringComparison.Ordinal)) continue;

                    using var doc = JsonDocument.Parse(text);
                    if (!doc.RootElement.TryGetProperty("extensions", out var extensions)) continue;
                    if (!extensions.TryGetProperty("settings", out var settings)) continue;
                    if (!settings.TryGetProperty(extensionId, out var entry)) continue;

                    // state 0 is DISABLED; a missing value means enabled.
                    if (entry.TryGetProperty("state", out var state) &&
                        state.ValueKind == JsonValueKind.Number &&
                        state.GetInt32() == 0)
                        return null;

                    return entry.TryGetProperty("path", out var loadedPath) &&
                           loadedPath.ValueKind == JsonValueKind.String
                        ? loadedPath.GetString() ?? ""
                        : "";
                }
                catch
                {
                    // Fall through to the other file, then to "not loaded".
                }
            }

            return null;
        }
    }
}
