// Original implementation, MIT licensed.
//
// Part of the main window: the torrent helper, its settings and playback.

﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using MpvLauncher.Gui.Services;


namespace MpvLauncher.Gui
{
    public partial class MainWindow
    {
        #region Torrent streaming

        private bool _torrentSettingsLoading;

        private void PopulateTorrentSettings()
        {
            _torrentSettingsLoading = true;
            try
            {
                var cfg = _configService.Config;
                ChkTorrentEnabled.IsChecked = cfg.TorrentStreamingEnabled;
                ChkTorrentRememberFile.IsChecked = cfg.TorrentRememberFile;
                TxtTorrentDownloadDir.Text = TorrentLaunchPolicy.EffectiveDownloadDirectory(cfg);
                TxtTorrentWindow.Text = cfg.TorrentForwardWindowMiB.ToString(CultureInfo.InvariantCulture);
                TxtTorrentBuffer.Text = cfg.TorrentMpvBufferMiB.ToString(CultureInfo.InvariantCulture);
                TxtTorrentUpload.Text = cfg.TorrentUploadLimitKiB.ToString(CultureInfo.InvariantCulture);
                TxtTorrentMaxConns.Text = cfg.TorrentMaxConnections.ToString(CultureInfo.InvariantCulture);
                TxtTorrentHalfOpen.Text = cfg.TorrentMaxHalfOpen.ToString(CultureInfo.InvariantCulture);
                TxtTorrentTailWait.Text = cfg.TorrentTailWaitSeconds.ToString(CultureInfo.InvariantCulture);
                TxtTorrentTrackers.Text = string.IsNullOrWhiteSpace(cfg.TorrentTrackerList)
                    ? TorrentSettings.DefaultTrackerList
                    : cfg.TorrentTrackerList;
            }
            finally
            {
                _torrentSettingsLoading = false;
            }

            UpdateTorrentHelperStatus();
        }

        private void UpdateTorrentHelperStatus()
        {
            var cfg = _configService.Config;
            if (TorrentHelperInstallService.IsInstalled)
            {
                string version = string.IsNullOrWhiteSpace(cfg.TorrentHelperVersion) ? "?" : cfg.TorrentHelperVersion;
                TxtTorrentHelperStatus.Text = _locService.Format(
                    "torrent_helper_installed", "Installed (v{0})", version);
            }
            else
            {
                TxtTorrentHelperStatus.Text = _locService.Get(
                    "torrent_helper_not_installed", "Not installed");
            }

            BtnInstallTorrentHelper.Content = _locService.Get(
                "torrent_install_helper", "Download & install");
        }

