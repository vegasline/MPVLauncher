// Original implementation, MIT licensed.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using MpvLauncher.Gui.Services;

namespace MpvLauncher.Gui
{
    /// <summary>
    /// The optional browser relay: one card on the Settings tab.
    ///
    /// The relay is a separate program. The viewer never has to find or download
    /// it: turning the switch on, or any later launch with the switch on, installs
    /// the helper and keeps it on the newest published release the same way the
    /// player and the torrent helper are kept current.
    /// </summary>
    public partial class MainWindow
    {
        private RelayInstallService? _relayInstaller;
        private bool _relayUpdateRunning;

        private RelayInstallService RelayInstaller => _relayInstaller ??= new RelayInstallService(_configService);

        // The card has labels and buttons side by side, so the setter is split by
        // target type: the localizer returns text, the control takes it in Text or
        // in Content.
        private void Loc(System.Windows.Controls.TextBlock target, string key, string fallback)
            => target.Text = _locService.Get(key, fallback);

        private void Loc(System.Windows.Controls.ContentControl target, string key, string fallback)
            => target.Content = _locService.Get(key, fallback);

        /// <summary>
        /// Fills the relay card from the saved configuration. Called once from the
        /// window constructor, after the other tabs are populated.
        /// </summary>
        private void PopulateRelaySettings()
        {
            Loc(TxtRelaySettingsTitle, "relay_settings_title", "Browser relay (optional)");
            Loc(TxtRelaySettingsDesc, "relay_settings_desc",
                "Off by default. When enabled, streams the site's own player refuses to hand over are fetched through the browser tab you already have open and handed to mpv.");
            Loc(TxtRelayHowTitle, "relay_how_title", "What the helper does");
            Loc(TxtRelayHowText, "relay_how_text",
                "Some sites keep the real stream URL behind their own player script. The helper is a small local program that asks the open tab for that URL, copies the tab's own request headers, and serves the result to mpv from 127.0.0.1. It runs only on your machine, only while a relayed stream is playing. The launcher installs it automatically and updates it with the app.");
            Loc(ChkRelayEnabled, "relay_enable", "Enable browser relay");
            Loc(TxtRelayPrivacy, "relay_privacy_text",
                "The relay fetches only through the tab you press relay on, using that tab's own session. It never reads or stores cookies or tokens.");
            Loc(TxtRelayHelperTitle, "relay_helper_label", "Helper program (mpvrelay)");
            Loc(TxtRelayBlacklistTitle, "relay_blacklist_title", "Never relay these sites");
            Loc(TxtRelayBlacklistDesc, "relay_blacklist_desc",
                "One host per entry. A site on this list always opens directly, even when the relay is on.");
            Loc(BtnAddRelayBlacklist, "relay_blacklist_add", "Add");
            Loc(BtnRemoveRelayBlacklist, "relay_blacklist_remove", "Remove selected");
            Loc(BtnClearRelayHosts, "relay_forget_hosts", "Forget learned sites");

            ChkRelayEnabled.IsChecked = _configService.Config.RelayEnabled;
            RefreshRelayBlacklist();
            UpdateRelayHelperStatus();
        }

        /// <summary>
        /// Shows the installed helper version, or says that it will be installed
        /// on the next relay launch. There is no install button on purpose.
        /// </summary>
        private void UpdateRelayHelperStatus()
        {
            if (RelayInstallService.IsInstalled)
            {
                string version = RelayInstaller.InstalledVersion;
                TxtRelayHelperStatus.Text = string.IsNullOrWhiteSpace(version)
                    ? _locService.Get("relay_helper_installed_plain", "Installed and kept up to date automatically.")
                    : _locService.Format("relay_helper_installed", "Installed (v{0}), kept up to date automatically.", version);
            }
            else
            {
                TxtRelayHelperStatus.Text = _locService.Get("relay_helper_auto_install",
                    "Not installed yet. It is downloaded and installed automatically on the next relay use.");
            }

            TxtRelaySettingsStatus.Text = _configService.Config.RelayEnabled
                ? _locService.Get("relay_enabled_note", "Browser relay is on.")
                : _locService.Get("relay_disabled_note", "Browser relay is off.");
        }

        private void RefreshRelayBlacklist()
        {
            var hosts = new List<string>(_configService.Config.RelayBlacklist ?? new List<string>());
            LstRelayBlacklist.ItemsSource = hosts;
        }

        private void ChkRelayEnabled_Changed(object sender, RoutedEventArgs e)
        {
            _configService.Config.RelayEnabled = ChkRelayEnabled.IsChecked == true;
            _configService.Save();
            UpdateRelayHelperStatus();

            // Turning the switch on is the moment the helper becomes visible, so
            // the download starts here instead of behind a button.
            if (_configService.Config.RelayEnabled) _ = EnsureRelayHelperAsync();
        }

