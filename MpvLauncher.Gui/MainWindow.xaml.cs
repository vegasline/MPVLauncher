using System;
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
    /// <summary>
    /// The application window: one place that owns every service instance and
    /// wires the UI to it.
    ///
    /// Services are constructed here rather than in App so their creation order
    /// is visible: config first, because the theme and language that follow are
    /// chosen by it, and the browser integration last, because it needs the
    /// extension files to already exist.
    ///
    /// The window also acts as the single-instance gate. A second launch is
    /// detected and forwarded here instead of opening a rival window.
    /// </summary>
    public partial class MainWindow : Window
    {
        // One instance of each service for the lifetime of the window. They are
        // stateless apart from ConfigService, which caches the live AppConfig.
        private readonly ConfigService _configService;
        private readonly LocalizationService _locService;
        private readonly ThemeService _themeService;
        private readonly ProcessService _procService;
        private readonly DependencyService _depService;
        private readonly BrowserIntegrationService _browserService;
        private readonly DownloadInstallService _downloadService;
        private readonly ModernZAnime4kService _modernZService;

        /// <summary>Corner radius used by the borderless window chrome.</summary>
        private const double WindowCornerRadius = 8;

        /// <summary>
        /// Guards the install/uninstall buttons. A second click while a download
        /// or registry write is running would interleave two installs.
        /// </summary>
        private bool _isInstalling;

        public MainWindow()
        {
            InitializeComponent();

            AppPaths.EnsureLayout();
            _configService = new ConfigService();

            // On a first run the embedded templates are authoritative and are
            // written even if a file already exists; afterwards the user's copies
            // win so their edits survive.
            AppPaths.SeedTemplates(overwriteEmbedded: _configService.Config.FirstRun);

            _locService = new LocalizationService(AppPaths.LanguagesDir, _configService.Config.Language);
            _themeService = new ThemeService(AppPaths.ThemesDir, _configService.Config.Theme);
            _procService = new ProcessService(_configService);
            _depService = new DependencyService(_procService);
            _browserService = new BrowserIntegrationService();
            _downloadService = new DownloadInstallService(_configService);
            _modernZService = new ModernZAnime4kService();

            // Theme and language changes come from the settings pages, so the
            // window subscribes rather than being told.
            _locService.LanguageChanged += UpdateLocalization;
            _themeService.ThemeChanged += UpdateTheme;

            try
            {
                Icon = IconGenerator.CreateAppIcon(64);
                IconGenerator.EnsureIcons();
            }
            catch { }

            Loaded += MainWindow_Loaded;
        }

        /// <summary>
        /// Fills every control from the saved config, then shows what was found
        /// on disk. Runs on Loaded rather than in the constructor because the
        /// named controls do not exist until InitializeComponent has run and the
        /// window has a visual tree.
        /// </summary>
        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            UpdateLocalization();
            UpdateTheme();
            LoadHistory();
            PopulateLanguages();
            PopulateThemes();

            // ---- Playback settings ----
            TxtMpvPath.Text = _configService.Config.MpvPath;
            ChkAlwaysOnTop.IsChecked = _configService.Config.AlwaysOnTop;

            // The combo stores its mode in Tag, so the saved string is matched
            // back to an item rather than assigned directly.
            string fw = _configService.Config.ForceWindow ?? "immediate";
            foreach (ComboBoxItem item in CmbForceWindow.Items)
            {
                if (string.Equals(item.Tag?.ToString(), fw, StringComparison.OrdinalIgnoreCase))
                {
                    CmbForceWindow.SelectedItem = item;
                    break;
                }
            }

            TxtMpvGeometry.Text = _configService.Config.Geometry;
            TxtMpvProfile.Text = _configService.Config.MpvProfile;
            TxtMpvExtraArgs.Text = _configService.Config.ExtraArgs;

            // ---- Appearance overrides ----
            // The opacity sliders show percentages while the config stores 0..1.
            SliderBlur.Value = _configService.Config.CustomBlur;
            SliderOpacity.Value = _configService.Config.CustomCardOpacity * 100;
            SliderSidebarOpacity.Value = _configService.Config.CustomSidebarOpacity * 100;
            SliderInputOpacity.Value = _configService.Config.CustomInputOpacity * 100;
            SliderButtonOpacity.Value = _configService.Config.CustomButtonOpacity * 100;
            TxtColorBg.Text = _configService.Config.CustomBgColor;
            TxtColorSidebar.Text = _configService.Config.CustomSidebarColor;
            TxtColorCard.Text = _configService.Config.CustomCardColor;
            TxtColorAccent.Text = _configService.Config.CustomAccentColor;
            TxtColorBorder.Text = _configService.Config.CustomBorderColor;
            TxtColorTextPrimary.Text = _configService.Config.CustomTextPrimary;
            TxtColorTextSecondary.Text = _configService.Config.CustomTextSecondary;
            TxtColorLogo.Text = _configService.Config.CustomLogoColor;
            TxtCustomBgUrl.Text = _configService.Config.CustomBackground;

            TxtInstallDir.Text = $"Tools folder: {AppPaths.BinDir}";
            TxtDataFolderPath.Text = AppPaths.Root;
            TxtStatus.Text = _locService.Get("status_ready", "Ready");

            _ = RefreshDependenciesAsync();
            _ = InitializeBrowserIntegrationAsync();
        }

        #region Window
        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
                DragMove();
        }

        private void BtnMinimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

        /**
         * Keeps the rounded corners in step with the window size.
         *
         * The window has no OS frame (WindowStyle=None), so the rounding is a
         * geometry clip applied to the content element. Re-clipping on resize is
         * what keeps the corners from becoming square as the window grows.
         */
        private void WindowContentClip_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            double r = _configService != null ? _configService.Config.WindowCornerRadius : WindowCornerRadius;
            WindowContentClip.Clip = new RectangleGeometry(
                new Rect(0, 0, e.NewSize.Width, e.NewSize.Height),
                r,
                r);
        }
        #endregion

        #region Navigation
        private Button? _activeNavButton;

        /**
         * Shows one panel and highlights its nav button.
         *
         * All five panels are declared in XAML and collapsed rather than created
         * on demand, so switching pages is a visibility toggle with no layout
         * cost. Every button is reset first because there is no "previous" state
         * to diff against - with only five entries the exhaustive reset is
         * clearer than tracking one.
         */
        private void SetActiveTab(StackPanel activePanel, Button activeButton)
        {
            _activeNavButton = activeButton;

            PanelPlayer.Visibility = Visibility.Collapsed;
            PanelTools.Visibility = Visibility.Collapsed;
            PanelSettings.Visibility = Visibility.Collapsed;
            PanelThemes.Visibility = Visibility.Collapsed;
            PanelGuide.Visibility = Visibility.Collapsed;

            BtnNavPlayer.Background = Brushes.Transparent;
            BtnNavTools.Background = Brushes.Transparent;
            BtnNavSettings.Background = Brushes.Transparent;
            BtnNavThemes.Background = Brushes.Transparent;
            BtnNavGuide.Background = Brushes.Transparent;

            var muted = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#9AA3B2"));
            BtnNavPlayer.Foreground = muted;
            BtnNavTools.Foreground = muted;
            BtnNavSettings.Foreground = muted;
            BtnNavThemes.Foreground = muted;
            BtnNavGuide.Foreground = muted;

            activePanel.Visibility = Visibility.Visible;
            string accent = !string.IsNullOrWhiteSpace(_configService?.Config.CustomAccentColor)
                ? _configService.Config.CustomAccentColor
                : (_themeService?.CurrentTheme?.Colors?.Accent ?? "#6366F1");
            activeButton.Background = ToBrush(accent, "#6366F1");
            activeButton.Foreground = Brushes.White;
        }

        private void BtnNavPlayer_Click(object sender, RoutedEventArgs e) => SetActiveTab(PanelPlayer, BtnNavPlayer);
        private void BtnNavTools_Click(object sender, RoutedEventArgs e) => SetActiveTab(PanelTools, BtnNavTools);
        private void BtnNavSettings_Click(object sender, RoutedEventArgs e) => SetActiveTab(PanelSettings, BtnNavSettings);
        private void BtnNavThemes_Click(object sender, RoutedEventArgs e) => SetActiveTab(PanelThemes, BtnNavThemes);
        private void BtnNavGuide_Click(object sender, RoutedEventArgs e) => SetActiveTab(PanelGuide, BtnNavGuide);
        #endregion

        #region Player & history
        private void BtnPlayUrl_Click(object sender, RoutedEventArgs e)
        {
            string url = TxtUrlInput.Text.Trim();
            if (!string.IsNullOrEmpty(url))
                Play(url);
        }

        private void TxtUrlInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
                BtnPlayUrl_Click(sender, e);
        }

        private void BtnBrowseFile_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "Video files|*.mp4;*.mkv;*.webm;*.avi;*.mov;*.flv;*.m3u8;*.ts|All files|*.*"
            };
            if (dlg.ShowDialog() == true)
                Play(dlg.FileName);
        }

        private void Window_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (files.Length > 0)
                    Play(files[0]);
            }
        }

        /// <summary>
        /// The single place playback is started from, whatever the source: the
        /// URL box, a dropped file, a history row or a pasted path. Centralising
        /// it means the status line and the history refresh happen once.
        /// </summary>
        private void Play(string target)
        {
            TxtStatus.Text = _locService.Get("status_playing", "Launching MPV...");
            var res = _procService.PlayMedia(target);

            // A loader failure is the one case where the raw reason is not
            // enough on its own. The user has a dialog on screen with a
            // procedure name in it and no way to know it is not an mpv setting
            // to change, so the message names the actual remedy.
            TxtStatus.Text = res.Probe.Health == MpvHealth.NotRunnable
                ? (res.Probe.IsLoaderProblem
                    ? _locService.Format("play_mpv_loader", "", res.Probe.Describe()) +
                      " " + _locService.Get("play_mpv_loader_fix",
                          "This is almost always an out-of-date graphics driver or Vulkan runtime " +
                          "- mpv needs Vulkan 1.1 or newer. Update the driver. No option in " +
                          "MPVLauncher can work around this.")
                    : _locService.Format("play_mpv_broken", "mpv cannot start ({0}).", res.Probe.Describe()))
                : res.Message;

            LoadHistory();
        }

        /**
         * Rebinds the history list.
         *
         * ItemsSource is cleared first because assigning the same list instance
         * again would not raise a change notification and the list would look
         * unchanged after a reload.
         */
        private void LoadHistory()
        {
            LstHistory.ItemsSource = null;
            LstHistory.ItemsSource = _configService.Config.PlaybackHistory;
        }

        private void LstHistory_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (LstHistory.SelectedItem is HistoryItem item)
                Play(item.Url);
        }

        private void BtnClearHistory_Click(object sender, RoutedEventArgs e)
        {
            _configService.ClearHistory();
            LoadHistory();
            TxtStatus.Text = _locService.Get("history_cleared", "Playback history cleared.");
        }
        #endregion

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

                TxtMpvStatus.Text = status.Mpv;
                TxtYtdlStatus.Text = status.Ytdl;
                TxtFfmpegStatus.Text = status.Ffmpeg;
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
                    // The recorded host path is refreshed on every launch, since
                    // the app may have moved. A full install is only needed on a
                    // first run.
                    _browserService.RepairNativeHostPath();
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

        private async void BtnUpdateYtdl_Click(object sender, RoutedEventArgs e)
        {
            TxtStatus.Text = "Updating yt-dlp...";
            var res = await _depService.UpdateYtdlAsync();
            TxtStatus.Text = res.Success ? "yt-dlp updated." : res.Output;
            await RefreshDependenciesAsync();
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
                    ? _locService.Get("ext_install_success", "Browser extensions installed successfully. Restart your browsers.")
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

            return head + Environment.NewLine + bullet + string.Join(bullet, lines)
                   + Environment.NewLine + Environment.NewLine
                   + _locService.Get("install_hint_chromium",
                       "In Chrome / Edge / Brave: open chrome://extensions, turn on Developer mode, and choose 'Load unpacked' then select the Chromium folder above.")
                   + Environment.NewLine
                   + _locService.Get("install_hint_firefox",
                       "In Firefox: open about:debugging, choose 'This Firefox', and load the temporary add-on from the XPI path above.");
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
        private void BtnOpenExtensionFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                AppPaths.EnsureLayout();
                string extDir = AppPaths.ChromiumExtensionDir;
                Directory.CreateDirectory(extDir);

                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{extDir}\"",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not open folder: " + ex.Message, "MPV Launcher", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
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
        private void BtnShowDataFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                AppPaths.EnsureLayout();
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{AppPaths.Root}\"",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "MPV Launcher", MessageBoxButton.OK, MessageBoxImage.Error);
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

        #region Themes
        private void PopulateThemes()
        {
            PanelThemesWrap.Children.Clear();
            var themes = _themeService.GetAvailableThemes();

            foreach (var t in themes)
            {
                var btn = new Button
                {
                    Style = (Style)FindResource("SecondaryButton"),
                    Margin = new Thickness(0, 0, 10, 10),
                    Padding = new Thickness(14, 10, 14, 10),
                    Tag = t.Id
                };

                var sp = new StackPanel();
                sp.Children.Add(new TextBlock { Text = t.Name, FontWeight = FontWeights.SemiBold, FontSize = 13 });
                if (!string.IsNullOrEmpty(t.Author))
                    sp.Children.Add(new TextBlock
                    {
                        Text = t.Author,
                        FontSize = 11,
                        Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#64748B")),
                        Margin = new Thickness(0, 2, 0, 0)
                    });

                btn.Content = sp;
                btn.Click += (_, _) =>
                {
                    _themeService.LoadTheme(t.Id);
                    _configService.Config.Theme = t.Id;
                    _configService.Save();
                };

                PanelThemesWrap.Children.Add(btn);
            }
        }

        /// <summary>
        /// Applies the selected theme and the user's appearance overrides to
        /// every brush the window uses.
        ///
        /// Precedence throughout is: an explicit override in config, then the
        /// theme's value, then a built-in default. ApplyCardStyle and ToBrush
        /// below are what encode that, so this method reads as a list of
        /// decisions rather than a wall of colour parsing.
        /// </summary>
        private void UpdateTheme()
        {
            var tm = _themeService.CurrentTheme;
            var cfg = _configService.Config;

            string bgUrl = !string.IsNullOrEmpty(cfg.CustomBackground)
                ? cfg.CustomBackground
                : tm.Background.Image;

            string bgColor = !string.IsNullOrEmpty(cfg.CustomBgColor) ? cfg.CustomBgColor : tm.Background.Color;
            WindowFrame.Background = ToBrush(bgColor, "#12141A");
            BgOverlayBorder.Background = ToBrush(tm.Background.OverlayColor, "#B20D0F18");

            WindowFrame.CornerRadius = new CornerRadius(cfg.WindowCornerRadius);
            BgOverlayBorder.CornerRadius = new CornerRadius(cfg.WindowCornerRadius);

            if (WindowFrame.Effect is System.Windows.Media.Effects.DropShadowEffect dse)
            {
                dse.BlurRadius = cfg.CustomShadowBlur;
                dse.Opacity = cfg.CustomShadowOpacity;
            }

            if (WindowContentClip != null)
            {
                double w = WindowContentClip.ActualWidth > 0 ? WindowContentClip.ActualWidth : Width;
                double h = WindowContentClip.ActualHeight > 0 ? WindowContentClip.ActualHeight : Height;
                WindowContentClip.Clip = new RectangleGeometry(new Rect(0, 0, w, h), cfg.WindowCornerRadius, cfg.WindowCornerRadius);
            }

            SetBackgroundImage(bgUrl);

            BgBlurEffect.Radius = cfg.CustomBlur >= 0 ? cfg.CustomBlur : tm.Background.BlurRadius;

            try
            {
                byte cardAlpha = (byte)Math.Clamp(cfg.CustomCardOpacity * 255, 10, 255);
                var cardColor = ToColor(!string.IsNullOrEmpty(cfg.CustomCardColor) ? cfg.CustomCardColor : tm.Colors.CardBg, "#1E222D");
                cardColor.A = cardAlpha;
                var cardBrush = new SolidColorBrush(cardColor);

                var cardRadius = new CornerRadius(cfg.CardCornerRadius);

                ApplyCardStyle(CardUrl, cardBrush, cardRadius);
                ApplyCardStyle(CardHistory, cardBrush, cardRadius);
                ApplyCardStyle(CardStatus, cardBrush, cardRadius);
                ApplyCardStyle(CardDownloads, cardBrush, cardRadius);
                ApplyCardStyle(CardBrowser, cardBrush, cardRadius);
                ApplyCardStyle(CardThemes, cardBrush, cardRadius);
                ApplyCardStyle(CardThemeAnime4k, cardBrush, cardRadius);
                ApplyCardStyle(CardThemeCustomizer, cardBrush, cardRadius);
                ApplyCardStyle(CardMpvConfig, cardBrush, cardRadius);
                ApplyCardStyle(CardLang, cardBrush, cardRadius);
                ApplyCardStyle(CardDataFolder, cardBrush, cardRadius);

                if (SidebarBorder != null)
                {
                    byte sideAlpha = (byte)Math.Clamp(cfg.CustomSidebarOpacity * 255, 10, 255);
                    var sideColor = ToColor(!string.IsNullOrEmpty(cfg.CustomSidebarColor) ? cfg.CustomSidebarColor : tm.Background.Color, "#12141A");
                    sideColor.A = sideAlpha;
                    SidebarBorder.Background = new SolidColorBrush(sideColor);
                }

                string accent = !string.IsNullOrWhiteSpace(cfg.CustomAccentColor) ? cfg.CustomAccentColor : tm.Colors.Accent;
                var accentBrush = ToBrush(accent, "#6366F1");

                if (BorderBrandIcon != null)
                {
                    // Logo tint, in order of precedence: the user's explicit
                    // override, then the theme's own logo colour, then accent.
                    string logoColor = !string.IsNullOrWhiteSpace(cfg.CustomLogoColor)
                        ? cfg.CustomLogoColor
                        : (!string.IsNullOrWhiteSpace(_themeService?.CurrentTheme?.Colors?.Logo)
                            ? _themeService.CurrentTheme.Colors.Logo
                            : accent);
                    BorderBrandIcon.Background = ToBrush(logoColor, accent);
                }

                if (_activeNavButton != null)
                {
                    _activeNavButton.Background = accentBrush;
                    _activeNavButton.Foreground = Brushes.White;
                }
                else if (BtnNavPlayer != null && BtnNavPlayer.Background != Brushes.Transparent)
                {
                    BtnNavPlayer.Background = accentBrush;
                    BtnNavPlayer.Foreground = Brushes.White;
                }

                if (BtnPlayUrl != null) BtnPlayUrl.Background = accentBrush;
                if (BtnInstallExtensions != null) BtnInstallExtensions.Background = accentBrush;
                if (BtnInstallMpv != null) BtnInstallMpv.Background = accentBrush;
                if (BtnInstallYtdlp != null) BtnInstallYtdlp.Background = accentBrush;
                if (BtnInstallFfmpeg != null) BtnInstallFfmpeg.Background = accentBrush;
                if (BtnSaveMpvSettings != null) BtnSaveMpvSettings.Background = accentBrush;
                if (BtnApplyColors != null) BtnApplyColors.Background = accentBrush;
            }
            catch { }
        }

        /// <summary>Applies the fill and corner radius to one card border.</summary>
        private static void ApplyCardStyle(Border? card, Brush bg, CornerRadius radius)
        {
            if (card != null)
            {
                card.Background = bg;
                card.CornerRadius = radius;
            }
        }

        private static SolidColorBrush ToBrush(string value, string fallback)
            => new(ToColor(value, fallback));

        /// <summary>
        /// Parses a colour, returning the fallback instead of throwing.
        ///
        /// Colours come from three places - the config file, theme JSON, and a
        /// text box the user types into - so any of them can hold something
        /// unparseable. A wrong colour must never take the window down, which is
        /// why there is no exception path out of here.
        ///
        /// "rgba(r,g,b,a)" is unpacked by hand: theme files use CSS notation
        /// that WPF's own converter does not accept.
        /// </summary>
        private static Color ToColor(string value, string fallback)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    string trimmed = value.Trim();
                    if (trimmed.StartsWith("rgba(", StringComparison.OrdinalIgnoreCase) && trimmed.EndsWith(")", StringComparison.Ordinal))
                    {
                        string[] parts = trimmed[5..^1].Split(',');
                        if (parts.Length == 4)
                        {
                            byte r = byte.Parse(parts[0].Trim(), CultureInfo.InvariantCulture);
                            byte g = byte.Parse(parts[1].Trim(), CultureInfo.InvariantCulture);
                            byte b = byte.Parse(parts[2].Trim(), CultureInfo.InvariantCulture);
                            double a = double.Parse(parts[3].Trim(), CultureInfo.InvariantCulture);
                            return Color.FromArgb((byte)Math.Clamp(a * 255, 0, 255), r, g, b);
                        }
                    }

                    return (Color)ColorConverter.ConvertFromString(trimmed);
                }
            }
            catch { }

            return (Color)ColorConverter.ConvertFromString(fallback);
        }

        private void SliderBlur_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (TxtBlurVal != null && _configService != null)
            {
                int val = (int)e.NewValue;
                TxtBlurVal.Text = $"{val}px";
                _configService.Config.CustomBlur = val;
                _configService.Save();
                UpdateTheme();
            }
        }

        private void SliderOpacity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (TxtOpacityVal != null && _configService != null)
            {
                int val = (int)e.NewValue;
                TxtOpacityVal.Text = $"{val}%";
                _configService.Config.CustomCardOpacity = val / 100.0;
                _configService.Save();
                UpdateTheme();
            }
        }

        private void SliderSidebarOpacity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (TxtSidebarOpacityVal != null && _configService != null)
            {
                int val = (int)e.NewValue;
                TxtSidebarOpacityVal.Text = $"{val}%";
                _configService.Config.CustomSidebarOpacity = val / 100.0;
                _configService.Save();
                UpdateTheme();
            }
        }

        private void SliderInputOpacity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (TxtInputOpacityVal != null && _configService != null)
            {
                int val = (int)e.NewValue;
                TxtInputOpacityVal.Text = $"{val}%";
                _configService.Config.CustomInputOpacity = val / 100.0;
                _configService.Save();
            }
        }

        private void SliderButtonOpacity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (TxtButtonOpacityVal != null && _configService != null)
            {
                int val = (int)e.NewValue;
                TxtButtonOpacityVal.Text = $"{val}%";
                _configService.Config.CustomButtonOpacity = val / 100.0;
                _configService.Save();
            }
        }

        private void BtnApplyColors_Click(object sender, RoutedEventArgs e)
        {
            _configService.Config.CustomBgColor = TxtColorBg.Text.Trim();
            _configService.Config.CustomSidebarColor = TxtColorSidebar.Text.Trim();
            _configService.Config.CustomCardColor = TxtColorCard.Text.Trim();
            _configService.Config.CustomAccentColor = TxtColorAccent.Text.Trim();
            _configService.Config.CustomBorderColor = TxtColorBorder.Text.Trim();
            _configService.Config.CustomTextPrimary = TxtColorTextPrimary.Text.Trim();
            _configService.Config.CustomTextSecondary = TxtColorTextSecondary.Text.Trim();
            _configService.Config.CustomLogoColor = TxtColorLogo.Text.Trim();
            _configService.Save();
            UpdateTheme();
            TxtStatus.Text = _locService.Get("colors_applied", "Custom colors applied.");
        }

        private void BtnPickColor_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string targetBoxName)
            {
                TextBox? targetBox = targetBoxName switch
                {
                    "TxtColorBg" => TxtColorBg,
                    "TxtColorSidebar" => TxtColorSidebar,
                    "TxtColorCard" => TxtColorCard,
                    "TxtColorAccent" => TxtColorAccent,
                    "TxtColorBorder" => TxtColorBorder,
                    "TxtColorTextPrimary" => TxtColorTextPrimary,
                    "TxtColorTextSecondary" => TxtColorTextSecondary,
                    _ => null
                };

                if (targetBox != null)
                {
                    var dlg = new ColorPickerDialog(targetBox.Text.Trim());
                    try { dlg.Owner = this; } catch { }
                    if (dlg.ShowDialog() == true)
                    {
                        targetBox.Text = dlg.ResultColor;
                        BtnApplyColors_Click(sender, e);
                    }
                }
            }
        }

        private void BtnBrowseBgImage_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "Image Files|*.png;*.jpg;*.jpeg;*.webp;*.bmp|All Files|*.*",
                Title = "Select Background Image"
            };
            if (dlg.ShowDialog() == true)
            {
                TxtCustomBgUrl.Text = dlg.FileName;
                _configService.Config.CustomBackground = dlg.FileName;
                _configService.Save();
                UpdateTheme();
            }
        }

        private void BtnApplyCustomBg_Click(object sender, RoutedEventArgs e)
        {
            _configService.Config.CustomBackground = TxtCustomBgUrl.Text.Trim();
            _configService.Save();
            UpdateTheme();
            TxtStatus.Text = "Custom background applied.";
        }

        private void BtnResetThemeDefaults_Click(object sender, RoutedEventArgs e)
        {
            _configService.Config.ResetThemeSettings();
            _configService.Save();
            _themeService.LoadTheme("dark");

            SliderBlur.Value = _configService.Config.CustomBlur;
            SliderOpacity.Value = _configService.Config.CustomCardOpacity * 100;
            SliderSidebarOpacity.Value = _configService.Config.CustomSidebarOpacity * 100;
            SliderInputOpacity.Value = _configService.Config.CustomInputOpacity * 100;
            SliderButtonOpacity.Value = _configService.Config.CustomButtonOpacity * 100;
            TxtColorBg.Text = _configService.Config.CustomBgColor;
            TxtColorSidebar.Text = _configService.Config.CustomSidebarColor;
            TxtColorCard.Text = _configService.Config.CustomCardColor;
            TxtColorAccent.Text = _configService.Config.CustomAccentColor;
            TxtColorBorder.Text = _configService.Config.CustomBorderColor;
            TxtColorTextPrimary.Text = _configService.Config.CustomTextPrimary;
            TxtColorTextSecondary.Text = _configService.Config.CustomTextSecondary;
            TxtColorLogo.Text = _configService.Config.CustomLogoColor;
            TxtCustomBgUrl.Text = "";

            UpdateTheme();
            TxtStatus.Text = _locService.Get("theme_reset_success", "Theme and UI settings reset to defaults.");
        }

        private async void BtnImportTheme_Click(object sender, RoutedEventArgs e)
        {
            string url = TxtThemeUrl.Text.Trim();
            if (string.IsNullOrEmpty(url)) return;
            TxtStatus.Text = "Downloading theme...";
            var res = await _themeService.ImportFromUrlAsync(url);
            TxtStatus.Text = res.Message;
            if (res.Success)
            {
                TxtThemeUrl.Text = "";
                PopulateThemes();
            }
        }

        private async void BtnInstallThemeAnime4k_Click(object sender, RoutedEventArgs e)
        {
            if (_isInstalling) return;
            _isInstalling = true;
            BtnInstallThemeAnime4k.IsEnabled = false;
            TxtThemeAnime4kStatus.Text = "Installing ModernZ Theme & Anime4K shaders...";

            var progress = new Progress<DownloadProgress>(p =>
            {
                TxtThemeAnime4kStatus.Text = $"{p.Stage} {(p.Percent.HasValue ? $"({p.Percent:F0}%)" : "")}";
            });

            try
            {
                var res = await _modernZService.InstallAllAsync(progress);
                TxtThemeAnime4kStatus.Text = res.Message;
                TxtStatus.Text = res.Message;
            }
            catch (Exception ex)
            {
                TxtThemeAnime4kStatus.Text = $"Error: {ex.Message}";
            }
            finally
            {
                _isInstalling = false;
                BtnInstallThemeAnime4k.IsEnabled = true;
            }
        }
        #endregion

        #region Language
        private void PopulateLanguages()
        {
            CmbLanguage.Items.Clear();
            var langs = _locService.GetAvailableLanguages();
            foreach (var l in langs)
            {
                var item = new ComboBoxItem { Content = l.Name, Tag = l.Code };
                if (l.Code == _configService.Config.Language)
                    item.IsSelected = true;
                CmbLanguage.Items.Add(item);
            }
        }

        private void CmbLanguage_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CmbLanguage.SelectedItem is ComboBoxItem item && item.Tag is string code)
            {
                _locService.LoadLanguage(code);
                _configService.Config.Language = code;
                _configService.Save();
            }
        }

        /// <summary>
        /// Loads the window background from a path or a URL.
        ///
        /// A local file is read directly. A URL is cached under the app's cache
        /// folder first and only re-fetched when that file is missing, so the
        /// background does not delay startup on every launch and works offline
        /// once seen. The cache name is a hash of the URL rather than the URL
        /// itself, which keeps arbitrary query strings from becoming file names.
        /// </summary>
        private void SetBackgroundImage(string? bgUrl)
        {
            if (string.IsNullOrWhiteSpace(bgUrl))
            {
                BgImageElement.Source = null;
                return;
            }

            try
            {
                // 1. Local file on disk
                if (File.Exists(bgUrl))
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.UriSource = new Uri(Path.GetFullPath(bgUrl), UriKind.Absolute);
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
                    bmp.EndInit();
                    bmp.Freeze();
                    BgImageElement.Source = bmp;
                    return;
                }

                // 2. Remote URL: Check cache in %APPDATA%\MPVLauncher\cache
                if (Uri.TryCreate(bgUrl, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                {
                    string hash = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(bgUrl))).ToLowerInvariant();
                    string ext = Path.GetExtension(uri.AbsolutePath);
                    if (string.IsNullOrWhiteSpace(ext) || ext.Length > 5) ext = ".png";
                    string cachedFile = Path.Combine(AppPaths.CacheDir, $"bg_{hash}{ext}");

                    if (File.Exists(cachedFile))
                    {
                        var bmp = new BitmapImage();
                        bmp.BeginInit();
                        bmp.UriSource = new Uri(cachedFile, UriKind.Absolute);
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
                        bmp.EndInit();
                        bmp.Freeze();
                        BgImageElement.Source = bmp;
                        return;
                    }

                    // Download asynchronously and cache
                    Task.Run(async () =>
                    {
                        try
                        {
                            using var client = new System.Net.Http.HttpClient();
                            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");
                            byte[] data = await client.GetByteArrayAsync(uri);
                            Directory.CreateDirectory(AppPaths.CacheDir);
                            await File.WriteAllBytesAsync(cachedFile, data);

                            Dispatcher.Invoke(() =>
                            {
                                try
                                {
                                    var bmp = new BitmapImage();
                                    bmp.BeginInit();
                                    bmp.UriSource = new Uri(cachedFile, UriKind.Absolute);
                                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                                    bmp.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
                                    bmp.EndInit();
                                    bmp.Freeze();
                                    BgImageElement.Source = bmp;
                                }
                                catch { }
                            });
                        }
                        catch { }
                    });
                    return;
                }

                // 3. Fallback
                var fallbackBmp = new BitmapImage();
                fallbackBmp.BeginInit();
                fallbackBmp.UriSource = new Uri(bgUrl, UriKind.RelativeOrAbsolute);
                fallbackBmp.CacheOption = BitmapCacheOption.OnLoad;
                fallbackBmp.EndInit();
                fallbackBmp.Freeze();
                BgImageElement.Source = fallbackBmp;
            }
            catch
            {
                BgImageElement.Source = null;
            }
        }

        /// <summary>
        /// Re-labels every control in the window.
        ///
        /// Called on startup and whenever the user picks a language. Every
        /// lookup passes an English fallback, so a language pack that is missing
        /// a key still renders readable text rather than the raw key - which is
        /// also why the fallback has to be kept in step with the locale files.
        ///
        /// Only text is touched: nothing here rebuilds a panel, so switching
        /// language cannot change the current page or lose a typed value.
        /// </summary>
        private void UpdateLocalization()
        {
            Title = _locService.Get("app_title", "MPV Launcher");
            TxtTitleBar.Text = _locService.Get("app_title", "MPV Launcher");
            TxtSidebarBrand.Text = _locService.Get("app_title", "MPV Launcher");

            BtnNavPlayer.Content = _locService.Get("tab_player", "Player");
            BtnNavTools.Content = _locService.Get("tab_tools", "Dependencies");
            BtnNavSettings.Content = _locService.Get("tab_settings", "Settings");
            BtnNavThemes.Content = _locService.Get("tab_themes", "Themes");
            BtnNavGuide.Content = _locService.Get("tab_guide", "Guide");

            TxtPlayerTitle.Text = _locService.Get("player_title", "Media & Stream Player");
            TxtPlayerDesc.Text = _locService.Get("player_desc", "Paste a video URL or drag and drop a local video file.");
            TxtUrlInput.ToolTip = _locService.Get("input_url_placeholder", "YouTube, Twitch, or a direct m3u8/mp4 link...");
            BtnPlayUrl.Content = _locService.Get("btn_play", "Play with MPV");
            TxtDropZone.Text = _locService.Get("drop_zone_text", "Drag and drop a video file here");
            BtnBrowseFile.Content = _locService.Get("btn_browse_file", "Select File");
            TxtHistoryTitle.Text = _locService.Get("history_title", "Playback history");
            BtnClearHistory.Content = _locService.Get("btn_clear_history", "Clear history");

            TxtToolsTitle.Text = _locService.Get("tools_title", "Dependencies & Browser");
            TxtSystemStatus.Text = _locService.Get("system_status", "System status");
            BtnUpdateYtdl.Content = _locService.Get("btn_update_ytdl", "Update yt-dlp (-U)");
            BtnRefreshTools.Content = _locService.Get("btn_refresh", "Refresh");
            TxtDownloadsTitle.Text = _locService.Get("downloads_title", "Download sources");
            TxtInstallDir.Text = $"{_locService.Get("tools_folder_label", "Tools folder:")} {AppPaths.BinDir}";
            BtnInstallMpv.Content = _locService.Get("btn_download_install", "Download & Install");
            BtnInstallYtdlp.Content = _locService.Get("btn_download_install", "Download & Install");
            BtnInstallFfmpeg.Content = _locService.Get("btn_download_install", "Download & Install");

            TxtBrowserTitle.Text = _locService.Get("browser_integration_title", "Browser extension integration");
            TxtBrowserDesc.Text = _locService.Get("browser_desc", "Extensions and native messaging are installed automatically when the app starts. Restart your browser after the first run.");
            BtnInstallExtensions.Content = _locService.Get("btn_install_extensions", "Install / repair extensions");
            BtnUninstallExtensions.Content = _locService.Get("btn_uninstall_extensions", "Uninstall extension");
            BtnFirefoxAddon.Content = _locService.Get("btn_firefox_addon", "🦊 Firefox Add-on");
            BtnOpenExtensionFolder.Content = _locService.Get("btn_open_ext_folder", "Open Extension Folder");
            BtnOpenChromeExtensions.Content = _locService.Get("btn_open_browser_ext", "Open chrome://extensions");

            TxtSettingsTitle.Text = _locService.Get("settings_title", "Application & MPV Settings");
            TxtMpvConfigTitle.Text = _locService.Get("mpv_config_title", "MPV Player Configuration");
            TxtMpvPathLabel.Text = _locService.Get("mpv_path_label", "MPV Executable Path:");
            BtnBrowseMpv.Content = _locService.Get("btn_browse_mpv", "Browse");
            ChkAlwaysOnTop.Content = _locService.Get("label_always_on_top", "Always on top (--ontop)");
            TxtForceWindowLabel.Text = _locService.Get("label_force_window", "Force Window (--force-window):");
            if (CmbForceWindow != null)
            {
                foreach (ComboBoxItem item in CmbForceWindow.Items)
                {
                    string tag = item.Tag?.ToString() ?? "";
                    if (tag.Equals("immediate", StringComparison.OrdinalIgnoreCase))
                        item.Content = _locService.Get("fw_immediate", "immediate (Fastest / Open Immediately)");
                    else if (tag.Equals("yes", StringComparison.OrdinalIgnoreCase))
                        item.Content = _locService.Get("fw_yes", "yes (When Media Loaded)");
                    else if (tag.Equals("no", StringComparison.OrdinalIgnoreCase))
                        item.Content = _locService.Get("fw_no", "no (Disabled)");
                }
            }

            TxtMpvGeometryLabel.Text = _locService.Get("label_mpv_geometry", "Window Geometry (--geometry):");
            TxtMpvProfileLabel.Text = _locService.Get("label_mpv_profile", "MPV Profile (--profile):");
            TxtMpvExtraArgsLabel.Text = _locService.Get("label_mpv_extra_args", "Extra MPV Parameters (e.g. --hwdec=auto):");
            BtnSaveMpvSettings.Content = _locService.Get("btn_save_settings", "Save Settings");

            TxtDataFolderTitle.Text = _locService.Get("data_folder_title", "Application data folder");
            BtnShowDataFolder.Content = _locService.Get("btn_show_folder", "Show folder");
            TxtLangTitle.Text = _locService.Get("lang_title", "Application Language");
            TxtLangDesc.Text = _locService.Get("lang_desc", "Application display language");

            TxtThemeTitle.Text = _locService.Get("theme_title", "Themes & UI Customization");
            TxtThemesTitle.Text = _locService.Get("theme_select", "Available themes");
            TxtThemeAnime4kTitle.Text = _locService.Get("theme_anime4k_title", "ModernZ Theme + Anime4K");
            TxtThemeAnime4kDesc.Text = _locService.Get("theme_anime4k_desc", "Downloads ModernZ theme files and installs Anime4K shaders to %APPDATA%\\mpv");
            BtnInstallThemeAnime4k.Content = _locService.Get("btn_theme_anime4k", "Install Theme+Anime4K");
            TxtThemeImport.Text = _locService.Get("theme_import_title", "Import theme from URL (GitHub / Pastebin RAW JSON)");
            BtnImportTheme.Content = _locService.Get("btn_import_theme", "Download & Apply");
            TxtThemeCustomTitle.Text = _locService.Get("theme_custom_title", "Advanced UI Appearance Customization");
            TxtGlassShadowSection.Text = _locService.Get("label_glass_shadow_section", "Frosted Glass & Shadow Effects");
            TxtOpacitySection.Text = _locService.Get("label_opacity_section", "Independent Section Opacity");
            TxtColorPaletteSection.Text = _locService.Get("label_color_palette_section", "Color Palette");

            TxtCustomBgTitle.Text = _locService.Get("custom_bg_title", "Custom background image");
            BtnBrowseBgImage.Content = _locService.Get("btn_browse_bg", "Browse");
            BtnApplyCustomBg.Content = _locService.Get("btn_apply_bg", "Apply");
            TxtBlurLabel.Text = _locService.Get("label_blur", "Frosted glass (blur):");
            TxtOpacityLabel.Text = _locService.Get("label_card_opacity", "Card Opacity:");
            TxtSidebarOpacityLabel.Text = _locService.Get("label_sidebar_opacity", "Sidebar Opacity:");
            TxtInputOpacityLabel.Text = _locService.Get("label_input_opacity", "Inputs Opacity:");
            TxtButtonOpacityLabel.Text = _locService.Get("label_button_opacity", "Buttons Opacity:");
            TxtColorBgLabel.Text = _locService.Get("label_bg_color", "Background Color:");
            TxtColorSidebarLabel.Text = _locService.Get("label_sidebar_color", "Sidebar Color:");
            TxtColorCardLabel.Text = _locService.Get("label_card_color", "Card Color:");
            TxtColorAccentLabel.Text = _locService.Get("label_accent_color", "Accent Color:");
            TxtColorBorderLabel.Text = _locService.Get("label_border_color", "Border Color:");
            TxtColorTextPrimaryLabel.Text = _locService.Get("label_text_primary", "Primary Text Color:");
            TxtColorTextSecondaryLabel.Text = _locService.Get("label_text_secondary", "Secondary Text Color:");
            BtnApplyColors.Content = _locService.Get("btn_apply_colors", "Apply Colors");
            if (TxtColorLogoLabel != null)
                TxtColorLogoLabel.Text = _locService.Get("label_logo_color", "Logo Color:");
            BtnResetThemeDefaults.Content = _locService.Get("btn_reset_defaults", "Reset to Defaults");

            // Guide sections
            TxtGuideTitle.Text = _locService.Get("guide_title", "User Guide & Information");
            TxtGuideQuickStartTitle.Text = _locService.Get("guide_quickstart_title", "🎬 Quick Start & Video Playback");
            TxtGuideQuickStartSub.Text = _locService.Get("guide_quickstart_sub", "MPV Launcher allows seamless playback of internet streams and local media files with high-performance MPV.");
            TxtGuideQuickStartBody.Text = _locService.Get("guide_quickstart_body", "• URL Playback: Paste video URLs from YouTube, Twitch, Vimeo, X, or direct streams in the 'Player' tab and click 'Play with MPV' or press Enter.\n• Drag & Drop: Drag and drop video files (.mp4, .mkv, .webm, etc.) directly into the launcher window.\n• Playback History: Last 50 media links are saved in the history list; double-click any item to replay.");

            TxtGuideDepsTitle.Text = _locService.Get("guide_deps_title", "📦 Dependencies (MPV, yt-dlp, FFmpeg)");
            TxtGuideDepsSub.Text = _locService.Get("guide_deps_sub", "Three core tools are required for optimal operation:");
            TxtGuideDepsBody.Text = _locService.Get("guide_deps_body", "1. MPV: Hardware-accelerated modern media player engine.\n2. yt-dlp: CLI tool extracting streams from hundreds of video platforms. Update frequently with 'Update yt-dlp (-U)'.\n3. FFmpeg: Media stream demuxer and converter engine.\n\nAll tools can be downloaded and installed with one click in the 'Dependencies' tab.");

            TxtGuideBrowserTitle.Text = _locService.Get("guide_browser_title", "🌐 Browser Extension Integration");
            TxtGuideBrowserSub.Text = _locService.Get("guide_browser_sub", "Open web videos in MPV directly from your browser:");
            TxtGuideBrowserBody.Text = _locService.Get("guide_browser_body", "• Chrome / Edge / Brave / Opera: Open 'chrome://extensions', enable 'Developer Mode', and click 'Load unpacked' selecting the Extension Folder.\n• Firefox: Click 'Firefox Add-on' to install directly from the Mozilla Add-ons store.\n• Native Messaging Host: Automatically configured on application launch.");

            TxtGuideAnime4kTitle.Text = _locService.Get("guide_anime4k_title", "✨ Anime4K & ModernZ Theme");
            TxtGuideAnime4kSub.Text = _locService.Get("guide_anime4k_sub", "Anime4K is a real-time AI upscaling and edge-refining shader algorithm for anime and animated content.");
            TxtGuideAnime4kBody.Text = _locService.Get("guide_anime4k_body", "• Click 'Install Theme+Anime4K' in the 'Themes' tab to install ModernZ OSC and Anime4K GLSL shaders to %APPDATA%\\mpv.\n• MPV Shortcuts:\n   - CTRL+0: Disable Anime4K\n   - CTRL+1: Anime4K Mode A (Fast)\n   - CTRL+2: Anime4K Mode B (HQ)\n   - CTRL+3: Anime4K Mode C (Very Fast)\n   - CTRL+4: Anime4K Mode A+A\n   - CTRL+5: Anime4K Mode B+B\n   - CTRL+6: Anime4K Mode C+A");

            TxtGuideCustomTitle.Text = _locService.Get("guide_custom_title", "🎨 UI & Theme Customization Tips");
            TxtGuideCustomSub.Text = _locService.Get("guide_custom_sub", "Customize every visual aspect of the launcher in the 'Themes' tab:");
            TxtGuideCustomBody.Text = _locService.Get("guide_custom_body", "• Corner Radii: Adjust window and card corners between 0px and 24px.\n• Independent Opacity: Fine-tune transparency for cards, sidebar, inputs, and buttons.\n• Frosted Glass & Shadow: Adjust blur strength and shadow intensity in real time.\n• Reset to Defaults: Restore default theme and styling anytime with the red 'Reset to Defaults' button.");

            TxtStatus.Text = _locService.Get("status_ready", "Ready");
        }
        #endregion
    }
}
