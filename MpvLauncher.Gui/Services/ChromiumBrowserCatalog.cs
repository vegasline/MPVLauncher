// Original implementation, MIT licensed.

using System;
using System.Collections.Generic;
using System.IO;

namespace MpvLauncher.Gui.Services
{
    /// <summary>One Chromium browser that is actually installed on this machine.</summary>
    public sealed class ChromiumBrowserInfo
    {
        /// <summary>Display name, e.g. "Brave".</summary>
        public string Name { get; init; } = "";

        /// <summary>
        /// Key under <c>Software\Clients\StartMenuInternet</c> when the browser
        /// registers one; empty for the ones that only live in their own folder.
        /// </summary>
        public string StartMenuKey { get; init; } = "";

        public string ExePath { get; init; } = "";

        /// <summary>Folder holding the profiles, e.g. ...\Brave-Browser\User Data.</summary>
        public string UserDataDir { get; init; } = "";

        /// <summary>Policy key the browser reads, without the HKCU/HKLM prefix.</summary>
        public string PolicyRoot { get; init; } = "";

        /// <summary>Profile folders found on disk.</summary>
        public int Profiles { get; init; }

        /// <summary>Profiles where our extension is present and enabled.</summary>
        public int LoadedProfiles { get; init; }

        /// <summary>
        /// Whether this build still accepts the <c>--load-extension</c> switch.
        /// Chrome 137 dropped it and ignores it without a message, so patching
        /// its launch command would be a lie that also breaks the Start menu.
        /// Edge and Brave still honour it - measured on Edge 156 and Brave 152 by
        /// launching each into a throwaway profile and reading the id back out of
        /// Secure Preferences.
        /// </summary>
        public bool RecognisesLaunchFlag { get; init; }

        /// <summary>
        /// ProgIDs this browser registers for itself, e.g. "BraveHTML". The
        /// URL-opening command lives there, complete with its "%1", which is
        /// what a link or a Start-menu launch actually runs - the
        /// StartMenuInternet command carries no placeholder at all.
        /// </summary>
        public string[] ProgIds { get; init; } = System.Array.Empty<string>();

        public bool Loaded => LoadedProfiles > 0;

        /// <summary>
        /// The folder the browser loaded the extension from, as recorded in the
        /// profile. Empty when nothing loaded; a value that differs from the
        /// current install folder means the browser is running a stale copy.
        /// </summary>
        public string LoadedPath { get; init; } = "";
    }

    /// <summary>
    /// Finds the Chromium browsers installed for this user and asks each one
    /// whether our extension is loaded.
    ///
    /// The install flow used to treat "Chromium" as one target, which meant a
    /// machine with Chrome, Brave and Edge could only ever be told something
    /// about one of them - and the answer was about a folder, not a browser.
    /// Listing them makes the choice explicit: install where the user wants it,
    /// and say per browser whether it worked.
    /// </summary>
    internal static class ChromiumBrowserCatalog
    {
        private sealed record Candidate(
            string Name,
            string StartMenuKey,
            string PolicyRoot,
            bool RecognisesLaunchFlag,
            string[] ProgIds,
            string[] ExePaths,
            string UserDataDir);

