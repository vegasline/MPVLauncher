using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
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
    public partial class MainWindow : Window
    {
        private readonly ConfigService _configService;
        private readonly LocalizationService _locService;
        private readonly ThemeService _themeService;
        private readonly ProcessService _procService;
        private readonly DependencyService _depService;
        private readonly BrowserIntegrationService _browserService;
        private readonly DownloadInstallService _downloadService;
        private readonly ModernZAnime4kService _modernZService;

        private const double WindowCornerRadius = 8;
        private bool _isInstalling;

        public MainWindow()
        {
            InitializeComponent();

            AppPaths.EnsureLayout();
            _configService = new ConfigService();
            AppPaths.SeedTemplates(overwriteEmbedded: _configService.Config.FirstRun);
            _locService = new LocalizationService(AppPaths.LanguagesDir, _configService.Config.Language);
            _themeService = new ThemeService(AppPaths.ThemesDir, _configService.Config.Theme);
            _procService = new ProcessService(_configService);
            _depService = new DependencyService(_procService);
            _browserService = new BrowserIntegrationService();
            _downloadService = new DownloadInstallService(_configService);
            _modernZService = new ModernZAnime4kService();

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

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            UpdateLocalization();
            UpdateTheme();
            LoadHistory();
            PopulateLanguages();
            PopulateThemes();

            SliderOpacity.Value = _configService.Config.CustomOpacity * 100;
            SliderBlur.Value = _configService.Config.CustomBlur;
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

        private void WindowContentClip_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            WindowContentClip.Clip = new RectangleGeometry(
                new Rect(0, 0, e.NewSize.Width, e.NewSize.Height),
                WindowCornerRadius,
                WindowCornerRadius);
        }
        #endregion

        #region Navigation
        private void SetActiveTab(StackPanel activePanel, Button activeButton)
        {
            PanelPlayer.Visibility = Visibility.Collapsed;
            PanelTools.Visibility = Visibility.Collapsed;
            PanelSettings.Visibility = Visibility.Collapsed;

            BtnNavPlayer.Background = Brushes.Transparent;
            BtnNavTools.Background = Brushes.Transparent;
            BtnNavSettings.Background = Brushes.Transparent;

            var muted = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#9AA3B2"));
            BtnNavPlayer.Foreground = muted;
            BtnNavTools.Foreground = muted;
            BtnNavSettings.Foreground = muted;

            activePanel.Visibility = Visibility.Visible;
            activeButton.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#6366F1"));
            activeButton.Foreground = Brushes.White;
        }

        private void BtnNavPlayer_Click(object sender, RoutedEventArgs e) => SetActiveTab(PanelPlayer, BtnNavPlayer);
        private void BtnNavTools_Click(object sender, RoutedEventArgs e) => SetActiveTab(PanelTools, BtnNavTools);
        private void BtnNavSettings_Click(object sender, RoutedEventArgs e) => SetActiveTab(PanelSettings, BtnNavSettings);
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

        private void Play(string target)
        {
            TxtStatus.Text = _locService.Get("status_playing", "Launching MPV...");
            var res = _procService.PlayMedia(target);
            TxtStatus.Text = res.Message;
            LoadHistory();
        }

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

        private async Task InitializeBrowserIntegrationAsync()
        {
            TxtExtensionStatus.Text = "Checking browser integration...";

            try
            {
                bool firstRun = _configService.Config.FirstRun;
                var result = await Task.Run(() =>
                {
                    _browserService.RepairNativeHostPath();
                    return firstRun
                        ? _browserService.InstallAll()
                        : (true, "Native messaging host path checked. Use Install / repair if the extension is missing.", "");
                });

                TxtExtensionStatus.Text = result.Item2;

                if (firstRun && result.Item1)
                {
                    _configService.Config.FirstRun = false;
                    _configService.Save();
                }
            }
            catch (Exception ex)
            {
                TxtExtensionStatus.Text = "Browser integration check failed: " + ex.Message;
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

        private void BtnInstallExtensions_Click(object sender, RoutedEventArgs e)
        {
            var res = _browserService.InstallAll();
            TxtExtensionStatus.Text = res.Message;
            TxtStatus.Text = res.Success
                ? _locService.Get("ext_installed", "Browser extensions installed. Restart your browsers.")
                : res.Message;
            MessageBox.Show(res.Message, "MPV Launcher", MessageBoxButton.OK,
                res.Success ? MessageBoxImage.Information : MessageBoxImage.Error);
        }

        private void BtnOpenExtensionFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                AppPaths.EnsureLayout();
                ResourceSeeder.Extract(overwriteExtension: true);
                new ExtensionInstallService().InstallAll();

                string extDir = AppPaths.ChromiumExtensionDir;
                Directory.CreateDirectory(extDir);

                try { Clipboard.SetText(extDir); } catch { }

                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{extDir}\"",
                    UseShellExecute = true
                });
                TxtStatus.Text = "Extension folder opened (path copied to clipboard).";
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
                string msg = _locService.Get("copied_chrome_extensions", "chrome://extensions panoya kopyalandı! Tarayıcınızın adres çubuğuna yapıştırın.");
                TxtExtensionStatus.Text = msg;
                TxtStatus.Text = msg;

                Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c start chrome://extensions || start edge://extensions",
                    UseShellExecute = true,
                    CreateNoWindow = true
                });
            }
            catch
            {
                TxtStatus.Text = "chrome://extensions panoya kopyalandı.";
            }
        }

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

        private void UpdateTheme()
        {
            var tm = _themeService.CurrentTheme;

            string bgUrl = !string.IsNullOrEmpty(_configService.Config.CustomBackground)
                ? _configService.Config.CustomBackground
                : tm.Background.Image;

            WindowFrame.Background = ToBrush(tm.Background.Color, "#12141A");
            BgOverlayBorder.Background = ToBrush(tm.Background.OverlayColor, "#B20D0F18");

            if (!string.IsNullOrEmpty(bgUrl))
            {
                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.UriSource = new Uri(bgUrl, UriKind.RelativeOrAbsolute);
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.EndInit();
                    BgImageElement.Source = bmp;
                }
                catch { BgImageElement.Source = null; }
            }
            else
            {
                BgImageElement.Source = null;
            }

            BgBlurEffect.Radius = _configService.Config.CustomBlur > 0
                ? _configService.Config.CustomBlur
                : tm.Background.BlurRadius;

            try
            {
                byte alpha = (byte)(_configService.Config.CustomOpacity * 255);
                var cardColor = ToColor(tm.Colors.CardBg, "#1A1E27");
                cardColor.A = alpha;

                var cardBrush = new SolidColorBrush(cardColor);
                CardUrl.Background = cardBrush;
                CardHistory.Background = cardBrush;
                CardStatus.Background = cardBrush;
                CardDownloads.Background = cardBrush;
                CardBrowser.Background = cardBrush;
                CardThemes.Background = cardBrush;
                CardThemeAnime4k.Background = cardBrush;
                CardCustomBg.Background = cardBrush;
                CardLang.Background = cardBrush;
                CardDataFolder.Background = cardBrush;
                PanelThemeEditor.Background = new SolidColorBrush(Color.FromArgb(0x30, cardColor.R, cardColor.G, cardColor.B));

                if (SidebarBorder != null)
                {
                    byte sideAlpha = (byte)Math.Clamp(alpha + 20, 30, 255);
                    SidebarBorder.Background = new SolidColorBrush(Color.FromArgb(sideAlpha, cardColor.R, cardColor.G, cardColor.B));
                }

                if (BorderBrandIcon != null && !string.IsNullOrWhiteSpace(tm.Colors.Accent))
                {
                    BorderBrandIcon.Background = ToBrush(tm.Colors.Accent, "#6366F1");
                }
            }
            catch { }
        }

        private static SolidColorBrush ToBrush(string value, string fallback)
            => new(ToColor(value, fallback));

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

        private void BtnApplyCustomBg_Click(object sender, RoutedEventArgs e)
        {
            _configService.Config.CustomBackground = TxtCustomBgUrl.Text.Trim();
            _configService.Save();
            UpdateTheme();
            TxtStatus.Text = "Custom background applied.";
        }

        private void SliderOpacity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (TxtOpacityVal != null && _configService != null)
            {
                int val = (int)e.NewValue;
                TxtOpacityVal.Text = $"{val}%";
                _configService.Config.CustomOpacity = val / 100.0;
                _configService.Save();
                UpdateTheme();
            }
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

        private void BtnThemeEditor_Click(object sender, RoutedEventArgs e)
        {
            PanelThemeEditor.Visibility = PanelThemeEditor.Visibility == Visibility.Visible
                ? Visibility.Collapsed
                : Visibility.Visible;

            if (PanelThemeEditor.Visibility == Visibility.Visible)
                FillThemeEditor();
        }

        private void FillThemeEditor()
        {
            var tm = _themeService.CurrentTheme;
            TxtThemeEditName.Text = tm.Name;
            TxtThemeEditAuthor.Text = tm.Author;
            TxtThemeEditBgImage.Text = tm.Background.Image;
            TxtThemeEditBgColor.Text = tm.Background.Color;
            TxtThemeEditCardColor.Text = tm.Colors.CardBg.StartsWith("rgba(", StringComparison.OrdinalIgnoreCase)
                ? "#1A1E27"
                : tm.Colors.CardBg;
            TxtThemeEditAccent.Text = tm.Colors.Accent;
            TxtThemeEditTextPrimary.Text = tm.Colors.TextPrimary;
            TxtThemeEditTextSecondary.Text = tm.Colors.TextSecondary;
            TxtThemeEditBlur.Text = tm.Background.BlurRadius.ToString("0.##", CultureInfo.InvariantCulture);
            TxtThemeEditOpacity.Text = tm.Opacity.Cards.ToString("0.##", CultureInfo.InvariantCulture);
        }

        private void BtnApplyThemeEdit_Click(object sender, RoutedEventArgs e)
        {
            var theme = BuildThemeFromEditor();
            _configService.Config.CustomBackground = "";
            _configService.Config.CustomBlur = theme.Background.BlurRadius;
            _configService.Config.CustomOpacity = theme.Opacity.Cards;
            _configService.Save();
            _themeService.ApplyTheme(theme);
            TxtCustomBgUrl.Text = "";
            SliderBlur.Value = theme.Background.BlurRadius;
            SliderOpacity.Value = theme.Opacity.Cards * 100;
            TxtStatus.Text = "Theme preview applied.";
        }

        private void BtnSaveThemeEdit_Click(object sender, RoutedEventArgs e)
        {
            var theme = BuildThemeFromEditor();
            _configService.Config.CustomBackground = "";
            _configService.Config.CustomBlur = theme.Background.BlurRadius;
            _configService.Config.CustomOpacity = theme.Opacity.Cards;
            var res = _themeService.SaveTheme(theme);
            TxtStatus.Text = res.Message;
            if (res.Success)
            {
                _configService.Config.Theme = theme.Id;
                _configService.Save();
                TxtCustomBgUrl.Text = "";
                SliderBlur.Value = theme.Background.BlurRadius;
                SliderOpacity.Value = theme.Opacity.Cards * 100;
                PopulateThemes();
            }
        }

        private ThemeModel BuildThemeFromEditor()
        {
            var current = _themeService.CurrentTheme;
            string name = string.IsNullOrWhiteSpace(TxtThemeEditName.Text) ? current.Name : TxtThemeEditName.Text.Trim();
            string id = MakeThemeId(name);
            double blur = ParseDouble(TxtThemeEditBlur.Text, current.Background.BlurRadius);
            double opacity = Math.Clamp(ParseDouble(TxtThemeEditOpacity.Text, current.Opacity.Cards), 0.2, 1.0);

            return new ThemeModel
            {
                Id = id,
                Name = name,
                Author = string.IsNullOrWhiteSpace(TxtThemeEditAuthor.Text) ? current.Author : TxtThemeEditAuthor.Text.Trim(),
                Background = new ThemeBackground
                {
                    Type = string.IsNullOrWhiteSpace(TxtThemeEditBgImage.Text) ? "solid" : "image",
                    Color = NormalizeColorText(TxtThemeEditBgColor.Text, current.Background.Color),
                    Image = TxtThemeEditBgImage.Text.Trim(),
                    BlurRadius = blur,
                    OverlayColor = current.Background.OverlayColor
                },
                Colors = new ThemeColors
                {
                    CardBg = NormalizeColorText(TxtThemeEditCardColor.Text, current.Colors.CardBg),
                    CardBorder = current.Colors.CardBorder,
                    Accent = NormalizeColorText(TxtThemeEditAccent.Text, current.Colors.Accent),
                    AccentHover = current.Colors.AccentHover,
                    TextPrimary = NormalizeColorText(TxtThemeEditTextPrimary.Text, current.Colors.TextPrimary),
                    TextSecondary = NormalizeColorText(TxtThemeEditTextSecondary.Text, current.Colors.TextSecondary),
                    TextMuted = current.Colors.TextMuted,
                    Success = current.Colors.Success,
                    Danger = current.Colors.Danger
                },
                Opacity = new ThemeOpacity
                {
                    Cards = opacity,
                    Inputs = current.Opacity.Inputs,
                    Buttons = current.Opacity.Buttons
                }
            };
        }

        private static string NormalizeColorText(string value, string fallback)
        {
            string trimmed = value.Trim();
            _ = ToColor(trimmed, fallback);
            return string.IsNullOrWhiteSpace(trimmed) ? fallback : trimmed;
        }

        private static double ParseDouble(string value, double fallback)
            => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
                ? parsed
                : fallback;

        private static string MakeThemeId(string name)
        {
            string raw = name.Trim().ToLowerInvariant();
            var chars = new char[raw.Length];
            int index = 0;
            foreach (char c in raw)
            {
                if (char.IsLetterOrDigit(c))
                    chars[index++] = c;
                else if (index > 0 && chars[index - 1] != '_')
                    chars[index++] = '_';
            }

            string id = new string(chars, 0, index).Trim('_');
            return string.IsNullOrWhiteSpace(id) ? "custom_theme" : id;
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

        private void UpdateLocalization()
        {
            Title = _locService.Get("app_title", "MPV Launcher");
            TxtTitleBar.Text = _locService.Get("app_title", "MPV Launcher");
            TxtSidebarBrand.Text = _locService.Get("app_title", "MPV Launcher");

            BtnNavPlayer.Content = _locService.Get("tab_player", "Player");
            BtnNavTools.Content = _locService.Get("tab_tools", "Dependencies");
            BtnNavSettings.Content = _locService.Get("tab_settings", "Themes & Settings");

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
            BtnInstallMpv.Content = _locService.Get("btn_download_install", "Download & Install");
            BtnInstallYtdlp.Content = _locService.Get("btn_download_install", "Download & Install");
            BtnInstallFfmpeg.Content = _locService.Get("btn_download_install", "Download & Install");

            TxtBrowserTitle.Text = _locService.Get("browser_integration_title", "Browser extension integration");
            TxtBrowserDesc.Text = _locService.Get("browser_desc", "Extensions and native messaging are installed automatically when the app starts. Restart your browser after the first run.");
            BtnInstallExtensions.Content = _locService.Get("btn_install_extensions", "Install / repair extensions");
            BtnOpenExtensionFolder.Content = _locService.Get("btn_open_ext_folder", "Open Extension Folder");
            BtnOpenChromeExtensions.Content = _locService.Get("btn_open_browser_ext", "Open chrome://extensions");

            TxtSettingsTitle.Text = _locService.Get("theme_title", "Appearance & Settings");
            TxtDataFolderTitle.Text = _locService.Get("data_folder_title", "Application data folder");
            BtnShowDataFolder.Content = _locService.Get("btn_show_folder", "Show folder");
            TxtThemeAnime4kTitle.Text = _locService.Get("theme_anime4k_title", "ModernZ Theme + Anime4K");
            TxtThemeAnime4kDesc.Text = _locService.Get("theme_anime4k_desc", "Downloads ModernZ theme files and installs Anime4K shaders to %APPDATA%\\mpv");
            BtnInstallThemeAnime4k.Content = _locService.Get("btn_theme_anime4k", "Tema+anime4k");
            TxtThemesTitle.Text = _locService.Get("theme_select", "Available themes");
            TxtThemeImport.Text = _locService.Get("theme_import_title", "Import theme from URL (GitHub / Pastebin RAW JSON)");
            BtnImportTheme.Content = _locService.Get("btn_import_theme", "Download & Apply");
            TxtCustomBgTitle.Text = _locService.Get("custom_bg_title", "Custom background & glass effect");
            BtnApplyCustomBg.Content = _locService.Get("btn_apply_bg", "Apply");
            TxtOpacityLabel.Text = _locService.Get("label_card_opacity", "Card opacity:");
            TxtBlurLabel.Text = _locService.Get("label_blur", "Frosted glass (blur):");
            TxtLangTitle.Text = _locService.Get("lang_title", "Language");

            TxtStatus.Text = _locService.Get("status_ready", "Ready");
        }
        #endregion
    }
}
