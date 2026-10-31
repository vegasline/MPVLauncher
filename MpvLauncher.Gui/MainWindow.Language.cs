// Original implementation, MIT licensed.
//
// Part of the main window: applying a language to every label.

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
                // 1. Local file on disk. A theme names its artwork relative to
                // the themes folder, so a relative value has to be resolved
                // there rather than against the working directory.
                string localPath = bgUrl;
                if (!Path.IsPathRooted(bgUrl) && !bgUrl.Contains("://", StringComparison.Ordinal))
                    localPath = Path.Combine(AppPaths.ThemesDir, bgUrl.Replace('/', Path.DirectorySeparatorChar));

                if (File.Exists(localPath))
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.UriSource = new Uri(Path.GetFullPath(localPath), UriKind.Absolute);
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
            UpdateUrlHint();
            BtnPlayUrl.Content = _locService.Get("btn_play", "Play with MPV");
            TxtDropZone.Text = _locService.Get("drop_zone_text", "Drag and drop a video file here");
            BtnBrowseFile.Content = _locService.Get("btn_browse_file", "Select File");
            TxtHistoryTitle.Text = _locService.Get("history_title", "Playback history");
            Resources["HistoryPinTip"] = _locService.Get("history_pin_tip", "Pin to top");
            Resources["HistoryFolderTip"] = _locService.Get("history_folder_tip", "Show in folder");
            Resources["HistoryDeleteTip"] = _locService.Get("history_delete_tip", "Remove from history");
            BtnClearHistory.Content = _locService.Get("btn_clear_history", "Clear history");

            TxtToolsTitle.Text = _locService.Get("tools_title", "Dependencies & Browser");
            TxtSystemStatus.Text = _locService.Get("system_status", "System status");
            BtnCheckToolUpdates.Content = _locService.Get("btn_check_tool_updates", "Check program updates");
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

            // ---- Torrent streaming ----
            TxtTorrentTitle.Text = string.IsNullOrWhiteSpace(_torrentName)
                ? _locService.Get("torrent_card_title", "Torrent stream")
                : _torrentName;
            BtnTorrentCancel.Content = _locService.Get("torrent_btn_cancel", "Cancel");
            BtnTorrentOpenSettings.Content = _locService.Get("torrent_open_settings", "Open torrent settings");
            TxtTorrentSettingsTitle.Text = _locService.Get("torrent_settings_title", "Torrent streaming (optional)");
            TxtTorrentSettingsDesc.Text = _locService.Get("torrent_settings_desc",
                "Off by default. When enabled, torrent links, .torrent files and info hashes are streamed through a local helper.");
            ChkTorrentEnabled.Content = _locService.Get("torrent_enable", "Enable torrent streaming");
            TxtTorrentPrivacy.Text = _locService.Get("torrent_privacy_text",
                "While a torrent streams, your IP address is visible to peers and trackers. Use this only for content you own or that is legal to share.");
            TxtTorrentDownloadDirLabel.Text = _locService.Get("torrent_dir_label", "Temporary download folder:");
            BtnBrowseTorrentDir.Content = _locService.Get("torrent_browse", "Browse");
            BtnResetTorrentDir.Content = _locService.Get("torrent_reset", "Default");
            ChkTorrentRememberFile.Content = _locService.Get("torrent_remember_file",
                "Remember the last played file in multi-file torrents");
            TxtTorrentHelperTitle.Text = _locService.Get("torrent_helper_label", "Helper program (mpvtorrent)");
            ExpTorrentAdvanced.Header = _locService.Get("torrent_advanced", "Advanced");
            TxtTorrentAdvancedNote.Text = _locService.Get("torrent_advanced_note",
                "Values are clamped to safe limits. Upload is off at 0 KiB/s.");
            TxtTorrentWindowLabel.Text = _locService.Get("torrent_window_label", "Forward window (MiB)");
            TxtTorrentBufferLabel.Text = _locService.Get("torrent_buffer_label", "mpv forward buffer (MiB)");
            TxtTorrentUploadLabel.Text = _locService.Get("torrent_upload_label", "Upload limit (KiB/s, 0 = off)");
            TxtTorrentConnsLabel.Text = _locService.Get("torrent_conns_label", "Maximum connections");
            TxtTorrentHalfOpenLabel.Text = _locService.Get("torrent_halfopen_label", "Simultaneous connection attempts");
            TxtTorrentTailWaitLabel.Text = _locService.Get("torrent_tailwait_label",
                "Wait for the end of the file before starting (s, 0 = start at once)");
            TxtTorrentTrackersLabel.Text = _locService.Get("torrent_trackers_label", "Tracker list (one URL per line):");
            BtnSaveTorrentSettings.Content = _locService.Get("torrent_save", "Save torrent settings");
            UpdateTorrentHelperStatus();

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
            ChkAutoUpdate.Content = _locService.Get("label_auto_update", "Download new versions automatically");
            TxtUpdateTitle.Text = _locService.Get("label_updates", "Updates");

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
            BtnSaveTheme.Content = _locService.Get("save_theme", "Save as theme");
            BtnResetThemeDefaults.Content = _locService.Get("btn_reset_defaults", "Reset to Defaults");

            // Shared tooltip for the eight swatch buttons, so the wording lives
            // in the language packs instead of once per button in XAML.
            Resources["PickColorTip"] = _locService.Get("pick_color", "Pick a colour");

            // Guide sections
            TxtGuideTitle.Text = _locService.Get("guide_title", "User Guide & Information");
            TxtGuideQuickStartTitle.Text = _locService.Get("guide_quickstart_title", "🎬 Quick Start & Video Playback");
            TxtGuideQuickStartSub.Text = _locService.Get("guide_quickstart_sub", "MPV Launcher allows seamless playback of internet streams and local media files with high-performance MPV.");
            TxtGuideQuickStartBody.Text = _locService.Get("guide_quickstart_body", "• URL Playback: Paste video URLs from YouTube, Twitch, Vimeo, X, or direct streams in the 'Player' tab and click 'Play with MPV' or press Enter.\n• Drag & Drop: Drag and drop video files (.mp4, .mkv, .webm, etc.) directly into the launcher window.\n• Playback History: Last 50 media links are saved in the history list; double-click any item to replay.");

            TxtGuideDepsTitle.Text = _locService.Get("guide_deps_title", "📦 Dependencies (MPV, yt-dlp, FFmpeg)");
            TxtGuideDepsSub.Text = _locService.Get("guide_deps_sub", "Three core tools are required for optimal operation:");
            TxtGuideDepsBody.Text = _locService.Get("guide_deps_body", "1. MPV: Hardware-accelerated modern media player engine.\n2. yt-dlp: CLI tool extracting streams from hundreds of video platforms. Use 'Check program updates' in the Dependencies tab when a stream stops working.\n3. FFmpeg: Media stream demuxer and converter engine.\n\nAll tools can be downloaded and installed with one click in the 'Dependencies' tab.");

            TxtGuideBrowserTitle.Text = _locService.Get("guide_browser_title", "🌐 Browser Extension Integration");
            TxtGuideBrowserSub.Text = _locService.Get("guide_browser_sub", "Open web videos in MPV directly from your browser:");
            TxtGuideBrowserBody.Text = _locService.Get("guide_browser_body", "• Chrome / Edge / Brave / Opera: Open 'chrome://extensions', enable 'Developer Mode', and click 'Load unpacked' selecting the Extension Folder.\n• Firefox: Click 'Firefox Add-on' to install directly from the Mozilla Add-ons store.\n• Native Messaging Host: Automatically configured on application launch.");

            TxtGuideAnime4kTitle.Text = _locService.Get("guide_anime4k_title", "✨ Anime4K & ModernZ Theme");
            TxtGuideAnime4kSub.Text = _locService.Get("guide_anime4k_sub", "Anime4K is a real-time AI upscaling and edge-refining shader algorithm for anime and animated content.");
            TxtGuideAnime4kBody.Text = _locService.Get("guide_anime4k_body", "• Click 'Install Theme+Anime4K' in the 'Themes' tab to install ModernZ OSC and Anime4K GLSL shaders to %APPDATA%\\mpv.\n• MPV Shortcuts:\n   - CTRL+0: Disable Anime4K\n   - CTRL+1: Anime4K Mode A (Fast)\n   - CTRL+2: Anime4K Mode B (HQ)\n   - CTRL+3: Anime4K Mode C (Very Fast)\n   - CTRL+4: Anime4K Mode A+A\n   - CTRL+5: Anime4K Mode B+B\n   - CTRL+6: Anime4K Mode C+A");

            TxtGuideCustomTitle.Text = _locService.Get("guide_custom_title", "🎨 UI & Theme Customization Tips");
            TxtGuideCustomSub.Text = _locService.Get("guide_custom_sub", "Customize every visual aspect of the launcher in the 'Themes' tab:");
            TxtGuideCustomBody.Text = _locService.Get("guide_custom_body", "• Corner Radii: Adjust window and card corners between 0px and 24px.\n• Independent Opacity: Fine-tune transparency for cards, sidebar, inputs, and buttons.\n• Frosted Glass & Shadow: Adjust blur strength and shadow intensity in real time.\n• Reset to Defaults: Restore default theme and styling anytime with the red 'Reset to Defaults' button.");

            // Report page
            BtnNavReport.Content = _locService.Get("tab_report", "Report");
            TxtReportTitle.Text = _locService.Get("report_title", "Report a problem");
            TxtReportKindLabel.Text = _locService.Get("report_kind_label", "Type");
            TxtReportMessageLabel.Text = _locService.Get("report_message_label", "Message");
            TxtReportMessage.ToolTip = _locService.Get("report_message_hint", "What happened, what you expected, and the steps to reproduce it.");
            TxtReportAddFileLabel.Text = _locService.Get("report_add_file", "Add file");
            TxtReportFilesHint.Text = _locService.Get("report_files_hint", "Images or logs, up to 8 MB each.");
            TxtReportSendLabel.Text = _locService.Get("report_send", "Send report");
            TxtReportWebhookNote.Text = _locService.Get("report_webhook_note", "Reports are delivered to the developer's Discord webhook.");
            TxtReportTrNote.Text = _locService.Get("report_tr_note", "Discord is blocked in Turkey. Turn on a VPN before sending.");
            Resources["ReportRemoveTip"] = _locService.Get("report_remove", "Remove");

            // The kinds are rebuilt rather than relabelled: a language change
            // must keep the selection but not the previous language's strings.
            PopulateReportKinds();

            // The VPN note only makes sense where Discord is blocked.
            UpdateReportNotes();

            // Icon-only controls get their name here, with the label they show.
            UpdateReportAutomationNames();

            // The status line is deliberately not touched here. It is transient
            // state, not a label: whoever has something to say sets it, and
            // rewriting it to "Ready" on every language change erased whatever
            // was there - a staged update, a download failure, a relaunch
            // error. The initial value is set where the window loads.
            //
            // State work lives here, at the end, and not among the labels
            // above: the updates card is redrawn from the state it is holding
            // rather than given a fixed label, so a staged update keeps its
            // install button across a language change. It used to sit in the
            // middle of the label table, where anyone changing the card's text
            // had to know to look for a re-render buried between two strings.
            RenderUpdateCard();
        }
        #endregion
    }
}