        /// <summary>
        /// Every browser we know how to configure, in the order people tend to
        /// look for them. Missing executables are dropped by <see cref="Detect"/>.
        /// </summary>
        private static IEnumerable<Candidate> Candidates()
        {
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

            yield return new Candidate("Chrome", "Google Chrome", @"Software\Policies\Google\Chrome", false,
                new[] { "ChromeHTML" },
                new[]
                {
                    Path.Combine(pf, @"Google\Chrome\Application\chrome.exe"),
                    Path.Combine(pf86, @"Google\Chrome\Application\chrome.exe"),
                    Path.Combine(local, @"Google\Chrome\Application\chrome.exe"),
                },
                Path.Combine(local, @"Google\Chrome\User Data"));

            yield return new Candidate("Edge", "Microsoft Edge", @"Software\Policies\Microsoft\Edge", true,
                new[] { "MSEdgeHTM", "MSEdgePDF", "MSEdgeDHTML" },
                new[]
                {
                    Path.Combine(pf86, @"Microsoft\Edge\Application\msedge.exe"),
                    Path.Combine(pf, @"Microsoft\Edge\Application\msedge.exe"),
                },
                Path.Combine(local, @"Microsoft\Edge\User Data"));

            yield return new Candidate("Brave", "Brave", @"Software\Policies\BraveSoftware\Brave", true,
                new[] { "BraveHTML" },
                new[]
                {
                    Path.Combine(pf, @"BraveSoftware\Brave-Browser\Application\brave.exe"),
                    Path.Combine(pf86, @"BraveSoftware\Brave-Browser\Application\brave.exe"),
                    Path.Combine(local, @"BraveSoftware\Brave-Browser\Application\brave.exe"),
                },
                Path.Combine(local, @"BraveSoftware\Brave-Browser\User Data"));

            yield return new Candidate("Chromium", "Chromium", @"Software\Policies\Chromium", true,
                new[] { "ChromiumHTM" },
                new[]
                {
                    Path.Combine(pf, @"Chromium\Application\chrome.exe"),
                    Path.Combine(pf86, @"Chromium\Application\chrome.exe"),
                    Path.Combine(local, @"Chromium\Application\chrome.exe"),
                },
                Path.Combine(local, @"Chromium\User Data"));

            yield return new Candidate("Vivaldi", "Vivaldi", @"Software\Policies\Vivaldi", true,
                new[] { "VivaldiHTM" },
                new[]
                {
                    Path.Combine(local, @"Vivaldi\Application\vivaldi.exe"),
                    Path.Combine(pf, @"Vivaldi\Application\vivaldi.exe"),
                },
                Path.Combine(local, @"Vivaldi\User Data"));

            yield return new Candidate("Opera", "OperaStable", "", true,
                new[] { "OperaStable" },
                new[]
                {
                    Path.Combine(roaming, @"Opera Software\Opera Stable\opera.exe"),
                    Path.Combine(local, @"Programs\Opera\opera.exe"),
                },
                Path.Combine(roaming, @"Opera Software\Opera Stable"));
        }

        /// <summary>
        /// Every ProgID any known browser might have registered, for the cleanup
        /// pass that runs after a browser was uninstalled.
        /// </summary>
        internal static IReadOnlyList<string> AllProgIds()
        {
            var ids = new List<string>();
            foreach (var candidate in Candidates()) ids.AddRange(candidate.ProgIds);
            return ids;
        }

        /// <summary>
        /// Installed browsers only. A browser whose executable exists but whose
        /// profile folder does not is still listed: the user installed it and
        /// may simply not have started it yet.
        /// </summary>
        internal static IReadOnlyList<ChromiumBrowserInfo> Detect(string extensionId)
        {
            var found = new List<ChromiumBrowserInfo>();

            foreach (var candidate in Candidates())
            {
                string exe = "";
                foreach (string path in candidate.ExePaths)
                {
                    if (File.Exists(path)) { exe = path; break; }
                }
                if (exe.Length == 0) continue;

                int profiles = 0;
                int loaded = 0;
                try
                {
                    foreach (var state in BrowserExtensionState.Scan(
                                 extensionId, new[] { (candidate.Name, candidate.UserDataDir) }))
                    {
                        profiles = state.Profiles;
                        loaded = state.LoadedProfiles;
                    }
                }
                catch
                {
                    // A profile that cannot be read only costs the counts.
                }

                found.Add(new ChromiumBrowserInfo
                {
                    Name = candidate.Name,
                    StartMenuKey = candidate.StartMenuKey,
                    ExePath = exe,
                    UserDataDir = candidate.UserDataDir,
                    PolicyRoot = candidate.PolicyRoot,
                    Profiles = profiles,
                    LoadedProfiles = loaded,
                    RecognisesLaunchFlag = candidate.RecognisesLaunchFlag,
                    ProgIds = candidate.ProgIds,
                });
            }

            return found;
        }
    }
}
