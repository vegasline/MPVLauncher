using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace MpvLauncher.Gui.Services
{
    /// <summary>
    /// One reported fact about a completed install.
    ///
    /// <see cref="Key"/> is a localization key and <see cref="Args"/> its
    /// substitution values (a path, an id, a count). The service reports the
    /// values but never the sentence around them, so the wording stays in the
    /// locale files where it can be translated.
    /// </summary>
    public sealed class InstallDetail
    {
        public string Key { get; init; } = "";
        public object[] Args { get; init; } = Array.Empty<object>();
    }

    /// <summary>Outcome of an install, including the id Chromium ended up with.</summary>
    public sealed class ExtensionInstallResult
    {
        public bool Success { get; init; }

        /// <summary>
        /// Raw exception text on failure. Not localized, and never shown alone -
        /// the UI prefixes it with a translated heading.
        /// </summary>
        public string Error { get; init; } = "";

        public string ChromiumId { get; init; } = "";

        /// <summary>Localized-reportable facts, in display order.</summary>
        public List<InstallDetail> Details { get; init; } = new();
    }

    /// <summary>One removed item. <see cref="Key"/> is a localization key, formatted with Count.</summary>
    public sealed class UninstallItem
    {
        public string Key { get; init; } = "";
        public int Count { get; init; }
    }

    /// <summary>
    /// Structured uninstall result. The UI turns this into a localized message,
    /// so the service layer stays free of user-facing text.
    /// </summary>
    public sealed class UninstallResult
    {
        public bool Success { get; init; }
        public string Error { get; init; } = "";
        public List<UninstallItem> Items { get; init; } = new();

        /// <summary>True when at least one thing was actually removed.</summary>
        public bool AnyRemoved => Items.Count > 0;
    }

    /// <summary>
    /// Installs and removes the browser extension on both browser families.
    ///
    /// The two need completely different mechanisms:
    ///
    ///   - Chromium has no sideload concept for a released extension, and from
    ///     Chrome 137 the --load-extension switch is refused outright. The
    ///     unpacked folder is therefore written in place and loaded once by
    ///     hand. Earlier builds tried to register it through the browsers' own
    ///     launch commands; that broke normal launches and is undone by
    ///     <see cref="RemoveLegacyLaunchOverrides"/>.
    ///
    ///   - Firefox accepts an unsigned XPI directly. The XPI is dropped into
    ///     each profile's extensions folder and user.js is given the preferences
    ///     that allow sideloading.
    ///
    /// Everything is written under HKCU and under %APPDATA%, so no part of this
    /// needs administrator rights. Uninstall is the exact inverse, and is
    /// deliberately conservative: it removes only what a matching install
    /// created, verified by <see cref="RequiredFiles"/> and by the FirefoxPrefsMarker.
    /// </summary>
    public class ExtensionInstallService
    {
        /// <summary>
        /// Files that must exist in a deployed copy. Listing the network probe and
        /// capture layer matters as much as the popup: without them the extension
        /// loads but silently finds no streams.
        /// </summary>
        private static readonly string[] RequiredFiles =
        {
            "background.js", "content_script.js", "i18n.js", "network_probe.js",
            "media_types.js", "network_capture.js", "stream_meta.js",
            "popup.js", "popup.html", "popup.css",
            "icon16.png", "icon32.png", "icon48.png", "icon128.png",
            // The name and description are placeholders resolved from here, so
            // an install without this folder shows __MSG_extensionName__.
            Path.Combine("_locales", "en", "messages.json")
        };

        /// <summary>
        /// Runs the full install for Chromium, in the only order that works:
        /// the files and manifest have to exist before the launch commands can
        /// name the folder, and the Chromium id has to exist before the manifest
        /// can carry its key.
        ///
        /// Firefox is not installed from here: its add-on is signed and lives on
        /// addons.mozilla.org, so the only thing it needs locally is the native
        /// messaging host, which <see cref="BrowserIntegrationService"/> writes.
        ///
        /// Idempotent - running it again is what the repair button does, and it
        /// rewrites rather than duplicates.
        ///
        /// Returns localization keys plus their values rather than a finished
        /// sentence, so the caller renders this in the user's language.
        /// </summary>
        public ExtensionInstallResult InstallAll()
        {
            try
            {
                // Refresh the bundle first: embedded resources, then the
                // extension/ folder from a development build, which is newer.
                AppPaths.SeedTemplates(overwriteEmbedded: true);
                IconGenerator.EnsureIcons();

                var (chromiumId, publicKey) = ChromiumExtensionKey.GetOrCreate();

                // 1. Copy the bundle into the per-browser folder.
                EnsureExtensionFiles(AppPaths.ChromiumExtensionDir);

                // 2. Chromium carries the key that pins the extension id.
                WriteChromiumManifest(AppPaths.ChromiumExtensionDir, publicKey);

                // 3. Chromium needs no registration step: the folder written
                //    above is the whole install, and the user loads it once from
                //    chrome://extensions. Nothing is written into the browser's
                //    own configuration - see the class remarks for why.

                // Each entry is a key plus the values to substitute into it.
                // The sentence itself is chosen by the locale file at display
                // time, so word order can differ per language.
                var details = new List<InstallDetail>
                {
                    new() { Key = "install_item_chromium_dir",
                            Args = new object[] { AppPaths.ChromiumExtensionDir,
                                                  CountFiles(AppPaths.ChromiumExtensionDir) } },
                    new() { Key = "install_item_chromium_id",   Args = new object[] { chromiumId } },
                };

                // What the browsers themselves report, one line each. Without
                // this the summary only ever described the files on disk, which
                // is how "installed" and "nothing loaded" could both be true.
                var lookupIds = new List<string> { chromiumId };

                var loadedByName = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                var browserOrder = new List<string>();
                foreach (string lookupId in lookupIds)
                {
                    foreach (var browser in BrowserExtensionState.Scan(lookupId))
                    {
                        if (loadedByName.ContainsKey(browser.Name))
                            loadedByName[browser.Name] |= browser.Loaded;
                        else
                        {
                            loadedByName[browser.Name] = browser.Loaded;
                            browserOrder.Add(browser.Name);
                        }
                    }
                }

                foreach (string name in browserOrder)
                {
                    details.Add(new InstallDetail
                    {
                        Key = loadedByName[name] ? "install_item_browser_loaded"
                                                 : "install_item_browser_missing",
                        Args = new object[] { name }
                    });
                }

                // Browsers that still honour the load switch get it on their
                // launch command, which is what an older release did for
                // "Chromium" as a whole. Chrome is skipped rather than patched:
                // it ignores the switch, and an override there only costs the
                // user a broken Start-menu entry.
                int flagged = 0;
                foreach (var browser in ChromiumBrowserCatalog.Detect(chromiumId))
                {
                    if (!browser.RecognisesLaunchFlag) continue;

                    // Three places decide what a click opens: the Start-menu
                    // command, the protocol handlers that receive a link, and
                    // the pinned shortcuts that carry their own command line.
                    bool command = ChromiumLaunchFlag.ApplyCommand(browser, AppPaths.ChromiumExtensionDir);
                    int progIds = ChromiumLaunchFlag.ApplyProgIds(browser, AppPaths.ChromiumExtensionDir);
                    int shortcuts = ChromiumLaunchFlag.PatchShortcuts(browser, AppPaths.ChromiumExtensionDir);

                    if (command || progIds > 0 || shortcuts > 0) flagged++;
                }
                if (flagged > 0)
                {
                    details.Add(new InstallDetail
                    {
                        Key = "install_item_launch_flag",
                        Args = new object[] { flagged }
                    });
                }

                return new ExtensionInstallResult
                {
                    Success = true,
                    ChromiumId = chromiumId,
                    Details = details
                };
            }
            catch (Exception ex)
            {
                // The exception text is passed through untouched; it is technical
                // output, and the UI wraps it in a translated heading.
                return new ExtensionInstallResult { Success = false, Error = ex.Message };
            }
        }

        /// <summary>
        /// Removes everything InstallAll created: registry entries, native host manifests,
        /// extension folders, the XPI, Firefox profile files and launch command patches.
        /// </summary>
        public UninstallResult UninstallAll()
        {
            var removed = new List<UninstallItem>();

            try
            {
                AppPaths.EnsureLayout();

                // 1. Chromium launch command and shortcut patches.
                //    The overrides are removed first: the pass below only strips
                //    the flags, which would leave behind an HKCU key holding a
                //    copy of the machine command that this app created.
                int cmd = RemoveChromiumCommandOverrides();
                cmd += UnpatchChromiumLaunchCommands();
                if (cmd > 0) removed.Add(new UninstallItem { Key = "uninstall_item_chromium_cmd", Count = cmd });
                int lnk = UnpatchChromiumShortcuts();
                if (lnk > 0) removed.Add(new UninstallItem { Key = "uninstall_item_chromium_shortcuts", Count = lnk });

                // 1a. The load flag this installer writes for the browsers that
                //     accept it. Chrome never got one, but the call is harmless
                //     there and keeps the sweep independent of what is installed.
                int flags = 0;
                foreach (string key in new[] { "Google Chrome", "Microsoft Edge", "Brave", "Chromium", "Vivaldi", "OperaStable" })
                {
                    if (ChromiumLaunchFlag.RemoveCommand(key, AppPaths.ChromiumExtensionDir))
                        flags++;
                }
                flags += ChromiumLaunchFlag.UnpatchShortcuts(AppPaths.ChromiumExtensionDir);
                if (flags > 0) removed.Add(new UninstallItem { Key = "uninstall_item_chromium_cmd", Count = flags });

                // 1c. The link-opening commands, which are where a browser's own
                //     "%1" lives. Removing them restores the machine command.
                int progIds = ChromiumLaunchFlag.RemoveProgIds(
                    ChromiumBrowserCatalog.AllProgIds(), AppPaths.ChromiumExtensionDir);
                if (progIds > 0) removed.Add(new UninstallItem { Key = "uninstall_item_chromium_cmd", Count = progIds });

                // 2. Firefox profile
                int profiles = RemoveFirefoxXpi();
                if (profiles > 0) removed.Add(new UninstallItem { Key = "uninstall_item_firefox_profile", Count = profiles });
                if (RemoveFirefoxProfilePrefs())
                    removed.Add(new UninstallItem { Key = "uninstall_item_firefox_userjs", Count = 1 });

                // 3. Native messaging host registry keys and manifests
                int reg = RemoveNativeHostEntries();
                if (reg > 0) removed.Add(new UninstallItem { Key = "uninstall_item_native_host", Count = reg });
                if (TryDeleteFile(AppPaths.ChromeManifest))
                    removed.Add(new UninstallItem { Key = "uninstall_item_chrome_manifest", Count = 1 });
                if (TryDeleteFile(AppPaths.FirefoxManifest))
                    removed.Add(new UninstallItem { Key = "uninstall_item_firefox_manifest", Count = 1 });

                // 4. Extension folders
                if (TryDeleteDirectory(AppPaths.ChromiumExtensionDir))
                    removed.Add(new UninstallItem { Key = "uninstall_item_chromium_dir", Count = 1 });
                if (TryDeleteDirectory(AppPaths.FirefoxExtensionDir))
                    removed.Add(new UninstallItem { Key = "uninstall_item_firefox_dir", Count = 1 });
                if (TryDeleteFile(AppPaths.FirefoxXpi))
                    removed.Add(new UninstallItem { Key = "uninstall_item_firefox_xpi", Count = 1 });

                return new UninstallResult { Success = true, Items = removed };
            }
            catch (Exception ex)
            {
                return new UninstallResult { Success = false, Error = ex.Message };
            }
        }

        private static bool TryDeleteFile(string path)
        {
            try
            {
                if (!File.Exists(path)) return false;
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryDeleteDirectory(string path)
        {
            try
            {
                if (!Directory.Exists(path)) return false;
                Directory.Delete(path, recursive: true);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static int RemoveNativeHostEntries()
        {
            string[] keys =
            {
                @"Software\Google\Chrome\NativeMessagingHosts\com.mpv.launcher",
                @"Software\BraveSoftware\Brave-Browser\NativeMessagingHosts\com.mpv.launcher",
                @"Software\Microsoft\Edge\NativeMessagingHosts\com.mpv.launcher",
                @"Software\Chromium\NativeMessagingHosts\com.mpv.launcher",
                @"Software\Opera Software\Opera Stable\NativeMessagingHosts\com.mpv.launcher",
                @"Software\Opera Software\Opera GX Stable\NativeMessagingHosts\com.mpv.launcher",
                @"Software\Vivaldi\NativeMessagingHosts\com.mpv.launcher",
                @"Software\Yandex\YandexBrowser\NativeMessagingHosts\com.mpv.launcher",
                @"Software\Mozilla\NativeMessagingHosts\com.mpv.launcher",
                @"Software\Wow6432Node\Mozilla\NativeMessagingHosts\com.mpv.launcher"
            };

            int count = 0;
            foreach (var k in keys)
            {
                try
                {
                    using var key = Registry.CurrentUser.OpenSubKey(k, writable: true);
                    if (key == null) continue;
                    key.DeleteValue("", throwOnMissingValue: false);
                    key.Close();
                    Registry.CurrentUser.DeleteSubKeyTree(k, throwOnMissingSubKey: false);
                    count++;
                }
                catch { }
            }
            return count;
        }

        /// <summary>
        /// Removes the flags an older release added, leaving the original
        /// command intact. Only the repair pass calls this.
        /// </summary>
        private static string RemoveExtensionFlags(string command)
        {
            if (!command.Contains(AppPaths.ChromiumExtensionDir, StringComparison.OrdinalIgnoreCase))
                return command;

            string result = StripFlag(command, "--load-extension=");
            result = StripFlag(result, "--enable-unsafe-extension-debugging");

            // --disable-features may carry other features; only remove ours.
            var parts = SplitCommand(result);
            var sb = new StringBuilder();
            foreach (var raw in parts)
            {
                string part = raw;
                if (part.StartsWith("--disable-features=", StringComparison.OrdinalIgnoreCase))
                {
                    var features = part["--disable-features=".Length..]
                        .Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Where(f => !f.Trim().Equals("DisableLoadExtensionCommandLineSwitch", StringComparison.OrdinalIgnoreCase))
                        .Select(f => f.Trim())
                        .ToList();
                    if (features.Count == 0) continue;
                    part = "--disable-features=" + string.Join(",", features);
                }
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(part);
            }
            return sb.ToString().Trim();
        }

        /// <summary>
        /// Removes the browser launch flags an earlier release wrote, and
        /// nothing else.
        ///
        /// Up to 1.9.16 the unpacked folder was registered by rewriting the
        /// browsers' URL handlers in HKCU to pass --load-extension. Chrome 137
        /// and later refuse that switch even with its feature flag disabled, so
        /// it loaded nothing there - while it did add a literal "%1" to
        /// Start-menu launches and an "unsupported command-line flag" bar to
        /// every window. Installing no longer writes those values; this undoes
        /// them on machines that ran an older build.
        ///
        /// Runs on every start rather than only inside Uninstall, because the
        /// breakage is in the browsers, not in this app: someone who never opens
        /// the Dependencies page would otherwise keep it. Each pass is a handful
        /// of registry reads that find nothing once the values are gone.
        /// </summary>
        internal static int RemoveLegacyLaunchOverrides()
        {
            int removed = 0;

            try
            {
                removed += RemoveChromiumCommandOverrides();
                removed += UnpatchChromiumLaunchCommands();
                removed += UnpatchChromiumShortcuts();
                removed += RemoveLegacyExternalEntries();
            }
            catch
            {
                // Best effort: a value that cannot be read must never stop the
                // launcher from opening.
            }

            return removed;
        }

        /// <summary>
        /// Removes entries an older build wrote under
        /// <c>HKCU\Software\Google\Chrome\Extensions</c>, which is Chromium's
        /// "external extension" list: one subkey per extension id, holding the
        /// path to install from.
        ///
        /// That list only accepts a CRX, so a build that pointed it at the
        /// unpacked folder left a key Chrome complains about on every start
        /// ("File ... can not be read") and an extension that never loads. Only
        /// subkeys whose path is our own folder are removed, so an entry that
        /// belongs to something else keeps working.
        /// </summary>
        private static int RemoveLegacyExternalEntries()
        {
            int removed = 0;
            string marker = Path.GetFullPath(AppPaths.ChromiumExtensionDir)
                .TrimEnd(Path.DirectorySeparatorChar);

            foreach (string hive in new[] { @"Software\Google\Chrome\Extensions",
                                            @"Software\Chromium\Extensions",
                                            @"Software\Microsoft\Edge\Extensions" })
            {
                try
                {
                    using var root = Registry.CurrentUser.OpenSubKey(hive, writable: false);
                    if (root == null) continue;

                    foreach (string id in root.GetSubKeyNames())
                    {
                        string? path = null;
                        using (var sub = root.OpenSubKey(id))
                            path = sub?.GetValue("path") as string;

                        if (string.IsNullOrWhiteSpace(path)) continue;
                        if (!string.Equals(Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar),
                                           marker, StringComparison.OrdinalIgnoreCase))
                            continue;

                        try
                        {
                            Registry.CurrentUser.DeleteSubKeyTree(hive + "\\" + id, throwOnMissingSubKey: false);
                            removed++;
                        }
                        catch
                        {
                            // Another process holding the key open is not worth
                            // failing startup over; the next launch retries.
                        }
                    }
                }
                catch
                {
                    // Best effort, same as the rest of this repair pass.
                }
            }

            return removed;
        }

        /// <summary>
        /// Deletes the per-user overrides an older release created, but only
        /// while they still carry our own extension path. A value the user has
        /// since changed is left alone rather than overwritten.
        /// </summary>
        private static int RemoveChromiumCommandOverrides()
        {
            int removed = 0;
            string marker = AppPaths.ChromiumExtensionDir;

            foreach (var (progId, startMenu) in ChromiumBrowsers)
            {
                var targets = new List<string> { $@"Software\Classes\{progId}\shell\open\command" };
                if (startMenu != null)
                    targets.Add($@"Software\Clients\StartMenuInternet\{startMenu}\shell\open\command");

                foreach (string key in targets)
                {
                    if (DeleteUserCommandIfOurs(key, marker)) removed++;
                }
            }

            return removed;
        }

        /// <summary>
        /// Removes the default value from an HKCU command key when it is ours,
        /// then prunes the now-empty keys it created. Leaving an empty key behind
        /// would be harmless; leaving a stale command would not be.
        /// </summary>
        private static bool DeleteUserCommandIfOurs(string subKey, string marker)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(subKey, writable: true);
                if (key == null) return false;

                string? current = key.GetValue("") as string;
                if (string.IsNullOrWhiteSpace(current)) return false;
                if (!current.Contains(marker, StringComparison.OrdinalIgnoreCase)) return false;

                key.DeleteValue("", throwOnMissingValue: false);
                PruneEmptyKeyChain(subKey);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Walks up the key path deleting any key left with no values and no
        /// subkeys. Best effort - a key that cannot be removed is inert anyway.
        /// </summary>
        private static void PruneEmptyKeyChain(string subKey)
        {
            var parts = subKey.Split('\\');

            // Walk from the deepest key upwards, removing each one that has been
            // left with no values and no subkeys. The walk stops at the first key
            // that still holds something, so a shared parent such as
            // Software\Classes is never touched.
            for (int depth = parts.Length; depth > 0; depth--)
            {
                string path = string.Join('\\', parts.Take(depth));
                string parent = string.Join('\\', parts.Take(depth - 1));
                string leaf = parts[depth - 1];

                try
                {
                    using var key = Registry.CurrentUser.OpenSubKey(path, writable: true);
                    if (key == null) break;
                    if (key.ValueCount > 0 || key.GetSubKeyNames().Length > 0) break;

                    using var parentKey = Registry.CurrentUser.OpenSubKey(parent, writable: true);
                    if (parentKey == null) break;
                    parentKey.DeleteSubKeyTree(leaf, throwOnMissingSubKey: false);
                }
                catch { break; }
            }
        }

        private static int UnpatchChromiumLaunchCommands()
        {
            int patched = 0;
            var keys = new List<string>
            {
                @"Software\Classes\ChromeHTML\shell\open\command",
                @"Software\Classes\ChromeBHTML\shell\open\command",
                @"Software\Classes\BraveHTML\shell\open\command",
                @"Software\Classes\MSEdgeHTM\shell\open\command",
                @"Software\Classes\MSEdgePDF\shell\open\command",
                @"Software\Clients\StartMenuInternet\Google Chrome\shell\open\command",
                @"Software\Clients\StartMenuInternet\Brave\shell\open\command",
                @"Software\Clients\StartMenuInternet\Microsoft Edge\shell\open\command",
                @"Software\Clients\StartMenuInternet\Chromium\shell\open\command"
            };

            foreach (var proto in new[] { "http", "https" })
            {
                try
                {
                    using var choice = Registry.CurrentUser.OpenSubKey(
                        $@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\{proto}\UserChoice");
                    string? progId = choice?.GetValue("ProgId") as string;
                    if (!string.IsNullOrWhiteSpace(progId))
                        keys.Add($@"Software\Classes\{progId}\shell\open\command");
                }
                catch { }
            }

            foreach (var key in keys.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    using var k = Registry.CurrentUser.OpenSubKey(key, writable: true);
                    if (k == null) continue;
                    string? current = k.GetValue("") as string;
                    if (string.IsNullOrWhiteSpace(current)) continue;
                    string updated = RemoveExtensionFlags(current);
                    if (string.Equals(current, updated, StringComparison.Ordinal)) continue;
                    if (string.IsNullOrWhiteSpace(updated)) continue;
                    k.SetValue("", updated);
                    patched++;
                }
                catch { }
            }
            return patched;
        }

        private static int UnpatchChromiumShortcuts()
        {
            string[] searchRoots =
            {
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    @"Microsoft\Windows\Start Menu\Programs"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar")
            };

            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return 0;
            dynamic? wsh = Activator.CreateInstance(shellType);
            if (wsh == null) return 0;

            int count = 0;
            foreach (var root in searchRoots)
            {
                if (!Directory.Exists(root)) continue;
                IEnumerable<string> files;
                try { files = Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories); }
                catch { continue; }

                foreach (var lnkPath in files)
                {
                    try
                    {
                        dynamic lnk = wsh.CreateShortcut(lnkPath);
                        string args = (lnk.Arguments as string) ?? "";
                        if (string.IsNullOrWhiteSpace(args)) continue;
                        if (!args.Contains(AppPaths.ChromiumExtensionDir, StringComparison.OrdinalIgnoreCase)) continue;

                        lnk.Arguments = RemoveExtensionFlags(args);
                        lnk.Save();
                        count++;
                    }
                    catch { }
                }
            }
            return count;
        }

        private static int RemoveFirefoxXpi()
        {
            int count = 0;
            string profilesRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                @"Mozilla\Firefox\Profiles");
            if (!Directory.Exists(profilesRoot)) return 0;

            foreach (var profile in Directory.EnumerateDirectories(profilesRoot))
            {
                string extDir = Path.Combine(profile, "extensions");
                if (!Directory.Exists(extDir)) continue;

                // Only count the profile when something of ours was actually there.
                string xpi = Path.Combine(extDir, AppPaths.FirefoxAddonId + ".xpi");
                string unpacked = Path.Combine(extDir, AppPaths.FirefoxAddonId);
                bool hadXpi = File.Exists(xpi);
                bool hadUnpacked = Directory.Exists(unpacked);
                if (!hadXpi && !hadUnpacked) continue;

                TryDeleteFile(xpi);
                TryDeleteDirectory(unpacked);
                count++;
            }
            return count;
        }

        /// <summary>
        /// Marker that delimits the preferences this app owns inside user.js.
        /// Every write is wrapped in it so removal can be exact.
        /// </summary>
        private const string FirefoxPrefsMarker = "// >>> MPV Launcher";

        /// <summary>Closing half of <see cref="FirefoxPrefsMarker"/>.</summary>
        private const string FirefoxPrefsEndMarker = "// <<< MPV Launcher";

        /// <summary>
        /// Marker written by builds before the delimited format. Still recognised
        /// on both write and removal, otherwise an upgrading install would append
        /// a second copy of the preferences and uninstall would leave the first
        /// set behind.
        /// </summary>
        private const string LegacyFirefoxPrefsMarker = "// MPV Launcher";

        /// <summary>
        /// Removes only the preference blocks this app wrote.
        ///
        /// Matching on the preference names instead would also delete a user's
        /// own "extensions.enabledScopes" line, silently changing how Firefox
        /// treats their sideloaded add-ons. The markers delimit our lines, so
        /// everything outside them is left untouched.
        /// </summary>
        private static bool RemoveFirefoxProfilePrefs()
        {
            bool changed = false;
            string profilesRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                @"Mozilla\Firefox\Profiles");
            if (!Directory.Exists(profilesRoot)) return false;

            foreach (var profile in Directory.EnumerateDirectories(profilesRoot))
            {
                try
                {
                    string userJs = Path.Combine(profile, "user.js");
                    if (!File.Exists(userJs)) continue;

                    string[] lines = File.ReadAllLines(userJs);
                    var kept = new List<string>(lines.Length);
                    bool profileChanged = false;
                    bool inOurBlock = false;

                    // The legacy marker has no closing line, so the end of the
                    // block has to be inferred. Only the four preferences this
                    // app ever wrote are absorbed; anything else ends the block
                    // and is kept, so a user's own line that happens to follow
                    // ours is not swallowed.
                    string[] ourPrefs =
                    {
                        "user_pref(\"extensions.autoDisableScopes\"",
                        "user_pref(\"extensions.enabledScopes\"",
                        "user_pref(\"extensions.sideloadScopes\"",
                        "user_pref(\"xpinstall.signatures.required\""
                    };

                    foreach (string line in lines)
                    {
                        string trimmed = line.Trim();

                        if (trimmed.StartsWith(FirefoxPrefsEndMarker, StringComparison.Ordinal))
                        {
                            inOurBlock = false;
                            profileChanged = true;
                            continue;
                        }

                        if (trimmed.StartsWith(FirefoxPrefsMarker, StringComparison.Ordinal))
                        {
                            // Delimited form: everything up to the closing
                            // marker belongs to us.
                            inOurBlock = true;
                            profileChanged = true;
                            continue;
                        }

                        if (trimmed.StartsWith(LegacyFirefoxPrefsMarker, StringComparison.Ordinal))
                        {
                            inOurBlock = true;
                            profileChanged = true;
                            continue;
                        }

                        if (inOurBlock)
                        {
                            bool isOurs = ourPrefs.Any(p => trimmed.StartsWith(p, StringComparison.Ordinal));
                            if (isOurs) continue;
                            inOurBlock = false;
                        }

                        kept.Add(line);
                    }

                    if (profileChanged)
                    {
                        File.WriteAllLines(userJs, kept);
                        changed = true;
                    }
                }
                catch { }
            }
            return changed;
        }

        private static int CountFiles(string dir)
        {
            try
            {
                return Directory.Exists(dir) ? Directory.EnumerateFiles(dir).Count() : 0;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// Brings the installed extension copies up to the version this build
        /// ships, once per update.
        ///
        /// The launcher updates by replacing its own exe, and the embedded
        /// extension rides along - but a normal start never rewrites
        /// extensions\chromium, so a browser keeps loading the previous
        /// release's scripts. Comparing the shipped version with the installed
        /// one fixes that without rewriting a healthy install on every launch.
        ///
        /// Called from the window constructor. Returns the version written, or
        /// null when there was nothing to do, so the caller can tell the user
        /// that a browser restart is pending.
        /// </summary>
        internal static string? RefreshIfOutdated()
        {
            try
            {
                string installedManifest = Path.Combine(AppPaths.ChromiumExtensionDir, "manifest.json");

                // Nothing installed: the install flow writes everything in that
                // case. Creating a half-install from here would be worse than
                // leaving the machine alone.
                if (!File.Exists(installedManifest)) return null;

                string? shipped = ReadEmbeddedExtensionVersion();
                if (shipped == null) return null;

                string installed = ReadManifestVersion(installedManifest);
                if (!IsNewerVersion(shipped, installed)) return null;

                // Only the extension is rewritten. Themes keep a hand-edited
                // copy and language packs merge forward - both are the user's.
                AppPaths.SeedTemplates(overwriteEmbedded: true);

                var (_, publicKey) = ChromiumExtensionKey.GetOrCreate();
                EnsureExtensionFiles(AppPaths.ChromiumExtensionDir);

                // The Chromium manifest carries the key that pins the extension
                // id, so it is rebuilt rather than copied from the bundle.
                WriteChromiumManifest(AppPaths.ChromiumExtensionDir, publicKey);

                return shipped;
            }
            catch
            {
                // An extension refresh must never block startup; Install on the
                // Dependencies page does the same work on demand.
                return null;
            }
        }

        /// <summary>
        /// The extension's version, read from the manifest this build ships, so
        /// the number lives in one place: bumping extension/manifest.json is
        /// enough for the installer, the refresh check and the bundle.
        /// </summary>
        internal static string ShippedVersion() => ReadEmbeddedExtensionVersion() ?? "1.9.7";

        /// <summary>The version inside the manifest embedded in this build.</summary>
        private static string? ReadEmbeddedExtensionVersion()
        {
            using var stream = System.Reflection.Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("embedded.extension.manifest.json");
            if (stream == null) return null;

            using var reader = new StreamReader(stream, Encoding.UTF8);
            using var doc = JsonDocument.Parse(reader.ReadToEnd());
            return doc.RootElement.TryGetProperty("version", out var v) ? v.GetString() : null;
        }

        private static string ReadManifestVersion(string path)
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                return doc.RootElement.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "";
            }
            catch
            {
                return "";
            }
        }

        /// <summary>
        /// Dotted-version compare. A value that does not parse counts as
        /// different rather than newer: rewriting the files once is cheaper than
        /// skipping an update because the number looked odd.
        /// </summary>
        private static bool IsNewerVersion(string candidate, string current)
        {
            if (Version.TryParse(candidate, out var shipped) && Version.TryParse(current, out var installed))
                return shipped > installed;

            return !string.Equals(candidate, current, StringComparison.OrdinalIgnoreCase);
        }

        private static void EnsureExtensionFiles(string targetDir)
        {
            Directory.CreateDirectory(targetDir);

            // The on-disk bundle first - a dev build has the current extension/ files.
            int copied = 0;
            string sourceDir = FindBundledExtensionDir();
            if (!string.IsNullOrEmpty(sourceDir))
                copied = CopyExtensionTree(sourceDir, targetDir);

            // No bundle on disk yet: write it from the embedded resources.
            if (copied == 0)
                copied = ExtractEmbeddedExtension(targetDir);

            VerifyExtensionFiles(targetDir);
        }

        /// <summary>
        /// Copies every bundle file to the target. manifest.json is excluded
        /// because each browser needs its own variant.
        /// </summary>
        private static int CopyExtensionTree(string source, string dest)
        {
            if (!Directory.Exists(source)) return 0;
            Directory.CreateDirectory(dest);

            string sourceRoot = Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string destRoot = Path.GetFullPath(dest).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (sourceRoot.Equals(destRoot, StringComparison.OrdinalIgnoreCase)) return 0;

            int copied = 0;
            // Recursive: _locales/ arrives as subfolders, and a flat copy would
            // drop them without saying anything.
            foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                string name = Path.GetFileName(file);
                if (name.Equals("manifest.json", StringComparison.OrdinalIgnoreCase)) continue;
                if (name.Equals("chrome_debug.log", StringComparison.OrdinalIgnoreCase)) continue;

                try
                {
                    string target = Path.Combine(dest, Path.GetRelativePath(source, file));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(file, target, overwrite: true);
                    copied++;
                }
                catch { }
            }
            return copied;
        }

        private static int ExtractEmbeddedExtension(string targetDir)
        {
            var asm = typeof(ExtensionInstallService).Assembly;
            const string prefix = "embedded.extension.";
            int count = 0;

            foreach (string resName in asm.GetManifestResourceNames())
            {
                if (!resName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                string fileName = resName[prefix.Length..];

                // Resource names carry the source tree's separators, so a file
                // under _locales/ arrives as "_locales\en\messages.json".
                // Combining that with the target alone left the folder missing,
                // File.Create threw, and the catch dropped the file silently -
                // which is exactly how the localized name disappeared.
                string relative = fileName
                    .Replace('/', Path.DirectorySeparatorChar)
                    .Replace('\\', Path.DirectorySeparatorChar);

                if (relative.Equals("manifest.json", StringComparison.OrdinalIgnoreCase)) continue;

                string target = Path.Combine(targetDir, relative);

                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    using Stream? src = asm.GetManifestResourceStream(resName);
                    if (src == null) continue;
                    using var dst = File.Create(target);
                    src.CopyTo(dst);
                    count++;
                }
                catch { }
            }
            return count;
        }

        /// <summary>
        /// Reports a missing critical file explicitly instead of skipping it: a
        /// half-copied extension loads and then finds nothing, which looks like a
        /// detection bug rather than an install bug.
        /// </summary>
        private static void VerifyExtensionFiles(string targetDir)
        {
            var missing = RequiredFiles
                .Where(f => !File.Exists(Path.Combine(targetDir, f)))
                .ToList();

            if (missing.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Eklenti dosyaları {targetDir} klasörüne yazılamadı. Eksik: {string.Join(", ", missing)}. " +
                    "Uygulama dizinindeki 'extension' klasörünü kontrol edin.");
            }
        }

        public static string FindBundledExtensionDir()
        {
            string[] candidates =
            {
                AppPaths.ExtensionBundleDir,
                Path.Combine(AppContext.BaseDirectory, "extension"),
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\extension"))
            };

            foreach (var d in candidates)
            {
                if (Directory.Exists(d) && File.Exists(Path.Combine(d, "background.js")))
                    return d;
            }
            return "";
        }

        internal static void WriteChromiumManifest(string dir, string publicKey)
        {
            var icons = new Dictionary<string, string>
            {
                ["16"] = "icon16.png",
                ["32"] = "icon32.png",
                ["48"] = "icon48.png",
                ["128"] = "icon128.png"
            };

            var manifest = new Dictionary<string, object?>
            {
                ["manifest_version"] = 3,
                ["name"] = "__MSG_extensionName__",
                ["default_locale"] = "en",
                // Kept in step with extension/manifest.json. The extension is
                // distributed separately (addons.mozilla.org), so bumping this
                // for an app-only change would make the app install a version
                // the store does not have, and the two copies would collide.
                // A test holds the two to the same values.
                ["version"] = ShippedVersion(),
                ["description"] = "__MSG_extensionDescription__",
                ["key"] = publicKey,
                ["icons"] = icons,
                // webNavigation was here and nothing ever called it. A
                // permission the code does not use is one the user is asked to
                // grant and the store reviewer has to justify.
                ["permissions"] = new[] { "nativeMessaging", "activeTab", "scripting", "webRequest", "tabs", "storage" },
                ["host_permissions"] = new[] { "<all_urls>" },
                ["action"] = new Dictionary<string, object>
                {
                    ["default_popup"] = "popup.html",
                    ["default_title"] = "MPV Launcher",
                    ["default_icon"] = icons
                },
                ["background"] = new Dictionary<string, string>
                {
                    ["service_worker"] = "background.js"
                },
                ["content_scripts"] = new object[]
                {
                    new Dictionary<string, object>
                    {
                        ["matches"] = new[] { "<all_urls>" },
                        ["js"] = new[] { "content_script.js" },
                        ["all_frames"] = true,
                        ["run_at"] = "document_idle"
                    }
                }
            };
            WriteJson(Path.Combine(dir, "manifest.json"), manifest);
        }

        internal static void WriteFirefoxManifest(string dir)
        {
            var icons = new Dictionary<string, string>
            {
                ["16"] = "icon16.png",
                ["32"] = "icon32.png",
                ["48"] = "icon48.png",
                ["128"] = "icon128.png"
            };

            var manifest = new Dictionary<string, object?>
            {
                ["manifest_version"] = 3,
                ["name"] = "__MSG_extensionName__",
                ["default_locale"] = "en",
                // Kept in step with extension/manifest.json. The extension is
                // distributed separately (addons.mozilla.org), so bumping this
                // for an app-only change would make the app install a version
                // the store does not have, and the two copies would collide.
                // A test holds the two to the same values.
                ["version"] = ShippedVersion(),
                ["description"] = "__MSG_extensionDescription__",
                ["icons"] = icons,
                ["permissions"] = new[] { "nativeMessaging", "activeTab", "scripting", "webRequest", "tabs", "storage" },
                ["host_permissions"] = new[] { "<all_urls>" },
                ["action"] = new Dictionary<string, object>
                {
                    ["default_popup"] = "popup.html",
                    ["default_title"] = "MPV Launcher",
                    ["default_icon"] = icons
                },
                // Firefox does not implement service workers in MV3 and refuses
                // to load the add-on when one is declared. The sidecars are
                // listed ahead of background.js so it finds them already
                // defined, rather than relying on importScripts.
                ["background"] = new Dictionary<string, object>
                {
                    ["scripts"] = new[]
                    {
                        "media_types.js", "network_capture.js", "themes.js", "background.js"
                    }
                },
                ["content_scripts"] = new object[]
                {
                    new Dictionary<string, object>
                    {
                        ["matches"] = new[] { "<all_urls>" },
                        ["js"] = new[] { "content_script.js" },
                        ["all_frames"] = true,
                        ["run_at"] = "document_idle"
                    }
                },
                ["browser_specific_settings"] = new Dictionary<string, object>
                {
                    ["gecko"] = new Dictionary<string, object>
                    {
                        ["id"] = AppPaths.FirefoxAddonId,
                        // data_collection_permissions only exists from 140.
                        // Promising 128 asked the browser for a key it could
                        // not read, and the store rejected it.
                        ["strict_min_version"] = "140.0",
                        ["data_collection_permissions"] = new Dictionary<string, object>
                        {
                            ["required"] = new[] { "none" }
                        }
                    }
                }
            };
            WriteJson(Path.Combine(dir, "manifest.json"), manifest);
        }

        private static void WriteJson(string path, object obj)
        {
            File.WriteAllText(path, JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true }));
        }

        /// <summary>
        /// Chromium browsers an earlier release attached the unpacked extension
        /// to, with the registry names their own installers use. Only the repair
        /// pass reads this now; StartMenu is null for the ProgIds that have no
        /// separate Start-menu registration.
        /// </summary>
        private static readonly (string ProgId, string? StartMenu)[] ChromiumBrowsers =
        {
            ("ChromeHTML",   "Google Chrome"),
            ("ChromeHTML85", null),
            ("BraveHTML",    "Brave"),
            ("MSEdgeHTM",    "Microsoft Edge"),
            ("MSEdgePDF",    null),
            ("ChromiumHTM",  "Chromium"),
        };

        private static string StripFlag(string command, string flagPrefix)
        {
            var result = new StringBuilder();
            var parts = SplitCommand(command);
            foreach (var part in parts)
            {
                if (part.StartsWith(flagPrefix, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (result.Length > 0) result.Append(' ');
                result.Append(part);
            }
            return result.ToString();
        }

        private static List<string> SplitCommand(string command)
        {
            var parts = new List<string>();
            var cur = new StringBuilder();
            bool inQuote = false;
            foreach (char c in command)
            {
                if (c == '"')
                {
                    inQuote = !inQuote;
                    cur.Append(c);
                    continue;
                }
                if (char.IsWhiteSpace(c) && !inQuote)
                {
                    if (cur.Length > 0)
                    {
                        parts.Add(cur.ToString());
                        cur.Clear();
                    }
                    continue;
                }
                cur.Append(c);
            }
            if (cur.Length > 0)
                parts.Add(cur.ToString());
            return parts;
        }

    }
}
