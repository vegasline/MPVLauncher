using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace MpvLauncher.Gui.Services
{
    public class BrowserIntegrationService
    {
        private static string ExePath =>
            Environment.ProcessPath
            ?? Path.Combine(AppContext.BaseDirectory, "MPVLauncher.exe");

        private static string HostExePath => ExePath;

        public (bool Success, string Message, string ChromiumId) InstallAll()
        {
            var ext = new ExtensionInstallService().InstallAll();
            var ff = InstallFirefox();
            var cr = InstallChromium(ext.ChromiumId);

            bool ok = ext.Success && ff.Success && cr.Success;
            string msg =
                ext.Message + Environment.NewLine + Environment.NewLine +
                ff.Message + Environment.NewLine + Environment.NewLine +
                cr.Message;

            return (ok, msg, ext.ChromiumId);
        }

        public (bool Success, string Message) InstallFirefox()
        {
            try
            {
                AppPaths.EnsureLayout();
                string jsonPath = AppPaths.FirefoxManifest;

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

                return (true, $"Firefox native host registered.\nHost: {HostExePath}\nManifest: {jsonPath}");
            }
            catch (Exception ex)
            {
                return (false, "Firefox native host error: " + ex.Message);
            }
        }

        public (bool Success, string Message) InstallChromium(string? extensionId = null)
        {
            try
            {
                AppPaths.EnsureLayout();
                string jsonPath = AppPaths.ChromeManifest;

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
                catch { }

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

                return (true, $"Chromium native host registered.\nHost: {HostExePath}\nManifest: {jsonPath}");
            }
            catch (Exception ex)
            {
                return (false, "Chromium native host error: " + ex.Message);
            }
        }

        /// <summary>
        /// Rewrite native-host path if this executable moved.
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
