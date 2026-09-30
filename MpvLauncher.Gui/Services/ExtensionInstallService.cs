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
    ///   - Chromium has no sideload concept for a released extension, so the
    ///     unpacked folder is registered by appending --load-extension to the
    ///     browser's launch command, and a URL-protocol handler is registered so
    ///     a normal window can be launched with it. From Chrome 137 the switch is
    ///     ignored unless DisableLoadExtensionCommandLineSwitch is turned off,
    ///     which is why that policy is written alongside it.
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
            "media_types.js", "network_capture.js",
            "popup.js", "popup.html", "popup.css",
            "icon16.png", "icon32.png", "icon48.png", "icon128.png"
        };

        private static readonly string[] ChromiumExes =
        {
            "chrome.exe", "brave.exe", "msedge.exe", "chromium.exe", "vivaldi.exe", "opera.exe"
        };

        /// <summary>
        /// Runs the full install for both browsers, in the only order that works:
        /// the files and manifests have to exist before the launch commands can
        /// name the folder, and the Chromium id has to exist before the manifest
        /// can carry its key.
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

                // 1. Copy the bundle into the per-browser folders.
                EnsureExtensionFiles(AppPaths.ChromiumExtensionDir);
                EnsureExtensionFiles(AppPaths.FirefoxExtensionDir);

                // 2. Each browser needs its own manifest: Chromium carries the
                //    key that pins the extension id, Firefox the gecko id.
                WriteChromiumManifest(AppPaths.ChromiumExtensionDir, publicKey);
                WriteFirefoxManifest(AppPaths.FirefoxExtensionDir);
                PackFirefoxXpi();

                // 3. Chromium: make a normally launched window load the folder.
                //    The registry overrides come first because they are the part
                //    that survives a restart; the shortcut patch is a bonus for
                //    launch paths that bypass the ProgId.
                int chromiumPatched = PatchChromiumLaunchCommands();
                chromiumPatched += EnsureChromiumCommandOverrides();
                PatchChromiumShortcuts();

                // 4. Firefox: drop the XPI in and allow sideloading.
                int firefoxProfiles = SideloadFirefoxXpi();
                WriteFirefoxProfilePrefs();

                // 5. Drop any policy from an older install that would now block
                //    the unpacked extension.
                ClearFirefoxPolicies();

                var xpi = new FileInfo(AppPaths.FirefoxXpi);

                // Each entry is a key plus the values to substitute into it.
                // The sentence itself is chosen by the locale file at display
                // time, so word order can differ per language.
                return new ExtensionInstallResult
                {
                    Success = true,
                    ChromiumId = chromiumId,
                    Details = new List<InstallDetail>
                    {
                        new() { Key = "install_item_chromium_dir",
                                Args = new object[] { AppPaths.ChromiumExtensionDir,
                                                      CountFiles(AppPaths.ChromiumExtensionDir) } },
                        new() { Key = "install_item_chromium_id",   Args = new object[] { chromiumId } },
                        new() { Key = "install_item_chromium_cmd",  Args = new object[] { chromiumPatched } },
                        new() { Key = "install_item_firefox_dir",
                                Args = new object[] { AppPaths.FirefoxExtensionDir,
                                                      CountFiles(AppPaths.FirefoxExtensionDir) } },
                        new() { Key = "install_item_firefox_id",    Args = new object[] { AppPaths.FirefoxAddonId } },
                        new() { Key = "install_item_firefox_xpi",
                                Args = new object[] { AppPaths.FirefoxXpi, xpi.Length } },
                        new() { Key = "install_item_firefox_profile", Args = new object[] { firefoxProfiles } },
                    }
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

        /// <summary>Removes the flags InjectExtensionFlags added, leaving the original command intact.</summary>
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
        /// Writes per-user overrides of the browser launch commands.
        ///
        /// This is the part that makes the extension survive a restart, and it
        /// exists because of two separate facts:
        ///
        ///   1. A machine-wide browser registers its command under HKLM, not
        ///      HKCU. Patching only HKCU therefore finds nothing to patch, which
        ///      is why the extension worked in Chrome - Chrome persists a
        ///      manually loaded unpacked extension - but vanished in Brave,
        ///      which does not persist it.
        ///
        ///   2. Shortcuts and registrations that a browser manages are rewritten
        ///      by the browser itself: on update, or on first run, Brave
        ///      recreates its own Start-menu entry and any flag written there is
        ///      lost. A shortcut patch is therefore not durable.
        ///
        /// HKCU is merged over HKLM by Windows, so writing the same key under
        /// HKCU overrides the machine value, applies the flags to every launch
        /// path the shell resolves through that ProgId, and needs no
        /// administrator rights. Only browsers that are actually installed are
        /// touched, and only the command string is overridden - the rest of each
        /// registration still comes from HKLM.
        ///
        /// Uninstall removes exactly these values again; see
        /// <see cref="RemoveChromiumCommandOverrides"/>.
        /// </summary>
        private static int EnsureChromiumCommandOverrides()
        {
            int patched = 0;

            foreach (var (progId, startMenu) in ChromiumBrowsers)
            {
                // Prefer the real ProgId command, so the override mirrors what
                // the browser would otherwise have been launched with.
                string? baseCommand = ReadMachineCommand($@"Software\Classes\{progId}\shell\open\command");
                if (baseCommand == null && startMenu != null)
                    baseCommand = ReadMachineCommand($@"Software\Clients\StartMenuInternet\{startMenu}\shell\open\command");

                if (string.IsNullOrWhiteSpace(baseCommand)) continue;
                if (!IsChromiumCommand(baseCommand!)) continue;

                var targets = new List<string> { $@"Software\Classes\{progId}\shell\open\command" };
                if (startMenu != null)
                    targets.Add($@"Software\Clients\StartMenuInternet\{startMenu}\shell\open\command");

                foreach (string key in targets)
                {
                    if (WriteUserCommand(key, InjectExtensionFlags(baseCommand!))) patched++;
                }
            }

            return patched;
        }

        /// <summary>
        /// Deletes the per-user overrides created above, but only while they
        /// still carry our own extension path. A value the user has since
        /// changed is left alone rather than overwritten.
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

        /// <summary>Reads a command from HKLM, checking the 32-bit view as well.</summary>
        private static string? ReadMachineCommand(string subKey)
        {
            foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
            {
                foreach (var view in new[]
                         {
                             RegistryView.Registry64, RegistryView.Registry32,
                         })
                {
                    try
                    {
                        using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                        using var key = baseKey.OpenSubKey(subKey);
                        if (key?.GetValue("") is string cmd && !string.IsNullOrWhiteSpace(cmd))
                            return cmd;
                    }
                    catch { }
                }
            }
            return null;
        }

        /// <summary>
        /// Creates or updates an HKCU command value. Returns true only when the
        /// stored value actually changed, so a repeat install does not inflate
        /// the "patched" count the UI reports.
        /// </summary>
        private static bool WriteUserCommand(string subKey, string command)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(subKey);
                if (key == null) return false;
                if (key.GetValue("") as string == command) return false;
                key.SetValue("", command);
                return true;
            }
            catch
            {
                return false;
            }
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
            foreach (string file in Directory.EnumerateFiles(source))
            {
                string name = Path.GetFileName(file);
                if (name.Equals("manifest.json", StringComparison.OrdinalIgnoreCase)) continue;
                if (name.Equals("chrome_debug.log", StringComparison.OrdinalIgnoreCase)) continue;

                try
                {
                    File.Copy(file, Path.Combine(dest, name), overwrite: true);
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
                if (fileName.Equals("manifest.json", StringComparison.OrdinalIgnoreCase)) continue;

                try
                {
                    using Stream? src = asm.GetManifestResourceStream(resName);
                    if (src == null) continue;
                    using var dst = File.Create(Path.Combine(targetDir, fileName));
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

        private static void WriteChromiumManifest(string dir, string publicKey)
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
                ["name"] = "MPV Launcher",
                ["version"] = "1.9.4",
                ["description"] = "Open page videos, iframes and streams directly in MPV.",
                ["key"] = publicKey,
                ["icons"] = icons,
                ["permissions"] = new[] { "nativeMessaging", "activeTab", "scripting", "webRequest", "tabs", "webNavigation", "storage" },
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
                        ["run_at"] = "document_end"
                    }
                }
            };
            WriteJson(Path.Combine(dir, "manifest.json"), manifest);
        }

        private static void WriteFirefoxManifest(string dir)
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
                ["name"] = "MPV Launcher",
                ["version"] = "1.9.4",
                ["description"] = "Network stream detector (HLS, DASH, MP4, iframes) and one-click media player for MPV.",
                ["icons"] = icons,
                ["permissions"] = new[] { "nativeMessaging", "activeTab", "scripting", "webRequest", "tabs", "webNavigation", "storage" },
                ["host_permissions"] = new[] { "<all_urls>" },
                ["action"] = new Dictionary<string, object>
                {
                    ["default_popup"] = "popup.html",
                    ["default_title"] = "MPV Launcher",
                    ["default_icon"] = icons
                },
                ["background"] = new Dictionary<string, object>
                {
                    ["scripts"] = new[]
                    {
                        "media_types.js", "network_capture.js", "background.js"
                    }
                },
                ["content_scripts"] = new object[]
                {
                    new Dictionary<string, object>
                    {
                        ["matches"] = new[] { "<all_urls>" },
                        ["js"] = new[] { "content_script.js" },
                        ["all_frames"] = true,
                        ["run_at"] = "document_end"
                    }
                },
                ["browser_specific_settings"] = new Dictionary<string, object>
                {
                    ["gecko"] = new Dictionary<string, object>
                    {
                        ["id"] = AppPaths.FirefoxAddonId,
                        ["strict_min_version"] = "128.0",
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

        private static void PackFirefoxXpi()
        {
            string xpi = AppPaths.FirefoxXpi;
            try
            {
                if (File.Exists(xpi))
                    File.Delete(xpi);
            }
            catch (Exception ex)
            {
                throw new IOException("Eski Firefox XPI silinemedi: " + ex.Message, ex);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(xpi)!);
            ZipFile.CreateFromDirectory(
                AppPaths.FirefoxExtensionDir,
                xpi,
                CompressionLevel.Fastest,
                includeBaseDirectory: false);

            var fi = new FileInfo(xpi);
            if (!fi.Exists || fi.Length == 0)
                throw new IOException("Firefox XPI oluşturulamadı: " + xpi);

            // Refresh the timestamp so Firefox does not keep serving a cached copy.
            try { File.SetLastWriteTimeUtc(xpi, DateTime.UtcNow); } catch { }
        }

        /// <summary>
        /// Chromium browsers we can attach the unpacked extension to, with the
        /// registry names their own installers use. StartMenu is null for the
        /// ProgIds that have no separate Start-menu registration.
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

        /// <summary>
        /// Chrome 137+ ignores --load-extension unless DisableLoadExtensionCommandLineSwitch is disabled.
        /// Patching URL protocol / StartMenuInternet commands applies the flags to normal launches.
        /// </summary>
        private static int PatchChromiumLaunchCommands()
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
                if (PatchCommandKey(key))
                    patched++;
            }

            return patched;
        }

        private static bool PatchCommandKey(string subKey)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(subKey, writable: true);
                if (key == null) return false;
                string? current = key.GetValue("") as string;
                if (string.IsNullOrWhiteSpace(current)) return false;
                if (!IsChromiumCommand(current)) return false;

                string updated = InjectExtensionFlags(current);
                if (string.Equals(current, updated, StringComparison.Ordinal))
                    return false;

                key.SetValue("", updated);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsChromiumCommand(string command)
        {
            foreach (var exe in ChromiumExes)
            {
                if (command.Contains(exe, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static string InjectExtensionFlags(string command)
        {
            string ext = AppPaths.ChromiumExtensionDir;
            string flags =
                "--disable-features=DisableLoadExtensionCommandLineSwitch " +
                "--enable-unsafe-extension-debugging " +
                "--load-extension=\"" + ext + "\"";

            if (command.Contains(ext, StringComparison.OrdinalIgnoreCase)
                && command.Contains("DisableLoadExtensionCommandLineSwitch", StringComparison.OrdinalIgnoreCase))
                return command;

            // Strip a previous incomplete --load-extension we may have written.
            command = StripFlag(command, "--load-extension=");
            command = StripFlag(command, "--enable-unsafe-extension-debugging");
            command = StripFlag(command, "--disable-features=DisableLoadExtensionCommandLineSwitch");

            string trimmed = command.Trim();
            if (trimmed.StartsWith('"'))
            {
                int end = trimmed.IndexOf('"', 1);
                if (end > 1)
                {
                    string exePart = trimmed[..(end + 1)];
                    string rest = trimmed[(end + 1)..].TrimStart();
                    return (exePart + " " + flags + (string.IsNullOrEmpty(rest) ? "" : " " + rest)).Trim();
                }
            }

            int space = trimmed.IndexOf(' ');
            if (space > 0)
                return trimmed[..space] + " " + flags + " " + trimmed[space..].TrimStart();

            return trimmed + " " + flags;
        }

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

        private static void PatchChromiumShortcuts()
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
            if (shellType == null) return;
            dynamic? wsh = Activator.CreateInstance(shellType);
            if (wsh == null) return;

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
                        string target = (lnk.TargetPath as string) ?? "";
                        if (string.IsNullOrEmpty(target)) continue;
                        string exe = Path.GetFileName(target);
                        if (!ChromiumExes.Contains(exe, StringComparer.OrdinalIgnoreCase)) continue;

                        string args = (lnk.Arguments as string) ?? "";
                        string updated = InjectExtensionFlags(string.IsNullOrWhiteSpace(args)
                            ? ("\"" + target + "\"")
                            : ("\"" + target + "\" " + args));

                        // Arguments should not include the exe path.
                        string newArgs = updated;
                        string quoted = "\"" + target + "\"";
                        if (newArgs.StartsWith(quoted, StringComparison.OrdinalIgnoreCase))
                            newArgs = newArgs[quoted.Length..].Trim();
                        else if (newArgs.StartsWith(target, StringComparison.OrdinalIgnoreCase))
                            newArgs = newArgs[target.Length..].Trim();

                        lnk.Arguments = newArgs;
                        lnk.Save();
                    }
                    catch { }
                }
            }
        }

        private static int SideloadFirefoxXpi()
        {
            int count = 0;
            string profilesRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                @"Mozilla\Firefox\Profiles");
            if (!Directory.Exists(profilesRoot)) return 0;

            string destName = AppPaths.FirefoxAddonId + ".xpi";
            foreach (var profile in Directory.EnumerateDirectories(profilesRoot))
            {
                try
                {
                    string extDir = Path.Combine(profile, "extensions");
                    Directory.CreateDirectory(extDir);

                    string destXpi = Path.Combine(extDir, destName);
                    File.Copy(AppPaths.FirefoxXpi, destXpi, overwrite: true);
                    try { File.SetLastWriteTimeUtc(destXpi, DateTime.UtcNow); } catch { }

                    // If a folder from an earlier install is still there, Firefox treats
                    // it as already installed and ignores the new XPI. Remove it first.
                    string unpacked = Path.Combine(extDir, AppPaths.FirefoxAddonId);
                    if (Directory.Exists(unpacked))
                    {
                        try { Directory.Delete(unpacked, recursive: true); } catch { }
                    }
                    count++;
                }
                catch { }
            }
            return count;
        }

        /// <summary>
        /// Removes the registry policy values this app wrote, and prunes our own
        /// entry out of any policies.json that happens to mention us.
        ///
        /// The previous version deleted the whole file. These files are the
        /// enterprise's - they sit under Program Files or LocalAppData and carry
        /// proxy, certificate and add-on policy set by an administrator - and
        /// this app never creates them. Deleting one on uninstall silently
        /// disables an organisation's Firefox management.
        /// </summary>
        private static void ClearFirefoxPolicies()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Policies\Mozilla\Firefox\ExtensionSettings", writable: true);
                key?.DeleteValue(AppPaths.FirefoxAddonId, throwOnMissingValue: false);
                key?.DeleteSubKeyTree(AppPaths.FirefoxAddonId, throwOnMissingSubKey: false);
            }
            catch { }

            try
            {
                using var install = Registry.CurrentUser.OpenSubKey(
                    @"Software\Policies\Mozilla\Firefox", writable: true);
                install?.DeleteSubKeyTree("Extensions", throwOnMissingSubKey: false);
            }
            catch { }

            string[] searchDirs =
            {
                @"C:\Program Files\Mozilla Firefox\distribution\policies.json",
                @"C:\Program Files (x86)\Mozilla Firefox\distribution\policies.json",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Mozilla Firefox\distribution\policies.json")
            };

            foreach (var f in searchDirs)
            {
                try
                {
                    PruneOurEntryFromPolicies(f);
                }
                catch { }
            }
        }

        /// <summary>
        /// Removes only this add-on's entry from a Firefox policies.json,
        /// rewriting every other policy untouched. The file is left alone
        /// entirely when it does not mention us.
        /// </summary>
        private static void PruneOurEntryFromPolicies(string file)
        {
            if (!File.Exists(file)) return;

            string json = File.ReadAllText(file);
            if (!json.Contains(AppPaths.FirefoxAddonId, StringComparison.OrdinalIgnoreCase)) return;

            using var doc = JsonDocument.Parse(json);
            var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
            {
                WriteWithoutOurAddon(doc.RootElement.Clone(), writer);
            }

            string updated = System.Text.Encoding.UTF8.GetString(buffer.ToArray());
            File.WriteAllText(file, updated + Environment.NewLine);
        }

        /// <summary>
        /// Copies a policies document, dropping any object member whose key is
        /// our add-on id. Everything else is reproduced verbatim.
        /// </summary>
        private static void WriteWithoutOurAddon(JsonElement element, Utf8JsonWriter writer)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    writer.WriteStartObject();
                    foreach (var prop in element.EnumerateObject())
                    {
                        if (prop.NameEquals(AppPaths.FirefoxAddonId)) continue;
                        prop.WriteTo(writer);
                    }
                    writer.WriteEndObject();
                    break;

                case JsonValueKind.Array:
                    writer.WriteStartArray();
                    foreach (var item in element.EnumerateArray()) item.WriteTo(writer);
                    writer.WriteEndArray();
                    break;

                default:
                    element.WriteTo(writer);
                    break;
            }
        }

        /// <summary>
        /// Appends the preferences an unsigned XPI needs to be sideloaded.
        ///
        /// Every write goes inside <see cref="FirefoxPrefsMarker"/> so that
        /// uninstall can remove exactly these lines and nothing else the user
        /// has in user.js.
        /// </summary>
        private static void WriteFirefoxProfilePrefs()
        {
            string profilesRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                @"Mozilla\Firefox\Profiles");
            if (!Directory.Exists(profilesRoot)) return;

            const string block =
                "\r\n" + FirefoxPrefsMarker + "\r\n" +
                "user_pref(\"extensions.autoDisableScopes\", 0);\r\n" +
                "user_pref(\"extensions.enabledScopes\", 15);\r\n" +
                "user_pref(\"extensions.sideloadScopes\", 15);\r\n" +
                "user_pref(\"xpinstall.signatures.required\", false);\r\n" +
                FirefoxPrefsEndMarker + "\r\n";

            const string sideloadOnly =
                "\r\n" + FirefoxPrefsMarker + "\r\n" +
                "user_pref(\"extensions.sideloadScopes\", 15);\r\n" +
                FirefoxPrefsEndMarker + "\r\n";

            foreach (var profile in Directory.EnumerateDirectories(profilesRoot))
            {
                try
                {
                    string userJs = Path.Combine(profile, "user.js");
                    string existing = File.Exists(userJs) ? File.ReadAllText(userJs) : "";

                    // Either marker counts as "already installed": the legacy one
                    // appears when upgrading from a build that had no delimiters.
                    bool alreadyWrote =
                        existing.Contains(FirefoxPrefsMarker, StringComparison.Ordinal) ||
                        existing.Contains(LegacyFirefoxPrefsMarker, StringComparison.Ordinal);

                    if (alreadyWrote)
                    {
                        if (!existing.Contains("extensions.sideloadScopes", StringComparison.Ordinal))
                            File.AppendAllText(userJs, sideloadOnly);
                        continue;
                    }

                    File.AppendAllText(userJs, block);
                }
                catch { }
            }
        }
    }
}
