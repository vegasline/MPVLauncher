// Original implementation, MIT licensed.
//
// Part of the main window: dependency checks, browser integration and the tools tab.

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
        #region Dependencies & browser
        /// <summary>
        /// Probes mpv, yt-dlp and ffmpeg for their versions.
        ///
        /// Each check starts a process, so all three run together on a worker
        /// thread; doing them one at a time on the UI thread would freeze the
        /// window for as long as the slowest tool takes to answer.
        /// </summary>
        private async Task RefreshDependenciesAsync()
        {
            TxtMpvStatus.Text = "Checking MPV...";
            TxtYtdlStatus.Text = "Checking yt-dlp...";
            TxtFfmpegStatus.Text = "Checking FFmpeg...";

            try
            {
                var status = await Task.Run(() => (
                    Mpv: _depService.CheckMpvStatus(),
                    Ytdl: _depService.CheckYtdlStatus(),
                    Ffmpeg: _depService.CheckFfmpegStatus()));

                TxtMpvStatus.Text = CompactToolStatus(status.Mpv);
                TxtYtdlStatus.Text = CompactToolStatus(status.Ytdl);
                TxtFfmpegStatus.Text = CompactToolStatus(status.Ffmpeg);
            }
            catch (Exception ex)
            {
                TxtMpvStatus.Text = "MPV check failed";
                TxtYtdlStatus.Text = "yt-dlp check failed";
                TxtFfmpegStatus.Text = "FFmpeg check failed";
                TxtStatus.Text = "Dependency check failed: " + ex.Message;
            }
        }

        private async void BtnRefreshTools_Click(object sender, RoutedEventArgs e) => await RefreshDependenciesAsync();

        /// <summary>
        /// First line of a tool's version output, cut to a length that fits the
        /// tile. mpv prints a multi-line copyright banner and yt-dlp prints a
        /// changelog URL; the tile only has room for one line, and the version
        /// number was the part being pushed out of sight by the rest.
        /// </summary>
        private static string CompactToolStatus(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";

            string first = text
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault() ?? "";

            // mpv and ffmpeg append their copyright banner to the same line;
            // it is noise in a 12px tile, and dropping it here is what lets the
            // version number stay visible.
            int copyright = first.IndexOf(" Copyright", StringComparison.OrdinalIgnoreCase);
            if (copyright > 0) first = first[..copyright].TrimEnd();

            return first.Length <= 90 ? first : first[..87] + "...";
        }

        /// <summary>
        /// Brings browser integration up to date at startup.
        ///
        /// On a first run the full install runs. Afterwards only the recorded
        /// host path is repaired, because a full install writes registry values
        /// and rewrites shortcuts and there is no reason to redo that on every
        /// launch - the user has explicit buttons for it.
        ///
        /// Runs on a worker thread: it writes files and registry keys and would
        /// otherwise stall the window during startup.
        /// </summary>
        private async Task InitializeBrowserIntegrationAsync()
        {
            TxtExtensionStatus.Text = _locService.Get("install_checking", "Checking browser integration...");

            try
            {
                bool firstRun = _configService.Config.FirstRun;
                var result = await Task.Run(() =>
                {
                    // The host manifests are ensured on every launch, since a
                    // missing one would silently break a hand-loaded extension
                    // and a moved app folder leaves a stale path behind. A full
                    // install is only needed on a first run.
                    _browserService.EnsureNativeHost();
                    return firstRun
                        ? _browserService.InstallAll()
                        : null;
                });

                if (result == null)
                {
                    TxtExtensionStatus.Text = _locService.Get("install_repair_hint",
                        "Native messaging host path checked. Use Install / repair if the extension is missing.");
                    return;
                }

                // A first run shows the full summary so the paths and ids are on
                // screen; later launches keep the status line short.
                TxtExtensionStatus.Text = result.Success
                    ? (firstRun ? BuildInstallMessage(result)
                                : _locService.Get("ext_installed",
                                    "Browser extensions and native messaging registered."))
                    : _locService.Get("install_error", "Browser extension install failed:") + " " + result.Error;

                if (firstRun && result.Success)
                {
                    _configService.Config.FirstRun = false;
                    _configService.Save();
                }
            }
            catch (Exception ex)
            {
                TxtExtensionStatus.Text = _locService.Get("install_error",
                    "Browser extension install failed:") + " " + ex.Message;
            }
        }

        private async void BtnCheckToolUpdates_Click(object sender, RoutedEventArgs e)
        {
            BtnCheckToolUpdates.IsEnabled = false;
            TxtStatus.Text = _locService.Get("tools_updates_checking", "Checking program updates...");

            try
            {
                // GitHub release lookups and the three --version probes are all
                // blocking work; none of it may run on the UI thread.
                var items = await Task.Run(() => _toolUpdateService.CheckAllAsync());

                TxtStatus.Text = items.All(i => i.State == ToolUpdateState.UpToDate)
                    ? _locService.Get("tools_updates_all_current", "All three programs are up to date.")
                    : "";

                var dialog = new ToolUpdatesDialog(
                    items,
                    _locService.Get,
                    async (kind, progress) =>
                    {
                        var result = await _downloadService.InstallAsync(kind, progress);
                        if (result.Success) await RefreshDependenciesAsync();
                        return result;
                    })
                {
                    Owner = this
                };

                dialog.ShowDialog();
            }
            catch (Exception ex)
            {
                TxtStatus.Text = _locService.Get("tools_updates_failed", "Check failed") + ": " + ex.Message;
            }
            finally
            {
                BtnCheckToolUpdates.IsEnabled = true;
            }
        }

        private async void BtnInstallMpv_Click(object sender, RoutedEventArgs e)
            => await RunInstallAsync(ToolKind.Mpv);

        private async void BtnInstallYtdlp_Click(object sender, RoutedEventArgs e)
            => await RunInstallAsync(ToolKind.YtDlp);

        private async void BtnInstallFfmpeg_Click(object sender, RoutedEventArgs e)
            => await RunInstallAsync(ToolKind.Ffmpeg);

        /**
         * Downloads and installs one tool, driving the progress bar.
         *
         * Re-entrancy is blocked by _isInstalling and the buttons are disabled
         * for the duration: two installs at once would race over the same
         * destination folder and leave a half-written binary behind.
         */
        private async System.Threading.Tasks.Task RunInstallAsync(ToolKind kind)
        {
            if (_isInstalling) return;
            _isInstalling = true;
            SetInstallButtonsEnabled(false);

            string label = kind switch
            {
                ToolKind.Mpv => "MPV",
                ToolKind.YtDlp => "yt-dlp",
                ToolKind.Ffmpeg => "FFmpeg",
                _ => "Tool"
            };

            TxtStatus.Text = $"Downloading {label}...";
            TxtDownloadStage.Text = "Starting...";
            TxtDownloadPct.Text = "";
            TxtDownloadDetail.Text = "";
            BarDownload.IsIndeterminate = true;
            BarDownload.Value = 0;

            var progress = new Progress<DownloadProgress>(p =>
            {
                TxtDownloadStage.Text = p.Stage;
                TxtDownloadDetail.Text = p.Detail ?? "";
                if (p.Percent is double pct)
                {
                    BarDownload.IsIndeterminate = false;
                    BarDownload.Value = pct;
                    TxtDownloadPct.Text = $"{pct:0}%";
                }
                else
                {
                    BarDownload.IsIndeterminate = true;
                    TxtDownloadPct.Text = "";
                }
            });

            var res = await _downloadService.InstallAsync(kind, progress);

            BarDownload.IsIndeterminate = false;
            if (res.Success)
            {
                BarDownload.Value = 100;
                TxtDownloadPct.Text = "100%";
                TxtDownloadStage.Text = "Done";
                TxtStatus.Text = res.Message;
            }
            else
            {
                TxtDownloadStage.Text = "Error";
                TxtDownloadDetail.Text = res.Message;
                TxtStatus.Text = res.Message;
            }

            await RefreshDependenciesAsync();
            SetInstallButtonsEnabled(true);
            _isInstalling = false;
        }

        private void SetInstallButtonsEnabled(bool enabled)
        {
            BtnInstallMpv.IsEnabled = enabled;
            BtnInstallYtdlp.IsEnabled = enabled;
            BtnInstallFfmpeg.IsEnabled = enabled;
        }

        /// <summary>
        /// Install / repair. Always performs a complete install, so it doubles as
        /// the repair action: running it again rewrites every file, manifest and
        /// registry entry from scratch rather than skipping what already exists.
        ///
        /// The work happens on a worker thread because it copies files, rewrites
        /// shortcuts and writes several registry keys.
        /// </summary>
        private async void BtnInstallExtensions_Click(object sender, RoutedEventArgs e)
        {
            BtnInstallExtensions.IsEnabled = false;
            TxtExtensionStatus.Text = "Installing browser extension...";
            TxtStatus.Text = "Installing browser extension...";

            try
            {
                var res = await Task.Run(() => _browserService.InstallAll());

                string message = res.Success
                    ? BuildInstallMessage(res)
                    : _locService.Get("install_error", "Browser extension install failed:") + " " + res.Error;

                TxtExtensionStatus.Text = message;
                TxtStatus.Text = res.Success
                    ? _locService.Get("ext_install_ready",
                                      "Extension files are ready. Load the Chromium folder in your browser once.")
                    : _locService.Get("ext_install_error", "Failed to install browser extensions. Please try again.");

                MessageBox.Show(message, _locService.Get("app_title", "MPV Launcher"), MessageBoxButton.OK,
                    res.Success ? MessageBoxImage.Information : MessageBoxImage.Error);
            }
            catch (Exception ex)
            {
                string msg = _locService.Get("install_error", "Browser extension install failed:") + " " + ex.Message;
                TxtExtensionStatus.Text = msg;
                TxtStatus.Text = msg;
                MessageBox.Show(msg, _locService.Get("app_title", "MPV Launcher"), MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                BtnInstallExtensions.IsEnabled = true;
            }
        }

        /// <summary>
        /// Renders the install summary from the service's structured result.
        ///
        /// Mirrors BuildUninstallMessage: the service hands over keys and values,
        /// and every sentence is resolved here so it comes from the active
        /// language file. The follow-up hint is kept because an install is not
        /// finished until the user has actually loaded the unpacked folder.
        /// </summary>
        private string BuildInstallMessage(ExtensionInstallResult res)
        {
            string head = _locService.Get(
                "install_success_head",
                "Browser extension files and the native messaging host are ready.");

            var bullet = Environment.NewLine + "• ";
            var lines = res.Details
                .Select(d => _locService.Format(d.Key, d.Key, d.Args));

            // The hint is only worth showing while something is still to do. A
            // browser that already reported "loaded" does not need to be told to
            // open its settings page, and repeating the instruction after the
            // manual step is exactly what makes it look like it did not work.
            bool chromiumMissing = res.Details.Any(d => d.Key == "install_item_browser_missing");
            bool chromiumSeen = res.Details.Any(d =>
                d.Key is "install_item_browser_loaded" or "install_item_browser_missing");

            string message = head + Environment.NewLine + bullet + string.Join(bullet, lines);

            if (chromiumMissing || !chromiumSeen)
            {
                message += Environment.NewLine + Environment.NewLine
                    + _locService.Get("install_hint_chromium",
                        "In Chrome / Edge / Brave: open chrome://extensions, turn on Developer mode, and choose 'Load unpacked' then select the Chromium folder above.");
            }

            return message;
        }

        /// <summary>
        /// Uninstall, behind a confirmation.
        ///
        /// This is destructive and partly outside the app's own folders - it
        /// edits browser launch commands and Firefox profiles - so the user is
        /// asked first and told exactly what will change.
        /// </summary>
        private async void BtnUninstallExtensions_Click(object sender, RoutedEventArgs e)
        {
            var confirmText = _locService.Get("uninstall_confirm_body",
                "Extension files, native messaging entries, the Firefox profile XPI and browser launch commands will be removed.");
            var answer = MessageBox.Show(
                confirmText + "\n\n" + _locService.Get("uninstall_confirm_question", "Continue?"),
                _locService.Get("app_title", "MPV Launcher"),
                MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) return;

            BtnUninstallExtensions.IsEnabled = false;
            TxtExtensionStatus.Text = _locService.Get("uninstall_running", "Removing browser extension...");
            TxtStatus.Text = TxtExtensionStatus.Text;

            try
            {
                var res = await Task.Run(() => _browserService.UninstallAll());

                string message = res.Success
                    ? BuildUninstallMessage(res)
                    : _locService.Get("uninstall_error", "Browser extension uninstall failed:") + " " + res.Error;

                TxtExtensionStatus.Text = message;
                TxtStatus.Text = res.Success
                    ? _locService.Get("uninstall_success", "Browser extension removed.")
                    : message;

                MessageBox.Show(message, _locService.Get("app_title", "MPV Launcher"), MessageBoxButton.OK,
                    res.Success ? MessageBoxImage.Information : MessageBoxImage.Error);
            }
            catch (Exception ex)
            {
                string msg = _locService.Get("uninstall_error", "Browser extension uninstall failed:") + " " + ex.Message;
                TxtExtensionStatus.Text = msg;
                TxtStatus.Text = msg;
                MessageBox.Show(msg, _locService.Get("app_title", "MPV Launcher"), MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                BtnUninstallExtensions.IsEnabled = true;
            }
        }


        /// <summary>
        /// Turns the service's structured result into a localized summary.
        ///
        /// The service deliberately returns localization keys rather than
        /// sentences, so all user-facing wording lives here and is translated
        /// with everything else. "Nothing removed" is a distinct outcome from
        /// "removed nothing successfully" and gets its own message, because it
        /// usually means the button was pressed before anything was installed.
        /// </summary>
        private string BuildUninstallMessage(UninstallResult res)
        {
            if (!res.AnyRemoved)
                return _locService.Get("uninstall_nothing", "Nothing to remove. The extension was not installed.");

            var bullet = Environment.NewLine + "• ";
            var lines = res.Items
                .Select(i => _locService.Get(i.Key, i.Key) + " (" + i.Count + ")");

            return _locService.Get("uninstall_removed", "Removed:") + bullet + string.Join(bullet, lines)
                   + Environment.NewLine + Environment.NewLine
                   + _locService.Get("uninstall_note",
                       "Note: if it still shows up in your browser, remove it from chrome://extensions or about:debugging.");
        }

        /// <summary>
        /// Opens the Chromium extension folder in Explorer, and nothing else.
        ///
        /// This is the "where are my files" button - it does not install or
        /// repair anything, which is what the separate install button is for. The
        /// folder is created if absent so the button never appears to do nothing
        /// on a fresh install.
        /// </summary>
        /// <summary>
        /// Opens a folder in Explorer, and makes the path usable immediately.
        ///
        /// There is no way to show a browsable folder without Explorer, so no
        /// launch method avoids whatever a shell extension does to it. Measured
        /// here: every variant returns in well under a tenth of a second, while
        /// the window itself can take thirty seconds or more when a third-party
        /// extension blocks the shell.
        ///
        /// So the path goes to the clipboard and on screen before Explorer is
        /// even asked. If the window never appears - or the user would rather
        /// paste it into a window they opened themselves - the button has still
        /// done something useful. Everything before the launch is on the UI
        /// thread and is instant.
        /// </summary>
        private async Task OpenFolderInExplorer(string path, string labelKey, string labelFallback)
        {
            TrySetClipboard(path);
            TxtStatus.Text = _locService.Get(labelKey, labelFallback) + "  " + path;

            try
            {
                await Task.Run(() =>
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"\"{path}\"",
                        UseShellExecute = true
                    });
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    _locService.Get("open_folder_failed", "Could not open folder:") + " " + ex.Message,
                    "MPV Launcher", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        /// <summary>
        /// Puts text on the clipboard, giving up quietly if another process is
        /// holding it. A clipboard that will not open must not stop the folder
        /// from opening, so nothing here is reported.
        /// </summary>
        private void TrySetClipboard(string text)
        {
            try
            {
                Clipboard.SetText(text);
            }
            catch { }
        }

        private async void BtnOpenExtensionFolder_Click(object sender, RoutedEventArgs e)
        {
            string extDir = AppPaths.ChromiumExtensionDir;
            try { Directory.CreateDirectory(extDir); } catch { /* Explorer will report it */ }
            await OpenFolderInExplorer(extDir, "opening_folder", "Opening folder...");
        }

        private void BtnOpenChromeExtensions_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText("chrome://extensions");
            }
            catch { }

            string msg = _locService.Get("copied_chrome_extensions", "chrome://extensions copied to clipboard! Paste it in your browser address bar.");
            TxtExtensionStatus.Text = msg;
            ShowToast(msg);
        }

        /// <summary>
        /// Shows a transient message at the bottom of the window.
        ///
        /// Built in code rather than declared in XAML because the content is
        /// dynamic. It fades in, holds, fades out and removes itself, and a
        /// toast already on screen is cleared first so two never overlap.
        /// </summary>
        private void ShowToast(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return;

            var border = new Border
            {
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1E222D")),
                BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#6366F1")),
                BorderThickness = new Thickness(1, 1, 1, 1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(16, 10, 16, 10),
                Margin = new Thickness(0, 0, 0, 20),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                Opacity = 0
            };

            var text = new TextBlock
            {
                Text = message,
                Foreground = Brushes.White,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap
            };

            border.Child = text;

            if (border.Parent is Panel parent)
            {
                parent.Children.Add(border);
            }
            else
            {
                // Fallback: add to the main grid
                if (WindowContentClip != null)
                {
                    WindowContentClip.Children.Add(border);
                }
            }

            var fadeIn = new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300));
            var fadeOut = new System.Windows.Media.Animation.DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(300))
            {
                BeginTime = TimeSpan.FromSeconds(3)
            };

            border.BeginAnimation(OpacityProperty, fadeIn);
            border.BeginAnimation(OpacityProperty, fadeOut);

            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(3.5)
            };
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                if (border.Parent is Panel p)
                {
                    p.Children.Remove(border);
                }
            };
            timer.Start();
        }

        /// <summary>Opens the add-on listing page on addons.mozilla.org.</summary>
        private void BtnFirefoxAddon_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "https://addons.mozilla.org/firefox/addon/mpv-launcher/",
                    UseShellExecute = true
                });
            }
            catch { }
        }

        /// <summary>Opens the application data folder in Explorer.</summary>
        private async void BtnShowDataFolder_Click(object sender, RoutedEventArgs e)
        {
            await OpenFolderInExplorer(AppPaths.Root, "opening_folder", "Opening folder...");
        }

        /// <summary>
        /// Looks for a newer build, downloads it if there is one, and says so.
        ///
        /// Nothing is applied here. The download is verified and staged; the swap
        /// is a separate click, because it only takes effect after a restart and
        /// doing it silently would mean the program changes between one launch
        /// and the next with nothing having said so.
        ///
        /// The button carries its own next action in Tag, so a staged update can
        /// turn the same control into the install button rather than adding a
        /// second one that is usually hidden.
        /// </summary>
        private async void BtnCheckUpdate_Click(object sender, RoutedEventArgs e)
        {
            if (BtnCheckUpdate.Tag as string == "install")
            {
                await InstallStagedUpdateAsync();
                return;
            }

            BtnCheckUpdate.IsEnabled = false;
            SetUpdateBusy("update_checking");

            var status = await _updateService.CheckAsync();

            BtnCheckUpdate.IsEnabled = true;
            ShowUpdateStatus(status);
        }

        /// <summary>
        /// Puts the result of a check on screen and offers the swap.
        ///
        /// The service returns a key and its arguments rather than a sentence,
        /// so the detail is translated here with everything else.
        /// </summary>
        private void ShowUpdateStatus(UpdateStatus status)
        {
            _updateStatus = status;
            _updateInstalled = false;
            _updateFailure = null;
            _updateBusyKey = null;
            RenderUpdateCard();
        }

        /// <summary>
        /// Shows progress while a check or an install runs.
        ///
        /// The key is held rather than the sentence for the same reason the
        /// rest of the card is: a language change during a slow check would
        /// otherwise leave "Checking for updates..." behind in the old language
        /// for as long as the request took.
        /// </summary>
        private void SetUpdateBusy(string? key)
        {
            _updateBusyKey = key;
            RenderUpdateCard();
        }

        /// <summary>
        /// Draws the Updates card from the state it is holding.
        ///
        /// Called both when the state changes and when the language changes, so
        /// the two cannot drift apart. Nothing here decides anything; it only
        /// reflects <see cref="_updateStatus"/> in the current language.
        /// </summary>
        private void RenderUpdateCard()
        {
            if (TxtUpdateVersion == null || BtnCheckUpdate == null) return;

            ChkAutoUpdate.IsChecked = _configService.Config.AutoUpdate;

            // Progress wins over everything else while an operation runs,
            // because redrawing on a language change would otherwise wipe it.
            if (_updateBusyKey is { } busy)
            {
                TxtUpdateVersion.Text = busy == "update_installing"
                    ? _locService.Get(busy, "Installing...")
                    : _locService.Get(busy, "Checking for updates...");
                return;
            }

            if (_updateInstalled)
            {
                TxtUpdateVersion.Text = _locService.Get("update_installed",
                    "Updated. Restart MPVLauncher to use the new version.");
                ResetUpdateButton();
                return;
            }

            // A failed install outranks whatever the last check said, and it is
            // held as a key so this line is redrawn on a language change rather
            // than staying in the language it happened to fail in.
            if (_updateFailure is { } failure)
            {
                TxtUpdateVersion.Text = _locService.Format("update_install_failed",
                    "Could not install the update: {0}",
                    _locService.Format(failure.Key, failure.Key, failure.Args));
                ResetUpdateButton();
                return;
            }

            var status = _updateStatus
                ?? new UpdateStatus(UpdateStage.UpToDate, UpdateService.CurrentVersion, null);

            switch (status.Stage)
            {
                case UpdateStage.Ready:
                    TxtUpdateVersion.Text = _locService.Format(
                        "update_ready", "Version {0} is ready to install.", status.Version);
                    BtnCheckUpdate.Content = _locService.Get("update_install_now", "Restart & install");
                    BtnCheckUpdate.Tag = "install";
                    break;

                case UpdateStage.Available:
                    TxtUpdateVersion.Text = FormatUpdateDetail(status, "update_unavailable");
                    ResetUpdateButton();
                    break;

                default:
                    TxtUpdateVersion.Text = status.DetailKey is { Length: > 0 }
                        ? FormatUpdateDetail(status, "update_check_failed")
                        : (status.Version.Length > 0
                            ? _locService.Format("update_current_label", "Version {0}", status.Version)
                            : _locService.Format("update_current",
                                "You are on the latest version ({0}).", UpdateService.CurrentVersion));
                    ResetUpdateButton();
                    break;
            }
        }

        /// <summary>
        /// Renders a detail the service described, in the interface language.
        ///
        /// The reason is prefixed to the sentence the stage implies - failed to
        /// check, or failed to download - rather than replacing it, so the
        /// reader is told both what was being attempted and why it did not work.
        /// </summary>
        private string FormatUpdateDetail(UpdateStatus status, string stageKey)
        {
            string reason = status.DetailKey is { Length: > 0 } key
                ? _locService.Format(key, key, status.DetailArgs)
                : "";

            if (status.Stage == UpdateStage.Available && status.Version.Length > 0)
                return _locService.Format(stageKey,
                    "Version {0} could not be downloaded: {1}", status.Version, reason);

            return _locService.Format(stageKey, "Could not check for updates: {0}", reason);
        }

        private void ResetUpdateButton()
        {
            BtnCheckUpdate.Content = _locService.Get("btn_check_update", "Check now");
            BtnCheckUpdate.Tag = null;
        }

        /// <summary>
        /// Records the update preference.
        ///
        /// Only the check is switched. Download and install are unchanged, so
        /// turning this off does not leave a user unable to update on purpose -
        /// the button in the same card still works.
        /// </summary>
        private void ChkAutoUpdate_Changed(object sender, RoutedEventArgs e)
        {
            if (_configService == null || ChkAutoUpdate == null) return;

            bool wanted = ChkAutoUpdate.IsChecked == true;
            if (_configService.Config.AutoUpdate == wanted) return;

            _configService.Config.AutoUpdate = wanted;
            _configService.Save();
        }

        /// <summary>
        /// The update checkbox is wired in the XAML rather than here.
        ///
        /// This method runs again on every language change, and subscribing in
        /// code would add another handler each time, so a checkbox touched after
        /// three language switches would write the setting three times. The
        /// other controls here already work this way - Always on top is read
        /// when the settings are saved and set when they are loaded.
        /// </summary>

        /// <summary>
        /// Looks for a newer build once the window is up.
        ///
        /// Deliberately not awaited by the startup path: it is a network call
        /// that can take seconds or fail entirely, and none of that should
        /// delay the window appearing. It also runs once per launch at most,
        /// and only when the preference is on.
        /// </summary>
        private async Task CheckForUpdatesInBackgroundAsync()
        {
            if (_updateService == null || !_configService.Config.AutoUpdate) return;

            try
            {
                var status = await _updateService.CheckAsync();
                if (status.Stage != UpdateStage.Ready && status.Stage != UpdateStage.Available)
                    return;

                // This runs from the constructor, so the window is not on screen
                // yet. Showing a dialog before then blocks against an owner that
                // has no size to centre on, and the result is a dialog that opens
                // nowhere. Waiting for the window to be rendered first is what
                // makes the offer visible at all.
                await WaitForWindowAsync();

                ShowUpdateStatus(status);

                // The card lives on the Settings tab and the window opens on the
                // Player tab, so writing the result there alone means a user who
                // never visits Settings is never told. This check is only made
                // when the preference is on and nobody has asked about updates,
                // so it is put in front of them once, at launch, where it cannot
                // be missed.
                if (_updateAnnounced) return;
                _updateAnnounced = true;

                TxtStatus.Text = _locService.Format("update_ready_notice",
                    "Version {0} is ready to install.", status.Version);

                await PromptForUpdateAsync(status);
            }
            catch (Exception ex)
            {
                // Silent by design. A user who never asked about updates should
                // not be told that the check they did not request failed.
                System.Diagnostics.Debug.WriteLine($"update check: {ex.Message}");
            }
        }

        /// <summary>
        /// Waits until this window has actually been drawn.
        ///
        /// The startup check is started from the constructor, which runs before the
        /// window is on screen, so anything modal it tries to open would have no
        /// owner to sit against. Waiting for the first render also means the user
        /// is already looking at the app when the notice appears, rather than
        /// finding a dialog waiting for a window that has not arrived.
        ///
        /// Completed on the first ContentRendered, and also once Loaded has been
        /// seen, because a window can report Loaded and then be told not to render
        /// (an off-screen start, a hidden start) - waiting on both means this
        /// cannot wait forever, and a bounded wait means it cannot hang the app if
        /// neither ever comes.
        /// </summary>
        private Task WaitForWindowAsync()
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            void Done()
            {
                if (!tcs.Task.IsCompleted) tcs.TrySetResult(true);
            }

            ContentRendered += (_, _) => Done();
            Loaded += (_, _) => Done();

            // A window that is never rendered must not stall the check: the
            // notice is worth showing whenever it can be, but never at the cost
            // of the app appearing to hang.
            _ = Task.Delay(TimeSpan.FromSeconds(10)).ContinueWith(_ => Done());

            return tcs.Task;
        }

        /// <summary>
        /// Offers the downloaded update at launch, and installs it if accepted.
        ///
        /// Only a staged update is offered. A version that exists but failed to
        /// download has nothing to install, and telling the user to press a
        /// button that then reports the same failure again would be worse than
        /// saying nothing - the card on the Settings tab carries that detail
        /// already.
        /// </summary>
        private async Task PromptForUpdateAsync(UpdateStatus status)
        {
            if (status.Stage != UpdateStage.Ready) return;

            var dlg = new UpdateDialog();

            // Assigning the text can still fail if the XAML did not load, and the
            // caller swallows its exceptions, so this says so in the status line
            // rather than leaving a notice that silently never appeared.
            try
            {
                dlg.SetText(
                    _locService.Get("update_dialog_heading", "Update ready"),
                    _locService.Format("update_dialog_body",
                        "Version {0} has been downloaded and verified. Restart MPVLauncher to finish installing it.",
                        status.Version),
                    _locService.Get("update_install_now", "Restart & install"),
                    _locService.Get("update_dialog_later", "Later"));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"update dialog: {ex}");
                TxtStatus.Text = _locService.Format("update_ready_notice",
                    "Version {0} is ready to install.", status.Version);
                return;
            }

            try { dlg.Owner = this; } catch { }

            bool install;
            try
            {
                install = dlg.ShowDialog() == true && dlg.InstallNow;
            }
            catch (Exception ex)
            {
                // Reported rather than swallowed: a notice that silently fails is
                // exactly what went wrong before, and it is invisible from here.
                System.Diagnostics.Debug.WriteLine($"update dialog: {ex.Message}");
                TxtStatus.Text = _locService.Format("update_ready_notice",
                    "Version {0} is ready to install.", status.Version);
                return;
            }

            if (install)
                await InstallStagedUpdateAsync();
        }

        /// <summary>
        /// Installs the staged update and, if asked, restarts into it.
        ///
        /// The restart is the point of the button: the new executable cannot be
        /// running from inside the process that is about to be replaced, so the
        /// swap is only ever half an update until the app has been relaunched.
        /// Leaving that as a manual step is what made the feature feel unfinished.
        /// </summary>
        private async Task InstallStagedUpdateAsync(bool restart = true)
        {
            BtnCheckUpdate.IsEnabled = false;
            SetUpdateBusy("update_installing");

            var (key, args) = await _updateService.ApplyAsync();
            BtnCheckUpdate.IsEnabled = true;
            _updateBusyKey = null;

            if (key is null)
            {
                _updateInstalled = true;
                _updateFailure = null;
                RenderUpdateCard();

                if (restart && RelaunchAfterUpdate())
                    return;
            }
            else
            {
                _updateInstalled = false;
                _updateFailure = new UpdateFailure(key, args);
                RenderUpdateCard();
            }
        }

        /// <summary>
        /// Starts the freshly installed executable and closes this one.
        ///
        /// Returns false when the new copy could not be started, so the caller
        /// leaves the "restart to finish" message on screen rather than shutting
        /// down and leaving the user with nothing running.
        ///
        /// The new process is started before this one closes, because once the
        /// swap has happened the file at CurrentExePath is the new build and that
        /// is the only path worth launching. Startup is not suppressed in the new
        /// copy: it opens on the Player tab like any launch, and the update
        /// dialog will not reappear because the version it offered is now current.
        /// </summary>
        private bool RelaunchAfterUpdate()
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = System.Environment.ProcessPath
                               ?? System.AppContext.BaseDirectory + "MPVLauncher.exe",
                    UseShellExecute = false,
                };

                System.Diagnostics.Process.Start(psi);

                // A short delay so the replacement is running before the old image
                // is released, which also keeps the purge helper's view of the
                // running process sensible.
                Dispatcher.BeginInvoke(new Action(() => Close()),
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);

                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"relaunch after update: {ex.Message}");
                TxtStatus.Text = _locService.Get("update_installed",
                    "Updated. Restart MPVLauncher to use the new version.");
                return false;
            }
        }

        private void BtnBrowseMpv_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "mpv.exe|mpv.exe|Executables (*.exe)|*.exe|All files (*.*)|*.*",
                Title = "Select mpv.exe"
            };
            if (dlg.ShowDialog() == true)
            {
                TxtMpvPath.Text = dlg.FileName;
            }
        }

        private void BtnSaveMpvSettings_Click(object sender, RoutedEventArgs e)
        {
            _configService.Config.MpvPath = TxtMpvPath.Text.Trim();
            _configService.Config.AlwaysOnTop = ChkAlwaysOnTop.IsChecked ?? true;
            _configService.Config.ForceWindow = (CmbForceWindow.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "immediate";
            _configService.Config.Geometry = TxtMpvGeometry.Text.Trim();
            _configService.Config.MpvProfile = TxtMpvProfile.Text.Trim();
            _configService.Config.ExtraArgs = TxtMpvExtraArgs.Text.Trim();
            _configService.Save();

            TxtStatus.Text = _locService.Get("settings_saved", "Settings saved successfully.");
        }
        #endregion
    }
}