        /// <summary>
        /// Parses one numeric field. A value that is not a number falls back to
        /// the documented default; a number outside the range is clamped. The
        /// caller gets one flag for both so it can say "some values changed".
        /// </summary>
        private static int ReadTorrentInt(TextBox box, int fallback, Func<int, int> normalize, ref bool invalid)
        {
            if (!int.TryParse(box.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
            {
                invalid = true;
                value = fallback;
            }
            return normalize(value);
        }

        private void BtnSaveTorrentSettings_Click(object sender, RoutedEventArgs e)
        {
            var cfg = _configService.Config;
            bool invalid = false;

            cfg.TorrentStreamingEnabled = ChkTorrentEnabled.IsChecked == true;
            cfg.TorrentRememberFile = ChkTorrentRememberFile.IsChecked ?? true;
            cfg.TorrentDownloadDir = TxtTorrentDownloadDir.Text.Trim();
            cfg.TorrentForwardWindowMiB = ReadTorrentInt(
                TxtTorrentWindow, TorrentSettings.DefaultForwardWindowMiB, TorrentSettings.NormalizeForwardWindowMiB, ref invalid);
            cfg.TorrentMpvBufferMiB = ReadTorrentInt(
                TxtTorrentBuffer, TorrentSettings.DefaultMpvBufferMiB, TorrentSettings.NormalizeMpvBufferMiB, ref invalid);
            cfg.TorrentUploadLimitKiB = ReadTorrentInt(
                TxtTorrentUpload, TorrentSettings.DefaultUploadKiB, TorrentSettings.NormalizeUploadKiB, ref invalid);
            cfg.TorrentMaxConnections = ReadTorrentInt(
                TxtTorrentMaxConns, TorrentSettings.DefaultMaxConnections, TorrentSettings.NormalizeMaxConnections, ref invalid);
            cfg.TorrentMaxHalfOpen = ReadTorrentInt(
                TxtTorrentHalfOpen, TorrentSettings.DefaultMaxHalfOpen,
                v => TorrentSettings.NormalizeHalfOpen(v, cfg.TorrentMaxConnections), ref invalid);
            cfg.TorrentTailWaitSeconds = ReadTorrentInt(
                TxtTorrentTailWait, TorrentSettings.DefaultTailWaitSeconds, TorrentSettings.NormalizeTailWait, ref invalid);
            cfg.TorrentTrackerList = TorrentSettings.NormalizeTrackerList(TxtTorrentTrackers.Text);
            _configService.Save();
            UpdateUrlHint();

            // Show what was actually stored, so a clamped or rejected value is
            // visible instead of silently different from what was typed.
            TxtTorrentWindow.Text = cfg.TorrentForwardWindowMiB.ToString(CultureInfo.InvariantCulture);
            TxtTorrentBuffer.Text = cfg.TorrentMpvBufferMiB.ToString(CultureInfo.InvariantCulture);
            TxtTorrentUpload.Text = cfg.TorrentUploadLimitKiB.ToString(CultureInfo.InvariantCulture);
            TxtTorrentMaxConns.Text = cfg.TorrentMaxConnections.ToString(CultureInfo.InvariantCulture);
            TxtTorrentHalfOpen.Text = cfg.TorrentMaxHalfOpen.ToString(CultureInfo.InvariantCulture);
            TxtTorrentTailWait.Text = cfg.TorrentTailWaitSeconds.ToString(CultureInfo.InvariantCulture);
            TxtTorrentTrackers.Text = cfg.TorrentTrackerList;
            TxtTorrentSettingsStatus.Text = invalid
                ? _locService.Get("torrent_bad_number", "Some values were invalid and were replaced with defaults.")
                : _locService.Get("torrent_saved", "Torrent settings saved.");

            if (!cfg.TorrentStreamingEnabled && _torrentSession.IsRunning)
                _ = StopTorrentAsync(killMpv: true);
        }

        private void ChkTorrentEnabled_Changed(object sender, RoutedEventArgs e)
        {
            if (_torrentSettingsLoading) return;
            bool enabled = ChkTorrentEnabled.IsChecked == true;
            _configService.Config.TorrentStreamingEnabled = enabled;
            _configService.Save();
            UpdateUrlHint();

            if (!enabled && _torrentSession.IsRunning)
                _ = StopTorrentAsync(killMpv: true);
        }

        /// <summary>
        /// WPF text boxes have no placeholder, so the hint is a separate TextBlock
        /// shown while the box is empty. Magnet links are only named when the
        /// torrent feature is on; with it off they would be refused on paste.
        /// </summary>
        private void UpdateUrlHint()
        {
            TxtUrlHint.Text = _configService.Config.TorrentStreamingEnabled
                ? _locService.Get("input_url_hint", "Paste a URL or magnet link")
                : _locService.Get("input_url_hint_url", "Paste a video URL");
        }

        private void BtnBrowseTorrentDir_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFolderDialog
            {
                Title = _locService.Get("torrent_dir_label", "Temporary download folder"),
                InitialDirectory = TorrentLaunchPolicy.EffectiveDownloadDirectory(_configService.Config)
            };
            if (dlg.ShowDialog() == true)
                TxtTorrentDownloadDir.Text = dlg.FolderName;
        }

        private void BtnTorrentResetField_Click(object sender, RoutedEventArgs e)
        {
            string tag = (sender as Button)?.Tag?.ToString() ?? "";
            switch (tag)
            {
                case "window": TxtTorrentWindow.Text = TorrentSettings.DefaultForwardWindowMiB.ToString(CultureInfo.InvariantCulture); break;
                case "buffer": TxtTorrentBuffer.Text = TorrentSettings.DefaultMpvBufferMiB.ToString(CultureInfo.InvariantCulture); break;
                case "upload": TxtTorrentUpload.Text = TorrentSettings.DefaultUploadKiB.ToString(CultureInfo.InvariantCulture); break;
                case "conns": TxtTorrentMaxConns.Text = TorrentSettings.DefaultMaxConnections.ToString(CultureInfo.InvariantCulture); break;
                case "halfopen": TxtTorrentHalfOpen.Text = TorrentSettings.DefaultMaxHalfOpen.ToString(CultureInfo.InvariantCulture); break;
                case "tailwait": TxtTorrentTailWait.Text = TorrentSettings.DefaultTailWaitSeconds.ToString(CultureInfo.InvariantCulture); break;
                case "trackers": TxtTorrentTrackers.Text = TorrentSettings.DefaultTrackerList; break;
            }
        }

        private async void BtnInstallTorrentHelper_Click(object sender, RoutedEventArgs e)
        {
            BtnInstallTorrentHelper.IsEnabled = false;
            TxtTorrentSettingsStatus.Text = _locService.Get("torrent_helper_installing", "Downloading helper...");
            try
            {
                var progress = new Progress<DownloadProgress>(p =>
                {
                    TxtTorrentSettingsStatus.Text = p.Percent is double pct
                        ? $"{p.Stage} {pct:0}%"
                        : p.Stage;
                });

                var result = await _torrentInstaller.InstallAsync(progress);
                if (result.Success)
                {
                    _configService.Config.TorrentHelperVersion = result.Version;
                    _configService.Config.TorrentHelperSha256 = result.Sha256;
                    _configService.Save();
                    TxtTorrentSettingsStatus.Text = _locService.Format(
                        "torrent_helper_installed", "Installed (v{0})", result.Version);
                }
                else
                {
                    TxtTorrentSettingsStatus.Text = _locService.Format(
                        "torrent_helper_failed", "Helper install failed: {0}", result.Message);
                }
            }
            catch (Exception ex)
            {
                TxtTorrentSettingsStatus.Text = _locService.Format(
                    "torrent_helper_failed", "Helper install failed: {0}", ex.Message);
            }
            finally
            {
                UpdateTorrentHelperStatus();
                BtnInstallTorrentHelper.IsEnabled = true;
            }
        }

        private async Task PlayTorrentAsync(TorrentSource source)
        {
            var cfg = _configService.Config;
            var block = TorrentLaunchPolicy.Check(cfg, TorrentHelperInstallService.IsInstalled);
            if (block == TorrentLaunchBlock.FeatureDisabled)
            {
                ShowTorrentNotice("torrent_disabled",
                    "Torrent streaming is off. Open settings to enable it.",
                    "torrent_disabled_hint",
                    "Every torrent source is refused while the switch is off, and the helper is not started.");
                return;
            }
            if (block == TorrentLaunchBlock.HelperMissing)
            {
                ShowTorrentNotice("torrent_helper_missing",
                    "The mpvtorrent helper is not installed.",
                    "torrent_helper_missing_hint",
                    "Open Settings, then Torrent streaming, and install the helper.");
                return;
            }

            if (!cfg.TorrentPrivacyAccepted)
            {
                var answer = MessageBox.Show(
                    this,
                    _locService.Get("torrent_privacy_text",
                        "While a torrent streams, your IP address is visible to peers and trackers. " +
                        "Use this only for content you own or that is legal to share."),
                    _locService.Get("torrent_privacy_title", "Privacy notice"),
                    MessageBoxButton.OKCancel,
                    MessageBoxImage.Warning);
                if (answer != MessageBoxResult.OK) return;
                cfg.TorrentPrivacyAccepted = true;
                _configService.Save();
            }

            if (_torrentStarting || _torrentSession.IsRunning)
                await StopTorrentAsync(killMpv: true);

            _torrentStarting = true;
            _torrentSource = source;
            _torrentInfoHash = source.InfoHash;
            _torrentFiles = Array.Empty<TorrentFileEntry>();
            _torrentName = "";

            TxtTorrentTitle.Text = _locService.Get("torrent_card_title", "Torrent stream");
            TxtTorrentStatus.Text = _locService.Get("torrent_status_starting", "Starting the torrent helper...");
            TxtTorrentDetail.Text = TorrentSourceParser.DescribeForLog(source);
            BtnTorrentOpenSettings.Visibility = Visibility.Collapsed;
            BarTorrent.Visibility = Visibility.Visible;
            BarTorrent.Value = 0;
            CardTorrent.Visibility = Visibility.Visible;

            int? select = null;
            if (cfg.TorrentRememberFile &&
                !string.IsNullOrEmpty(source.InfoHash) &&
                cfg.TorrentLastFiles.TryGetValue(source.InfoHash, out int remembered))
            {
                select = remembered;
            }

            var request = new TorrentStartRequest(source, cfg, Environment.ProcessId, select);
            var (ok, message) = await _torrentSession.StartAsync(request);
            _torrentStarting = false;
            if (!ok)
            {
                SetTorrentError(_locService.Format(
                    "torrent_error_start", "Could not start the torrent helper: {0}", message));
            }
        }

        private void ShowTorrentNotice(string statusKey, string statusFallback, string detailKey, string detailFallback)
        {
            TxtTorrentTitle.Text = _locService.Get("torrent_card_title", "Torrent stream");
            TxtTorrentStatus.Text = _locService.Get(statusKey, statusFallback);
            TxtTorrentDetail.Text = _locService.Get(detailKey, detailFallback);
            BtnTorrentOpenSettings.Visibility = Visibility.Visible;
            BarTorrent.Visibility = Visibility.Collapsed;
            CardTorrent.Visibility = Visibility.Visible;
            TxtStatus.Text = TxtTorrentStatus.Text;
        }

        private void OnTorrentEvent(TorrentHelperEvent e)
        {
            switch (e.Type)
            {
                case "source":
                    if (!string.IsNullOrEmpty(e.InfoHash)) _torrentInfoHash = e.InfoHash;
                    break;

                case "metadata":
                    if (!string.IsNullOrEmpty(e.Name))
                    {
                        _torrentName = e.Name!;
                        TxtTorrentTitle.Text = e.Name;
                    }
                    if (e.Files != null) _torrentFiles = e.Files;
                    break;

                case "select_required":
                    _ = PromptTorrentFileAsync();
                    break;

                case "hint":
                    TxtTorrentDetail.Text = _locService.Get(
                        "torrent_hint_still_searching",
                        "Still looking for peers. A restrictive network can slow this down.");
                    break;

                case "error":
                    if (e.Fatal)
                        SetTorrentError(_locService.Format(
                            "torrent_error_helper", "Torrent helper error: {0}", e.Message));
                    else if (!string.IsNullOrEmpty(e.Message))
                        TxtTorrentDetail.Text = e.Message;
                    break;

                case "state":
                    RenderTorrentState(e);
                    break;
            }
        }

        private void RenderTorrentState(TorrentHelperEvent e)
        {
            string status = e.Phase switch
            {
                "metadata" => _locService.Get("torrent_status_metadata", "Reading torrent metadata..."),
                "select" => _locService.Get("torrent_status_select", "Choose a file to play."),
                "preload" => _locService.Get("torrent_status_preload", "Buffering the beginning of the file..."),
                "playing" => _locService.Get("torrent_status_playing", "Streaming from the local endpoint..."),
                _ => _locService.Get("torrent_status_searching", "Searching for peers...")
            };
            TxtTorrentStatus.Text = status;
            TxtTorrentDetail.Text = _locService.Format(
                "torrent_peer_detail",
                "{0} peers · {1}/s · {2} MB received",
                e.Peers,
                FormatTorrentSpeed(e.DownloadSpeed),
                (e.Received / 1048576.0).ToString("0.0", CultureInfo.CurrentCulture));

            double progress = Math.Clamp(e.Progress, 0, 1);
            BarTorrent.Value = progress;
        }

        private static string FormatTorrentSpeed(long bytesPerSecond)
        {
            if (bytesPerSecond >= 1048576)
                return (bytesPerSecond / 1048576.0).ToString("0.0", CultureInfo.CurrentCulture) + " MB";
            if (bytesPerSecond >= 1024)
                return (bytesPerSecond / 1024.0).ToString("0.0", CultureInfo.CurrentCulture) + " KB";
            return bytesPerSecond.ToString(CultureInfo.CurrentCulture) + " B";
        }

        private async Task PromptTorrentFileAsync()
        {
            if (_torrentFiles.Count == 0) return;

            var dlg = new TorrentFilePickerDialog(
                _torrentFiles,
                string.IsNullOrWhiteSpace(_torrentName) ? TxtTorrentTitle.Text : _torrentName,
                _locService.Get) { Owner = this };
            if (dlg.ShowDialog() == true)
            {
                await _torrentSession.SendSelectAsync(dlg.SelectedIndex);
            }
            else
            {
                await StopTorrentAsync(killMpv: true);
            }
        }

        private void OnTorrentReady(TorrentHelperEvent e)
        {
            if (string.IsNullOrEmpty(e.Url)) return;

            var cfg = _configService.Config;
            var mpvArgs = TorrentHelperArguments.BuildMpvArguments(
                cfg.TorrentMpvBufferMiB,
                TorrentSettings.NormalizeCachePauseWait(TorrentSettings.DefaultCachePauseWaitSeconds));

            // The card names the torrent; mpv and the history name the file that
            // is actually playing. In a multi-file torrent the torrent name says
            // nothing about which episode this is, so it is not repeated here.
            string title = string.IsNullOrWhiteSpace(e.DisplayName)
                ? (e.FileName ?? "")
                : e.DisplayName!;

            var res = _procService.PlayMedia(e.Url, mpvArgs, title, addHistory: false);
            if (!res.Success || res.Process == null)
            {
                SetTorrentError(_locService.Get("torrent_error_mpv", "mpv could not start for this torrent."));
                _ = StopTorrentAsync(killMpv: false);
                return;
            }

            // The local URL is ephemeral and carries a session token, so the
            // history keeps the torrent source instead.
            if (_torrentSource != null)
                _configService.AddHistory(_torrentSource.Value, title);

            if (cfg.TorrentRememberFile && !string.IsNullOrEmpty(_torrentInfoHash))
            {
                cfg.TorrentLastFiles[_torrentInfoHash] = e.FileIndex;
                _configService.Save();
            }

            _torrentMpv = res.Process;
            _torrentMpv.EnableRaisingEvents = true;
            _torrentMpv.Exited += (_, _) => Dispatcher.InvokeAsync(async () =>
            {
                await StopTorrentAsync(killMpv: false);
            });

            TxtTorrentStatus.Text = _locService.Get("torrent_status_playing", "Streaming from the local endpoint...");
            BarTorrent.Value = 1;
            LoadHistory();
        }

        private void OnTorrentExited(int exitCode)
        {
            _torrentStarting = false;
            if (_torrentStopping) return;
            if (CardTorrent.Visibility == Visibility.Visible)
            {
                SetTorrentError(_locService.Format(
                    "torrent_error_helper_exit",
                    "The torrent helper stopped unexpectedly (exit code {0}).",
                    exitCode));
            }
            _torrentMpv = null;
        }

        private void SetTorrentError(string message)
        {
            TxtTorrentStatus.Text = _locService.Get("torrent_status_failed", "Torrent stream failed.");
            TxtTorrentDetail.Text = message;
            TxtStatus.Text = message;
            BtnTorrentOpenSettings.Visibility = Visibility.Collapsed;
            BarTorrent.Visibility = Visibility.Collapsed;
            CardTorrent.Visibility = Visibility.Visible;
        }

        private async Task StopTorrentAsync(bool killMpv)
        {
            if (_torrentStopping) return;
            _torrentStopping = true;
            try
            {
                var mpv = _torrentMpv;
                _torrentMpv = null;
                if (killMpv && mpv != null)
                {
                    try { if (!mpv.HasExited) mpv.Kill(entireProcessTree: true); }
                    catch { }
                }

                await _torrentSession.StopAsync();
                CardTorrent.Visibility = Visibility.Collapsed;
                _torrentFiles = Array.Empty<TorrentFileEntry>();
                _torrentName = "";
                _torrentSource = null;
                _torrentInfoHash = "";
            }
            finally
            {
                _torrentStopping = false;
                _torrentStarting = false;
            }
        }

        private void BtnTorrentCancel_Click(object sender, RoutedEventArgs e) =>
            _ = StopTorrentAsync(killMpv: true);

        private void BtnTorrentOpenSettings_Click(object sender, RoutedEventArgs e)
        {
            SetActiveTab(PanelSettings, BtnNavSettings);
            ExpTorrentAdvanced.IsExpanded = true;
            ChkTorrentEnabled.Focus();
        }

        #endregion
    }
}
