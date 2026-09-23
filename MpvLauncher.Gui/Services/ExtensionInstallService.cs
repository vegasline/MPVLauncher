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
    public sealed class ExtensionInstallResult
    {
        public bool Success { get; init; }
        public string Message { get; init; } = "";
        public string ChromiumId { get; init; } = "";
    }

    /// <summary>
    /// Installs the unpacked extension for Chromium (--load-extension + protocol command)
    /// and sideloads an XPI into Firefox profiles.
    /// </summary>
    public class ExtensionInstallService
    {
        private static readonly string[] ExtensionFiles =
        {
            "background.js", "content_script.js", "popup.js", "popup.html", "popup.css"
        };

        private static readonly string[] ChromiumExes =
        {
            "chrome.exe", "brave.exe", "msedge.exe", "chromium.exe", "vivaldi.exe", "opera.exe"
        };

        public ExtensionInstallResult InstallAll()
        {
            try
            {
                AppPaths.EnsureLayout();
                ResourceSeeder.Extract(overwriteExtension: true);

                var (chromiumId, publicKey) = ChromiumExtensionKey.GetOrCreate();

                // 1. Dosyaları gömülü kaynaklardan veya bundle klasöründen Chromium ve Firefox klasörlerine doğrudan kopyala
                EnsureExtensionFiles(AppPaths.ChromiumExtensionDir);
                EnsureExtensionFiles(AppPaths.FirefoxExtensionDir);

                WriteChromiumManifest(AppPaths.ChromiumExtensionDir, publicKey);
                WriteFirefoxManifest(AppPaths.FirefoxExtensionDir);
                PackFirefoxXpi();

                int chromiumPatched = PatchChromiumLaunchCommands();
                PatchChromiumShortcuts();
                int firefoxProfiles = SideloadFirefoxXpi();
                WriteFirefoxProfilePrefs();
                ClearFirefoxPolicies();

                return new ExtensionInstallResult
                {
                    Success = true,
                    ChromiumId = chromiumId,
                    Message =
                        "Tarayıcı eklenti dosyaları ve Native Messaging Host başarıyla hazırlandı!\n\n" +
                        $"• Chromium Eklenti Klasörü: {AppPaths.ChromiumExtensionDir}\n" +
                        $"• Chromium ID: {chromiumId}\n" +
                        $"• Firefox ID: {AppPaths.FirefoxAddonId}\n" +
                        $"• Firefox XPI: {AppPaths.FirefoxXpi}\n\n" +
                        "Chrome/Edge/Brave için: 'chrome://extensions' açıp Geliştirici Modu ile Chromium klasörünü seçin."
                };
            }
            catch (Exception ex)
            {
                return new ExtensionInstallResult { Success = false, Message = "Extension install failed: " + ex.Message };
            }
        }

        private static void EnsureExtensionFiles(string targetDir)
        {
            Directory.CreateDirectory(targetDir);

            // Önce gömülü kaynaklardan yazmayı dene
            var asm = typeof(ExtensionInstallService).Assembly;
            const string prefix = "embedded.extension.";
            bool extractedAny = false;

            foreach (string resName in asm.GetManifestResourceNames())
            {
                if (resName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    string fileName = resName[prefix.Length..];
                    string dest = Path.Combine(targetDir, fileName);
                    try
                    {
                        using Stream? src = asm.GetManifestResourceStream(resName);
                        if (src != null)
                        {
                            using var dst = File.Create(dest);
                            src.CopyTo(dst);
                            extractedAny = true;
                        }
                    }
                    catch { }
                }
            }

            // Gömülü kaynaktan çıkmadıysa diskteki konumlardan kopyala
            if (!extractedAny)
            {
                string sourceDir = FindBundledExtensionDir();
                if (!string.IsNullOrEmpty(sourceDir))
                {
                    CopySharedFiles(sourceDir, targetDir);
                }
            }
        }

        public static string FindBundledExtensionDir()
        {
            string[] candidates =
            {
                AppPaths.ExtensionBundleDir,
                Path.Combine(AppContext.BaseDirectory, "extension"),
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\extension")),
                AppPaths.ChromiumExtensionDir,
                AppPaths.FirefoxExtensionDir
            };

            foreach (var d in candidates)
            {
                if (Directory.Exists(d) && File.Exists(Path.Combine(d, "background.js")))
                    return d;
            }
            return "";
        }

        private static void CopySharedFiles(string source, string dest)
        {
            Directory.CreateDirectory(dest);
            foreach (var name in ExtensionFiles)
            {
                string src = Path.Combine(source, name);
                if (File.Exists(src))
                    File.Copy(src, Path.Combine(dest, name), overwrite: true);
            }
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
                ["version"] = "1.0.0",
                ["description"] = "Open page videos, iframes and streams directly in MPV.",
                ["key"] = publicKey,
                ["icons"] = icons,
                ["permissions"] = new[] { "nativeMessaging", "activeTab", "scripting", "tabs" },
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
                ["version"] = "1.0.0",
                ["description"] = "Open page videos, iframes and streams directly in MPV.",
                ["icons"] = icons,
                ["permissions"] = new[] { "nativeMessaging", "activeTab", "scripting", "tabs" },
                ["host_permissions"] = new[] { "<all_urls>" },
                ["action"] = new Dictionary<string, object>
                {
                    ["default_popup"] = "popup.html",
                    ["default_title"] = "MPV Launcher",
                    ["default_icon"] = icons
                },
                ["background"] = new Dictionary<string, object>
                {
                    ["scripts"] = new[] { "background.js" }
                },
                ["browser_specific_settings"] = new Dictionary<string, object>
                {
                    ["gecko"] = new Dictionary<string, object>
                    {
                        ["id"] = AppPaths.FirefoxAddonId,
                        ["strict_min_version"] = "109.0",
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
            if (File.Exists(AppPaths.FirefoxXpi))
            {
                try { File.Delete(AppPaths.FirefoxXpi); } catch { }
            }

            ZipFile.CreateFromDirectory(
                AppPaths.FirefoxExtensionDir,
                AppPaths.FirefoxXpi,
                CompressionLevel.Fastest,
                includeBaseDirectory: false);
        }

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
                    File.Copy(AppPaths.FirefoxXpi, Path.Combine(extDir, destName), overwrite: true);

                    string unpacked = Path.Combine(extDir, AppPaths.FirefoxAddonId);
                    CopySharedFiles(AppPaths.FirefoxExtensionDir, unpacked);
                    File.Copy(
                        Path.Combine(AppPaths.FirefoxExtensionDir, "manifest.json"),
                        Path.Combine(unpacked, "manifest.json"),
                        overwrite: true);
                    count++;
                }
                catch { }
            }
            return count;
        }

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
                    if (File.Exists(f)) File.Delete(f);
                }
                catch { }
            }
        }

        private static void WriteFirefoxProfilePrefs()
        {
            string profilesRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                @"Mozilla\Firefox\Profiles");
            if (!Directory.Exists(profilesRoot)) return;

            const string block =
                "\r\n// MPV Launcher\r\n" +
                "user_pref(\"extensions.autoDisableScopes\", 0);\r\n" +
                "user_pref(\"extensions.enabledScopes\", 15);\r\n" +
                "user_pref(\"extensions.sideloadScopes\", 15);\r\n" +
                "user_pref(\"xpinstall.signatures.required\", false);\r\n";

            foreach (var profile in Directory.EnumerateDirectories(profilesRoot))
            {
                try
                {
                    string userJs = Path.Combine(profile, "user.js");
                    string existing = File.Exists(userJs) ? File.ReadAllText(userJs) : "";
                    if (existing.Contains("MPV Launcher", StringComparison.Ordinal))
                    {
                        if (!existing.Contains("extensions.sideloadScopes", StringComparison.Ordinal))
                            File.AppendAllText(userJs, "user_pref(\"extensions.sideloadScopes\", 15);\r\n");
                        continue;
                    }
                    File.AppendAllText(userJs, block);
                }
                catch { }
            }
        }
    }
}