        /// <summary>
        /// Adds the typed host to the blacklist. A pasted URL is reduced to its
        /// host, so "https://www.YouTube.com/watch?v=1" and "youtube.com" are the
        /// same entry, which is how the router compares them.
        /// </summary>
        private void BtnAddRelayBlacklist_Click(object sender, RoutedEventArgs e)
        {
            string entry = NormalizeHost(TxtRelayBlacklistEntry.Text);
            if (entry.Length == 0)
            {
                TxtRelaySettingsStatus.Text = _locService.Get("relay_blacklist_invalid", "Enter a site name, for example youtube.com.");
                return;
            }

            var hosts = new List<string>(_configService.Config.RelayBlacklist ?? new List<string>());
            if (hosts.Any(h => string.Equals(NormalizeHost(h), entry, StringComparison.Ordinal)))
            {
                TxtRelaySettingsStatus.Text = _locService.Format("relay_blacklist_present", "{0} is already on the list.", entry);
                return;
            }

            hosts.Add(entry);
            _configService.Config.RelayBlacklist = hosts;
            _configService.Save();
            TxtRelayBlacklistEntry.Text = "";
            RefreshRelayBlacklist();
            TxtRelaySettingsStatus.Text = _locService.Format("relay_blacklist_added", "{0} is never relayed.", entry);
        }

        private void BtnRemoveRelayBlacklist_Click(object sender, RoutedEventArgs e)
        {
            if (LstRelayBlacklist.SelectedItem is not string selected) return;

            var hosts = new List<string>(_configService.Config.RelayBlacklist ?? new List<string>());
            hosts.RemoveAll(h => string.Equals(NormalizeHost(h), NormalizeHost(selected), StringComparison.Ordinal));
            _configService.Config.RelayBlacklist = hosts;
            _configService.Save();
            RefreshRelayBlacklist();
            TxtRelaySettingsStatus.Text = _locService.Format("relay_blacklist_removed", "{0} removed from the list.", NormalizeHost(selected));
        }

        /// <summary>
        /// Reduces whatever was typed or pasted to the host form the router uses:
        /// no scheme, no path, no "www.", lower case.
        /// </summary>
        private static string NormalizeHost(string? value)
        {
            string text = (value ?? "").Trim();
            if (text.Length == 0) return "";
            if (text.Contains("://", StringComparison.Ordinal))
            {
                text = text.Substring(text.IndexOf("://", StringComparison.Ordinal) + 3);
            }
            int cut = text.IndexOfAny(new[] { '/', '?', '#' });
            if (cut >= 0) text = text.Substring(0, cut);
            int at = text.LastIndexOf('@');
            if (at >= 0) text = text.Substring(at + 1);
            int colon = text.IndexOf(':');
            if (colon >= 0) text = text.Substring(0, colon);
            text = text.Trim().TrimStart('.').ToLowerInvariant();
            if (text.StartsWith("www.", StringComparison.Ordinal)) text = text.Substring(4);
            return text;
        }

        private void BtnClearRelayHosts_Click(object sender, RoutedEventArgs e)
        {
            int count = _configService.Config.RelayLearnedHosts?.Count ?? 0;
            _configService.Config.RelayLearnedHosts = new List<string>();
            _configService.Save();
            TxtRelaySettingsStatus.Text = count > 0
                ? _locService.Format("relay_hosts_cleared", "Forgot {0} learned sites.", count)
                : _locService.Get("relay_hosts_none", "No learned sites to forget.");
        }

        /// <summary>
        /// Installs or updates the helper without being asked. Safe to call on
        /// every launch: when the installed copy is current it costs one GitHub
        /// query and nothing else.
        /// </summary>
        private async System.Threading.Tasks.Task EnsureRelayHelperAsync()
        {
            if (_relayUpdateRunning) return;
            if (!_configService.Config.RelayEnabled) return;

            _relayUpdateRunning = true;
            try
            {
                var progress = new Progress<string>(stage => TxtRelayHelperStatus.Text = stage switch
                {
                    "release_lookup" => _locService.Get("relay_helper_stage_lookup", "Checking for the newest helper release..."),
                    "download" => _locService.Get("relay_helper_stage_download", "Downloading the relay helper..."),
                    "verify" => _locService.Get("relay_helper_stage_verify", "Verifying the relay helper..."),
                    _ => TxtRelayHelperStatus.Text
                });

                RelayInstallResult? result = await RelayInstaller
                    .EnsureInstalledAsync(progress, CancellationToken.None)
                    .ConfigureAwait(true);

                UpdateRelayHelperStatus();
                if (result != null)
                {
                    TxtRelayHelperStatus.Text = _locService.Format("relay_helper_updated",
                        "Helper updated to v{0}.", result.Version);
                }
            }
            catch (Exception ex)
            {
                TxtRelayHelperStatus.Text = _locService.Format("relay_helper_failed",
                    "Relay helper could not be installed: {0}", ex.Message);
            }
            finally
            {
                _relayUpdateRunning = false;
            }
        }
    }
}
