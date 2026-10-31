// Original implementation, MIT licensed.

using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;

namespace MpvLauncher.Gui.Services
{
    /// <summary>
    /// Loads the unpacked extension through the browser's own launch command.
    ///
    /// Chrome 137 dropped <c>--load-extension</c> and ignores it silently, but
    /// Edge and Brave still honour it - measured on Edge 156 and Brave 152 by
    /// launching each into a throwaway profile and reading the id back out of
    /// <c>Secure Preferences</c>. So for those two a patched launch command is
    /// still the only install that needs no click in the browser, and it is
    /// applied per browser rather than to "Chromium" as a whole.
    ///
    /// The bug an older build shipped is not repeated here: it appended
    /// <c>--single-argument %1</c>, and <c>--single-argument</c> takes the URL
    /// as its own argument, so a Start-menu launch passed the literal text
    /// "%1" to the browser. The switch is now inserted ahead of an existing
    /// <c>%1</c> and never replaces the placeholder.
    /// </summary>
    internal static class ChromiumLaunchFlag
    {
        /// <summary>What the command line has to carry, and what marks our work.</summary>
        internal static string Argument(string extensionDir) =>
            "--load-extension=\"" + extensionDir.TrimEnd(Path.DirectorySeparatorChar) + "\"";

        /// <summary>True when this argument list already points at that folder.</summary>
        internal static bool HasFlag(string? arguments, string extensionDir)
        {
            if (string.IsNullOrWhiteSpace(arguments)) return false;

            string needle = Argument(extensionDir);
            return arguments.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Adds the flag to a command or argument line without disturbing the
        /// rest of it. A legacy <c>--single-argument %1</c> pair - the thing that
        /// broke Start-menu launches - is removed on the way through.
        /// </summary>
        internal static string AddFlag(string? arguments, string extensionDir)
        {
            string text = Clean(arguments);
            if (HasFlag(text, extensionDir)) return text;

            string flag = Argument(extensionDir);
            int placeholder = text.IndexOf("%1", StringComparison.Ordinal);
            if (placeholder >= 0)
            {
                string head = text[..placeholder].TrimEnd();
                string tail = text[placeholder..];
                return (head.Length > 0 ? head + " " : "") + flag + " " + tail;
            }

            return text.Length > 0 ? text + " " + flag : flag;
        }

        /// <summary>Drops our flag, leaving whatever else the line carried.</summary>
        internal static string RemoveFlag(string? arguments, string extensionDir)
        {
            string text = Clean(arguments);
            string flag = Argument(extensionDir);

            int at;
            while ((at = text.IndexOf(flag, StringComparison.OrdinalIgnoreCase)) >= 0)
                text = (text[..at].TrimEnd() + " " + text[(at + flag.Length)..].TrimStart()).Trim();

            return Collapse(text);
        }

        /// <summary>
        /// Rewrites the per-user Start-menu command so a shell launch carries the
        /// flag. Returns false when this browser registers no such key.
        /// </summary>
        internal static bool ApplyCommand(ChromiumBrowserInfo browser, string extensionDir)
        {
            if (string.IsNullOrWhiteSpace(browser.StartMenuKey)) return false;

            string path = CommandPath(browser.StartMenuKey);
            string machine = Read(path, Registry.LocalMachine);
            string current = Read(path, Registry.CurrentUser);

            // Rebuild from the machine command so a second run cannot stack the
            // flag, and so a value an older build mangled is thrown away.
            string basis = string.IsNullOrWhiteSpace(machine) ? current : machine;
            if (string.IsNullOrWhiteSpace(basis))
                basis = "\"" + browser.ExePath + "\"";

            string updated = AddFlag(basis, extensionDir);
            if (string.Equals(updated, current, StringComparison.Ordinal)) return true;

            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(path, writable: true);
                key?.SetValue("", updated, RegistryValueKind.String);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Removes a per-user command this app wrote, if it is still ours.</summary>
        internal static bool RemoveCommand(string startMenuKey, string extensionDir)
        {
            if (string.IsNullOrWhiteSpace(startMenuKey)) return false;

            string path = CommandPath(startMenuKey);
            string current = Read(path, Registry.CurrentUser);
            if (!HasFlag(current, extensionDir) && current.IndexOf("--single-argument", StringComparison.OrdinalIgnoreCase) < 0)
                return false;

            string machine = Read(path, Registry.LocalMachine);
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(path, writable: true);
                if (key == null) return false;

                if (string.IsNullOrWhiteSpace(machine))
                {
                    key.DeleteValue("", throwOnMissingValue: false);
                }
                else
                {
                    // Fall back to the machine command rather than our own text.
                    key.SetValue("", machine, RegistryValueKind.String);
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Adds the flag to every shortcut that starts this browser - desktop,
        /// Start menu and the taskbar - because a pinned icon does not read the
        /// Start-menu command above. Returns how many shortcuts were changed.
        /// </summary>
        internal static int PatchShortcuts(ChromiumBrowserInfo browser, string extensionDir)
        {
            int changed = 0;
            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return 0;

            dynamic? shell = null;
            try { shell = Activator.CreateInstance(shellType); } catch { }
            if (shell == null) return 0;

            foreach (string lnk in ShortcutsFor(browser.ExePath, shell))
            {
                try
                {
                    dynamic shortcut = shell.CreateShortcut(lnk);
                    string arguments = (shortcut.Arguments as string) ?? "";
                    string updated = AddFlag(arguments, extensionDir);
                    if (string.Equals(updated, arguments, StringComparison.Ordinal)) continue;

                    shortcut.Arguments = updated;
                    shortcut.Save();
                    changed++;
                }
                catch
                {
                    // A shortcut that cannot be rewritten is not worth failing on.
                }
            }

            return changed;
        }

        /// <summary>Removes the flag from every shortcut that starts this browser.</summary>
        internal static int UnpatchShortcuts(string extensionDir)
        {
            int changed = 0;
            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return 0;

            dynamic? shell = null;
            try { shell = Activator.CreateInstance(shellType); } catch { }
            if (shell == null) return 0;

            foreach (string lnk in ShortcutsFor(null, shell))
            {
                try
                {
                    dynamic shortcut = shell.CreateShortcut(lnk);
                    string arguments = (shortcut.Arguments as string) ?? "";
                    if (!HasFlag(arguments, extensionDir) &&
                        arguments.IndexOf("--single-argument", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    shortcut.Arguments = RemoveFlag(arguments, extensionDir);
                    shortcut.Save();
                    changed++;
                }
                catch
                {
                    // Same reasoning as above: best effort.
                }
            }

            return changed;
        }

        /// <summary>
        /// Adds the flag to every ProgID this browser registers, which is the
        /// command a link or a Start-menu launch actually runs. Returns how many
        /// commands were changed.
        /// </summary>
        internal static int ApplyProgIds(ChromiumBrowserInfo browser, string extensionDir)
        {
            int changed = 0;
            foreach (string progId in browser.ProgIds)
            {
                if (ApplyProgId(progId, extensionDir)) changed++;
            }
            return changed;
        }

        /// <summary>Removes the flag from every ProgID in <paramref name="progIds"/>.</summary>
        internal static int RemoveProgIds(IEnumerable<string> progIds, string extensionDir)
        {
            int changed = 0;
            foreach (string progId in progIds)
            {
                if (RemoveProgId(progId, extensionDir)) changed++;
            }
            return changed;
        }

        /// <summary>
        /// One ProgID: read the machine command, put the flag directly after the
        /// executable, and keep everything else - "%1" included - untouched. A
        /// command with no machine value is left alone, because guessing what a
        /// browser wanted there is how the older build broke Start-menu launches.
        /// </summary>
        private static bool ApplyProgId(string progId, string extensionDir)
        {
            string path = ProgIdPath(progId);
            string machine = Read(path, Registry.LocalMachine);
            if (string.IsNullOrWhiteSpace(machine)) return false;

            string flag = Argument(extensionDir);
            if (ContainsFlag(machine, flag)) return true;

            string updated = InsertAfterExe(machine, flag);
            if (string.Equals(updated, Read(path, Registry.CurrentUser), StringComparison.Ordinal)) return true;

            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(path, writable: true);
                key?.SetValue("", updated, RegistryValueKind.String);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool RemoveProgId(string progId, string extensionDir)
        {
            string path = ProgIdPath(progId);
            string current = Read(path, Registry.CurrentUser);
            if (!ContainsFlag(current, Argument(extensionDir))) return false;

            string machine = Read(path, Registry.LocalMachine);
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(path, writable: true);
                if (key == null) return false;

                if (string.IsNullOrWhiteSpace(machine)) key.DeleteValue("", throwOnMissingValue: false);
                else key.SetValue("", machine, RegistryValueKind.String);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Puts the flag right after the quoted executable, so a command that
        /// ends in "--single-argument %1" keeps both of them.
        /// </summary>
        internal static string InsertAfterExe(string command, string flag)
        {
            string text = command.Trim();
            if (text.Length == 0) return flag;

            if (text[0] == '"')
            {
                int close = text.IndexOf('"', 1);
                if (close > 0) return text[..(close + 1)] + " " + flag + text[(close + 1)..];
            }

            int space = text.IndexOf(' ');
            return space < 0 ? text + " " + flag : text[..space] + " " + flag + text[space..];
        }

        internal static bool ContainsFlag(string? command, string flag) =>
            !string.IsNullOrEmpty(command) &&
            command.IndexOf(flag, StringComparison.OrdinalIgnoreCase) >= 0;

        private static string ProgIdPath(string progId) =>
            @"Software\Classes\" + progId + @"\shell\open\command";

        /// <summary>The folder holding the shell command for one Start-menu key.</summary>
        private static string CommandPath(string startMenuKey) =>
            @"Software\Clients\StartMenuInternet\" + startMenuKey + @"\shell\open\command";

        private static string Read(string path, RegistryKey hive)
        {
            try
            {
                using var key = hive.OpenSubKey(path);
                return (key?.GetValue("") as string)?.Trim() ?? "";
            }
            catch
            {
                return "";
            }
        }

        /// <summary>Removes the legacy pair, which turned "%1" into a literal.</summary>
        private static string Clean(string? arguments)
        {
            string text = (arguments ?? "").Trim();

            int at;
            while ((at = text.IndexOf("--single-argument", StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                int end = text.IndexOf(' ', at + "--single-argument".Length);
                text = end < 0 ? text[..at].TrimEnd() : (text[..at] + text[end..]).Trim();
            }

            return Collapse(text);
        }

        private static string Collapse(string text) =>
            System.Text.RegularExpressions.Regex.Replace(text.Trim(), @"\s{2,}", " ");

        /// <summary>
        /// Every .lnk under the places Windows keeps them whose target is this
        /// browser. A null exe matches any Chromium executable, which is what the
        /// cleanup pass needs after a browser was uninstalled.
        /// </summary>
        private static IEnumerable<string> ShortcutsFor(string? exePath, dynamic shell)
        {
            string[] roots =
            {
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    @"Microsoft\Windows\Start Menu\Programs"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar"),
            };

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "chrome.exe", "msedge.exe", "brave.exe", "vivaldi.exe", "opera.exe", "chromium.exe",
            };

            foreach (string root in roots)
            {
                IEnumerable<string> files;
                try
                {
                    if (!Directory.Exists(root)) continue;
                    files = Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories);
                }
                catch
                {
                    continue;
                }

                foreach (string file in files)
                {
                    string target = "";
                    try { target = (shell.CreateShortcut(file).TargetPath as string) ?? ""; }
                    catch { }

                    if (target.Length == 0) continue;
                    if (!names.Contains(Path.GetFileName(target))) continue;
                    if (exePath != null && !string.Equals(target, exePath, StringComparison.OrdinalIgnoreCase)) continue;

                    yield return file;
                }
            }
        }
    }
}
