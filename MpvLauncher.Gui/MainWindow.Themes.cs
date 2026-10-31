// Original implementation, MIT licensed.
//
// Part of the main window: appearance, themes and the live customisation controls.

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
        #region Themes
        /// <summary>
        /// Pushes the saved appearance values back into the sliders and colour
        /// boxes. Called at startup, after a theme switch and after a reset, so
        /// the controls always show what the window is actually painted with -
        /// the old startup path left some of them holding stale values.
        /// </summary>
        private void SyncAppearanceControls()
        {
            if (_configService == null) return;
            var cfg = _configService.Config;

            SliderBlur.Value = Math.Clamp(cfg.CustomBlur, SliderBlur.Minimum, SliderBlur.Maximum);
            SliderOpacity.Value = Math.Clamp(cfg.CustomCardOpacity * 100, SliderOpacity.Minimum, SliderOpacity.Maximum);
            SliderSidebarOpacity.Value = Math.Clamp(cfg.CustomSidebarOpacity * 100, SliderSidebarOpacity.Minimum, SliderSidebarOpacity.Maximum);
            SliderInputOpacity.Value = Math.Clamp(cfg.CustomInputOpacity * 100, SliderInputOpacity.Minimum, SliderInputOpacity.Maximum);
            SliderButtonOpacity.Value = Math.Clamp(cfg.CustomButtonOpacity * 100, SliderButtonOpacity.Minimum, SliderButtonOpacity.Maximum);

            TxtColorBg.Text = cfg.CustomBgColor;
            TxtColorSidebar.Text = cfg.CustomSidebarColor;
            TxtColorCard.Text = cfg.CustomCardColor;
            TxtColorAccent.Text = cfg.CustomAccentColor;
            TxtColorBorder.Text = cfg.CustomBorderColor;
            TxtColorTextPrimary.Text = cfg.CustomTextPrimary;
            TxtColorTextSecondary.Text = cfg.CustomTextSecondary;
            TxtColorLogo.Text = cfg.CustomLogoColor;
            TxtCustomBgUrl.Text = cfg.CustomBackground;
        }

        /// <summary>
        /// Saves what is on screen as a reusable theme file, then selects it so
        /// the user can see it in the list straight away.
        /// </summary>
        private void BtnSaveTheme_Click(object sender, RoutedEventArgs e)
        {
            var prompt = new PromptDialog(
                _locService.Get("save_theme", "Save as theme"),
                _locService.Get("theme_name_label", "Theme name"),
                _locService.Get("theme_name_default", "My theme"))
            {
                Owner = this
            };

            if (prompt.ShowDialog() != true || string.IsNullOrWhiteSpace(prompt.Value)) return;

            var model = _themeService.CaptureCurrent(prompt.Value, _configService.Config);
            string id = _themeService.SaveTheme(prompt.Value, model);
            _configService.Config.Theme = id;
            _configService.Save();

            PopulateThemes();
            SelectTheme(id);
            TxtStatus.Text = string.Format(
                _locService.Get("theme_saved", "Theme saved: {0}"),
                prompt.Value);
        }

        private void PopulateThemes()
        {
            PanelThemesWrap.Children.Clear();
            var themes = _themeService.GetAvailableThemes();
            string current = _configService.Config.Theme;

            foreach (var t in themes)
            {
                bool selected = string.Equals(t.Id, current, StringComparison.OrdinalIgnoreCase);

                var btn = new Button
                {
                    Style = (Style)FindResource("SecondaryButton"),
                    Margin = new Thickness(0, 0, 10, 10),
                    Padding = new Thickness(14, 10, 14, 10),
                    Tag = t.Id
                };

                if (selected)
                {
                    // The picker has to show which theme is live; without it a
                    // click looked like it did nothing at all.
                    btn.Background = UiTheme.Current.Accent;
                    btn.Foreground = UiTheme.Current.OnAccent;
                }

                var sp = new StackPanel();
                sp.Children.Add(new TextBlock
                {
                    Text = t.Name,
                    FontWeight = FontWeights.SemiBold,
                    FontSize = 13,
                    Foreground = selected ? UiTheme.Current.OnAccent : UiTheme.Current.TextPrimary
                });
                if (!string.IsNullOrEmpty(t.Author))
                    sp.Children.Add(new TextBlock
                    {
                        Text = t.Author,
                        FontSize = 11,
                        Foreground = selected ? UiTheme.Current.OnAccent : UiTheme.Current.TextMuted,
                        Opacity = selected ? 0.8 : 1.0,
                        Margin = new Thickness(0, 2, 0, 0)
                    });

                btn.Content = sp;
                btn.Click += (_, _) => SelectTheme(t.Id);

                PanelThemesWrap.Children.Add(btn);
            }
        }

        private void SelectTheme(string themeId)
        {
            _themeService.LoadTheme(themeId);
            UiTheme.AdoptTheme(_themeService.CurrentTheme, _configService.Config);
            _configService.Config.Theme = themeId;
            _configService.Save();

            UpdateTheme();
            SyncAppearanceControls();
            PopulateThemes();

            TxtStatus.Text = _locService.Get("theme_applied", "Theme applied.");
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
            var ui = UiTheme.Apply(tm, cfg);

            // The templates bind to these keys, so replacing the brushes here
            // restyles every reference in one pass. A colour that is still
            // assigned by hand somewhere is a leftover from before the palette.
            Resources["WindowBrush"] = ui.Window;
            Resources["CardBrush"] = ui.Card;
            Resources["CardBorderBrush"] = ui.CardBorder;
            Resources["WindowBorderBrush"] = ui.WindowBorder;
            Resources["HairlineBrush"] = ui.Hairline;
            Resources["SurfaceBrush"] = ui.Surface;
            Resources["InputBrush"] = ui.Input;
            Resources["PopupBrush"] = ui.Popup;
            Resources["HoverBrush"] = ui.Surface;
            Resources["TextPrimaryBrush"] = ui.TextPrimary;
            Resources["TextSecondaryBrush"] = ui.TextSecondary;
            Resources["TextMutedBrush"] = ui.TextMuted;
            Resources["AccentBrush"] = ui.Accent;
            Resources["AccentHoverBrush"] = ui.AccentHover;
            Resources["OnAccentBrush"] = ui.OnAccent;
            Resources["SuccessBrush"] = ui.Success;
            Resources["WarningBrush"] = ui.Warning;
            Resources["DangerBrush"] = ui.Danger;
            Resources["SecondaryButtonBrush"] = ui.SecondaryButton;
            Resources["DangerButtonBrush"] = ui.DangerButton;
            Resources["ButtonOpacity"] = ui.ButtonOpacity;

            string bgUrl = !string.IsNullOrEmpty(cfg.CustomBackground)
                ? cfg.CustomBackground
                : tm.Background.Image;

            // A theme may paint its background instead of shipping an image.
            // Both paths end in the same Border, so the overlay and blur below
            // apply either way.
            WindowFrame.Background = BuildBackgroundGradient(tm, cfg) ?? ui.Window;
            WindowFrame.BorderBrush = ui.WindowBorder;
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

            var cardRadius = new CornerRadius(cfg.CardCornerRadius);
            ApplyCardStyle(CardUrl, ui.Card, cardRadius);
            ApplyCardStyle(CardHistory, ui.Card, cardRadius);
            ApplyCardStyle(CardStatus, ui.Card, cardRadius);
            ApplyCardStyle(CardDownloads, ui.Card, cardRadius);
            ApplyCardStyle(CardBrowser, ui.Card, cardRadius);
            ApplyCardStyle(CardThemes, ui.Card, cardRadius);
            ApplyCardStyle(CardThemeAnime4k, ui.Card, cardRadius);
            ApplyCardStyle(CardThemeCustomizer, ui.Card, cardRadius);
            ApplyCardStyle(CardMpvConfig, ui.Card, cardRadius);
            ApplyCardStyle(CardLang, ui.Card, cardRadius);
            ApplyCardStyle(CardDataFolder, ui.Card, cardRadius);

            if (SidebarBorder != null) SidebarBorder.Background = ui.Sidebar;

            if (BorderBrandIcon != null)
            {
                // Logo tint: the theme's own logo colour, else the accent. The
                // config field is kept in sync by AdoptTheme and the picker.
                string logoColor = !string.IsNullOrWhiteSpace(cfg.CustomLogoColor)
                    ? cfg.CustomLogoColor
                    : (!string.IsNullOrWhiteSpace(tm.Colors?.Logo) ? tm.Colors.Logo : "");
                BorderBrandIcon.Background = string.IsNullOrWhiteSpace(logoColor)
                    ? ui.Accent
                    : ToBrush(logoColor, "#6366F1");
            }

            if (_activeNavButton != null)
            {
                _activeNavButton.Background = ui.Accent;
                _activeNavButton.Foreground = ui.OnAccent;
            }
            else if (BtnNavPlayer != null && BtnNavPlayer.Background != Brushes.Transparent)
            {
                BtnNavPlayer.Background = ui.Accent;
                BtnNavPlayer.Foreground = ui.OnAccent;
            }

            if (BtnPlayUrl != null) BtnPlayUrl.Background = ui.Accent;
            if (BtnInstallExtensions != null) BtnInstallExtensions.Background = ui.Accent;
            if (BtnInstallMpv != null) BtnInstallMpv.Background = ui.Accent;
            if (BtnInstallYtdlp != null) BtnInstallYtdlp.Background = ui.Accent;
            if (BtnInstallFfmpeg != null) BtnInstallFfmpeg.Background = ui.Accent;
            if (BtnSaveMpvSettings != null) BtnSaveMpvSettings.Background = ui.Accent;
            if (BtnApplyColors != null) BtnApplyColors.Background = ui.Accent;

            // The dialogs are built from the same palette, so one that is
            // already open follows a live theme change too.
            ThemePaletteChanged?.Invoke(ui);
        }

        /// <summary>Raised after the palette changes so open dialogs can restyle.</summary>
        public static event Action<UiPalette>? ThemePaletteChanged;

        /// <summary>Applies the fill and corner radius to one card border.</summary>
        private static void ApplyCardStyle(Border? card, Brush bg, CornerRadius radius)
        {
            if (card != null)
            {
                card.Background = bg;
                card.CornerRadius = radius;
            }
        }

        /// <summary>
        /// The theme's background gradient, or null when it uses an image or a
        /// flat colour. Angles follow the CSS convention: 90 points top to
        /// bottom, 135 from the top-left corner.
        /// </summary>
        private static Brush? BuildBackgroundGradient(ThemeModel theme, AppConfig config)
        {
            if (!string.IsNullOrEmpty(config.CustomBackground)) return null;

            var background = theme.Background;
            var stops = background?.Gradient;
            if (background == null || stops == null || stops.Count < 2) return null;

            double radians = (background.Angle - 90) * Math.PI / 180.0;
            var brush = new LinearGradientBrush
            {
                StartPoint = new Point(0.5 - Math.Cos(radians) / 2, 0.5 - Math.Sin(radians) / 2),
                EndPoint = new Point(0.5 + Math.Cos(radians) / 2, 0.5 + Math.Sin(radians) / 2)
            };

            for (int i = 0; i < stops.Count; i++)
            {
                brush.GradientStops.Add(new GradientStop(
                    UiTheme.Parse(stops[i], "#000000"),
                    (double)i / (stops.Count - 1)));
            }

            brush.Freeze();
            return brush;
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
                    "TxtColorLogo" => TxtColorLogo,
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

            SyncAppearanceControls();
            UpdateTheme();
            PopulateThemes();
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
    }
}
