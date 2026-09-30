using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace MpvLauncher.Gui.Services
{
    /// <summary>
    /// Registers this executable as a native messaging host for every supported
    /// browser, and is the entry point for installing or removing the extension.
    ///
    /// Both browsers use the same JSON manifest shape but allow-list different
    /// callers: Chromium matches <c>allowed_origins</c> against the calling
    /// extension's origin, Firefox matches <c>allowed_extensions</c> against the
    /// add-on id. Both lists are part of the trust boundary - a Chromium
    /// manifest listing the wrong origin would let that extension start mpv with
    /// a URL of its choosing.
    ///
    /// Only HKCU is written and no elevation is requested, so nothing here needs
    /// administrator rights.
    /// </summary>
    public class BrowserIntegrationService
    {
        /// <summary>
        /// Path recorded in the manifests. Environment.ProcessPath is the real
        /// executable even when the app runs from bin\, so moving the folder only
        /// requires RepairNativeHostPath rather than a full reinstall.
        /// </summary>
        private static string ExePath =>
            Environment.ProcessPath
            ?? Path.Combine(AppContext.BaseDirectory, "MPVLauncher.exe");

        private static string HostExePath => ExePath;

        /// <summary>
        /// Full setup: extension files first, then both native host registrations.
        ///
        /// Returns the same structured result the file-level service produces, so
        /// the caller renders one localized summary covering both halves. The
        /// Chromium manifest has to be written after the extension files exist,
        /// because it allow-lists the id they were installed under.
        /// </summary>
        public ExtensionInstallResult InstallAll()
        {
            var ext = new ExtensionInstallService().InstallAll();
            if (!ext.Success) return ext;

            var ff = InstallFirefox();
            var cr = InstallChromium(ext.ChromiumId);

            var details = new List<InstallDetail>(ext.Details)
            {
                new() { Key = "install_item_host_firefox",  Args = new object[] { ff.ManifestPath } },
                new() { Key = "install_item_host_chromium", Args = new object[] { cr.ManifestPath } },
            };

            string error = "";
            if (!ff.Success) error = ff.Error;
            else if (!cr.Success) error = cr.Error;

            return new ExtensionInstallResult
            {
                Success = ff.Success && cr.Success,
                Error = error,
                ChromiumId = ext.ChromiumId,
                Details = details,
            };
        }

        /// <summary>
        /// Removes the extension files, XPI, native messaging hosts and launch
        /// command patches. Returns a structured result so the UI can localise
        /// each line itself.
        /// </summary>
        public UninstallResult UninstallAll()
        {
            return new ExtensionInstallService().UninstallAll();
        }

        /// <summary>
        /// Writes the Firefox host manifest and points our add-on id at it.
        ///
        /// Both the native and Wow6432Node views are registered because a 32-bit
        /// Firefox reads the redirected key. Firefox identifies the caller by
        /// add-on id rather than origin, which is why the list has exactly one
        /// entry: the id from AppPaths.
        /// </summary>
        public (bool Success, string Error, string ManifestPath) InstallFirefox()
        {
            // Declared outside the try so the failure path can still report which
            // manifest was being written.
            string jsonPath = AppPaths.FirefoxManifest;
            try
            {
                AppPaths.EnsureLayout();
                jsonPath = AppPaths.FirefoxManifest;

                var manifest = new
                {
                    name = "com.mpv.launcher",
                    description = "MPV Native Messaging Host",
                    path = HostExePath,
                    type = "stdio",
                    allowed_extensions = new[] { AppPaths.FirefoxAddonId }
                };

                File.WriteAllText(jsonPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

                using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Mozilla\NativeMessagingHosts\com.mpv.launcher"))
                    key?.SetValue("", jsonPath);
                using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Wow6432Node\Mozilla\NativeMessagingHosts\com.mpv.launcher"))
                    key?.SetValue("", jsonPath);

                return (true, "", jsonPath);
            }
            catch (Exception ex)
            {
                return (false, ex.Message, jsonPath);
            }
        }

        /// <summary>
        /// Writes the Chromium host manifest and registers it for every
        /// Chromium-based browser.
        ///
        /// Chromium identifies the caller by extension origin, so the allow-list
        /// has to contain every id this extension might answer to. Three can
        /// legitimately occur:
        ///
        ///   - the id reported by the install that just ran;
        ///   - the id derived from the persisted RSA key, which is what a working
        ///     install always produces;
        ///   - the id Chromium derives from the unpacked folder path, used when
        ///     the manifest has no "key" field.
        ///
        /// The two literal ids are earlier builds that shipped before the key was
        /// persisted; browsers that still hold a grant from those installs would
        /// otherwise be refused. They are kept because removing them breaks a
        /// working pairing, not because they are trusted by default: Chromium
        /// still requires the user to have granted this host to that extension.
        /// </summary>
        public (bool Success, string Error, string ManifestPath) InstallChromium(string? extensionId = null)
        {
            string jsonPath = AppPaths.ChromeManifest;
            try
            {
                AppPaths.EnsureLayout();
                jsonPath = AppPaths.ChromeManifest;

                var origins = new List<string>();
                void AddId(string? id)
                {
                    if (string.IsNullOrWhiteSpace(id)) return;
                    string o = $"chrome-extension://{id.Trim()}/";
                    if (!origins.Contains(o)) origins.Add(o);
                }

                AddId(extensionId);
                try
                {
                    var keyPair = ChromiumExtensionKey.GetOrCreate();
                    AddId(keyPair.Id);
                }
                catch { /* the path-derived id below is the fallback */ }

                AddId(ChromiumExtensionKey.FromUnpackedPath(AppPaths.ChromiumExtensionDir));

                foreach (var id in new[]
                {
                    "dlmoomecgahphjiibfnhdnodfdggcakb",
                    "jbjpfmglemfbpndbjaagndcnbigfakcj"
                })
                    AddId(id);

                var manifest = new
                {
                    name = "com.mpv.launcher",
                    description = "MPV Native Messaging Host",
                    path = HostExePath,
                    type = "stdio",
                    allowed_origins = origins.ToArray()
                };

                File.WriteAllText(jsonPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

                // Per-user keys, so no browser needs elevation to register.
                string[] targets =
                {
                    @"Software\Google\Chrome\NativeMessagingHosts\com.mpv.launcher",
                    @"Software\BraveSoftware\Brave-Browser\NativeMessagingHosts\com.mpv.launcher",
                    @"Software\Microsoft\Edge\NativeMessagingHosts\com.mpv.launcher",
                    @"Software\Chromium\NativeMessagingHosts\com.mpv.launcher",
                    @"Software\Opera Software\Opera Stable\NativeMessagingHosts\com.mpv.launcher",
                    @"Software\Opera Software\Opera GX Stable\NativeMessagingHosts\com.mpv.launcher",
                    @"Software\Vivaldi\NativeMessagingHosts\com.mpv.launcher",
                    @"Software\Yandex\YandexBrowser\NativeMessagingHosts\com.mpv.launcher"
                };

                foreach (var t in targets)
                {
                    using var key = Registry.CurrentUser.CreateSubKey(t);
                    key?.SetValue("", jsonPath);
                }

                return (true, "", jsonPath);
            }
            catch (Exception ex)
            {
                return (false, ex.Message, jsonPath);
            }
        }

        /// <summary>
        /// Rewrites the recorded executable path in both manifests if the app has
        /// moved. Cheaper than a reinstall and enough to fix a host that fails
        /// with "specified native messaging host not found".
        /// </summary>
        public void RepairNativeHostPath()
        {
            try
            {
                RepairManifestPath(AppPaths.FirefoxManifest);
                RepairManifestPath(AppPaths.ChromeManifest);
            }
            catch { }
        }

        /// <summary>
        /// Updates only the "path" field, leaving name, type and the allow-lists
        /// exactly as they were. Rewriting the whole manifest from scratch here
        /// would risk dropping an allow-list entry the browser depends on.
        /// </summary>
        private static void RepairManifestPath(string jsonPath)
        {
            if (!File.Exists(jsonPath)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
            if (!doc.RootElement.TryGetProperty("path", out var p)) return;
            string? current = p.GetString();
            if (string.Equals(current, HostExePath, StringComparison.OrdinalIgnoreCase)) return;

            var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(jsonPath));
            if (dict == null) return;

            var rebuilt = new Dictionary<string, object?>();
            foreach (var kv in dict)
            {
                if (kv.Key == "path")
                    rebuilt[kv.Key] = HostExePath;
                else
                    rebuilt[kv.Key] = JsonSerializer.Deserialize<object>(kv.Value.GetRawText());
            }

            File.WriteAllText(jsonPath, JsonSerializer.Serialize(rebuilt, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
