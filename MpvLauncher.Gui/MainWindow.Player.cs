// Original implementation, MIT licensed.
//
// Part of the main window: the player card and the playback history.

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

        /// <summary>
        /// Ctrl+V outside a text box drops the clipboard into the URL box and
        /// starts playback: the usual flow is copying a link and wanting to
        /// watch it, not editing it. Inside a box the key keeps its normal
        /// paste behaviour.
        /// </summary>
        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.V || (Keyboard.Modifiers & ModifierKeys.Control) == 0) return;

            // Let the focused field handle its own paste.
            if (Keyboard.FocusedElement is TextBox || Keyboard.FocusedElement is RichTextBox) return;

            string text = "";
            try
            {
                if (Clipboard.ContainsText()) text = Clipboard.GetText().Trim();
            }
            catch
            {
                // A clipboard another process is holding is not an error.
            }

            if (string.IsNullOrWhiteSpace(text)) return;

            e.Handled = true;
            TxtUrlInput.Text = text;
            Play(text);
        }

        /// <summary>
        /// Coming back to the window is the moment to pick up history another
        /// process wrote: the native host and the extension add entries while
        /// this window sits in the background, and until now they only showed
        /// up after a restart.
        /// </summary>
        private void Window_Activated(object? sender, EventArgs e)
        {
            _configService.RefreshHistoryFromDisk();
            LoadHistory();
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
            if (!e.Data.GetDataPresent(DataFormats.FileDrop))
                return;

            string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (files.Length == 0)
                return;

            Play(files[0]);

            // A drop can carry a whole selection. Only the first file can be
            // played (there is no queue), and dropping six files and seeing one
            // start without a word reads as a bug, so say what was ignored.
            if (files.Length > 1)
            {
                TxtStatus.Text = string.Format(
                    _locService.Get("drop_multiple", "Playing the first file. {0} more were ignored."),
                    files.Length - 1);
            }
        }

        /// <summary>
        /// The single place playback is started from, whatever the source: the
        /// URL box, a dropped file, a history row or a pasted path. Centralising
        /// it means the status line and the history refresh happen once.
        /// </summary>
        private void Play(string target)
        {
            // Torrent sources are intercepted before mpv is involved. The gate
            // is checked here rather than in the click handlers so the URL box,
            // the file picker, drag/drop and history rows all behave the same.
            if (TorrentSourceParser.TryParse(target, out var torrent, out _))
            {
                _ = PlayTorrentAsync(torrent);
                return;
            }

            TxtStatus.Text = _locService.Get("status_playing", "Launching MPV...");
            var res = _procService.PlayMedia(target);

            // The launcher answers in keys, never sentences, so this line is the
            // only place a launch message becomes words. A loader failure is
            // the one case where the raw reason is not enough on its own: the
            // user has a dialog on screen with a procedure name in it and no
            // way to know it is not an mpv setting to change, so the message
            // names the actual remedy.
            TxtStatus.Text = res.Probe.Health == MpvHealth.NotRunnable
                ? (res.Probe.IsLoaderProblem
                    ? _locService.Format("play_mpv_loader", "", res.Probe.Describe()) +
                      " " + _locService.Get("play_mpv_loader_fix",
                          "This is almost always an out-of-date graphics driver or Vulkan runtime " +
                          "- mpv needs Vulkan 1.1 or newer. Update the driver. No option in " +
                          "MPVLauncher can work around this.")
                    : _locService.Format(res.MessageKey, "mpv cannot start ({0}).", res.Args))
                : (res.Args.Length > 0
                    ? _locService.Format(res.MessageKey, "", res.Args)
                    : _locService.Get(res.MessageKey));

            LoadHistory();

            // A link the user pasted has no title yet: the row is written first
            // (playback must not wait on a web request) and the page is asked for
            // its <title> in the background. A stream or a local file simply
            // keeps showing its link.
            _ = FillHistoryTitleAsync(target);
        }

        /// <summary>
        /// Gives rows that predate page titles a second chance.
        ///
        /// A history written by an older build stores the link where the title
        /// belongs, and nothing ever goes back to it: the row is only renamed
        /// while the launcher is the one that opened it. So an upgraded machine
        /// kept showing bare URLs for everything it had already played, which
        /// reads as "titles stopped working" even though the feature is new.
        ///
        /// Runs once per start, a few rows at a time, and stops at the first
        /// network hiccup for that row. Local files and streams are skipped by
        /// <see cref="PageTitleService.CanQuery"/>, so the cost is a handful of
        /// small page requests, and only until the list is caught up.
        /// </summary>
        private async Task BackfillHistoryTitlesAsync()
        {
            const int perRun = 6;

            try
            {
                var pending = _configService.Config.PlaybackHistory
                    .Where(x => string.IsNullOrWhiteSpace(x.Title) || x.Title == x.Url)
                    .Select(x => x.Url)
                    .Where(PageTitleService.CanQuery)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(perRun)
                    .ToList();

                if (pending.Count == 0) return;

                bool changed = false;
                foreach (string url in pending)
                {
                    string? title = await PageTitleService.FetchAsync(url);
                    if (string.IsNullOrWhiteSpace(title)) continue;
                    if (_configService.SetHistoryTitle(url, title!)) changed = true;
                }

                if (changed) LoadHistory();
            }
            catch
            {
                // Titles are a nicety, and this runs unobserved on the UI
                // thread's schedule. A failure leaves the rows as they were.
            }
        }

        /// <summary>
        /// Replaces a link-only row title with the page's own title.
        ///
        /// The extension sends the tab title with the playback, so this runs only
        /// for what the launcher itself opened. It stays silent on purpose: no
        /// title means the row keeps the link it already had.
        /// </summary>
        private async Task FillHistoryTitleAsync(string target)
        {
            try
            {
                var item = _configService.Config.PlaybackHistory
                    .FirstOrDefault(x => x.Url == target);
                if (item == null) return;
                if (!string.IsNullOrWhiteSpace(item.Title) && item.Title != item.Url) return;

                string? title = await PageTitleService.FetchAsync(target);
                if (string.IsNullOrWhiteSpace(title)) return;

                if (_configService.SetHistoryTitle(target, title!)) LoadHistory();
            }
            catch
            {
                // A title is a nicety; it never disturbs playback.
            }
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
            LstHistory.ItemsSource = FilteredHistory();
        }

        /// <summary>
        /// Keeps the history list live while the window is open.
        ///
        /// A playback started from the browser is recorded by the native host in
        /// its own process. The window used to re-read the file only when it was
        /// activated, which is why a row appeared to need a restart: the list is
        /// the one thing on screen that changes without the user touching the
        /// launcher. Watching the file turns that into a row that appears as
        /// soon as mpv has been handed the URL.
        ///
        /// The host writes with File.WriteAllText, which raises more than one
        /// event per save, so reloads are debounced. A reload only rebinds the
        /// list when something actually changed, so an unrelated settings save
        /// does not disturb the row the user was aiming at.
        /// </summary>
        private void StartHistoryWatcher()
        {
            try
            {
                _historyRefreshTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(400)
                };
                _historyRefreshTimer.Tick += (_, _) =>
                {
                    _historyRefreshTimer!.Stop();
                    RefreshHistoryIfChanged();
                };

                _historyWatcher = new FileSystemWatcher(AppPaths.SettingsDir, "history.json")
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                    EnableRaisingEvents = true
                };

                void Schedule(object? sender, FileSystemEventArgs args)
                {
                    // The event arrives on a pool thread; the timer must be
                    // touched on the thread that created it.
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        _historyRefreshTimer?.Stop();
                        _historyRefreshTimer?.Start();
                    }));
                }

                _historyWatcher.Changed += Schedule;
                _historyWatcher.Created += Schedule;
                _historyWatcher.Renamed += (sender, args) => Schedule(sender, args);

                Closed += (_, _) =>
                {
                    _historyWatcher?.Dispose();
                    _historyWatcher = null;
                    _historyRefreshTimer?.Stop();
                };
            }
            catch
            {
                // An unwatchable folder is not fatal: the list still refreshes
                // whenever the window is activated.
            }
        }

        /// <summary>Re-reads the host's rows and rebinds only when they moved.</summary>
        private void RefreshHistoryIfChanged()
        {
            var before = _configService.Config.PlaybackHistory
                .Select(x => x.Url + "|" + x.DisplayTitle + "|" + x.Pinned)
                .ToList();

            _configService.RefreshHistoryFromDisk();

            var after = _configService.Config.PlaybackHistory
                .Select(x => x.Url + "|" + x.DisplayTitle + "|" + x.Pinned)
                .ToList();

            if (!before.SequenceEqual(after)) LoadHistory();
        }

        /// <summary>
        /// The history as the list should show it: the search box narrows by
        /// title and URL, case-insensitively.
        /// </summary>
        private List<HistoryItem> FilteredHistory()
        {
            var items = _configService.Config.PlaybackHistory;
            string query = TxtHistorySearch?.Text?.Trim() ?? "";
            if (query.Length == 0) return items;

            return items.Where(i =>
                (i.DisplayTitle ?? "").Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                (i.Url ?? "").Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToList();
        }

        private void TxtHistorySearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            LoadHistory();
        }

        private void BtnHistoryPin_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not HistoryItem item) return;
            _configService.TogglePin(item.Url);
            LoadHistory();
        }

        private void BtnHistoryDelete_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not HistoryItem item) return;
            _configService.RemoveHistory(item.Url);
            LoadHistory();
        }

        /// <summary>
        /// Opens the folder for a local row, with the file already selected.
        /// Streams and torrents have no folder, and the button is hidden there.
        /// </summary>
        private void BtnHistoryFolder_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not HistoryItem item) return;

            try
            {
                string path = item.Url;
                if (path.StartsWith("file://", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(path, UriKind.Absolute, out var uri))
                    path = uri.LocalPath;

                if (File.Exists(path))
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
                else if (Directory.Exists(path))
                    Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch
            {
                // A missing drive or a locked shell is not worth a crash.
            }
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
    }
}
