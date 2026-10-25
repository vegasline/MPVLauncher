// Original implementation, MIT licensed.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MpvLauncher.Gui.Services;

namespace MpvLauncher.Gui
{
    /// <summary>
    /// Update report for MPV, yt-dlp and FFmpeg.
    ///
    /// The window is only a view: the check has already run when it opens, and
    /// an update button calls back into the same installer the per-tool buttons
    /// use. Keeping the download out of the dialog means the dialog cannot
    /// invent an install path of its own, and the progress text it shows is the
    /// installer's own stage description.
    /// </summary>
    public sealed class ToolUpdatesDialog : Window
    {
        private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0x9A, 0xA3, 0xB2));
        private static readonly Brush Current = new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80));
        private static readonly Brush Outdated = new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24));
        private static readonly Brush Broken = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));

        private readonly Func<string, string, string> _loc;
        private readonly Func<ToolKind, IProgress<DownloadProgress>, System.Threading.Tasks.Task<InstallResult>> _install;
        private readonly List<ToolUpdateRow> _rows = new();
        private bool _busy;

        private sealed class ToolUpdateRow
        {
            public required ToolUpdateInfo Info { get; init; }
            public required TextBlock Status { get; init; }
            public required Button Action { get; init; }
            public bool Done { get; set; }
        }

        public ToolUpdatesDialog(
            IReadOnlyList<ToolUpdateInfo> items,
            Func<string, string, string> loc,
            Func<ToolKind, IProgress<DownloadProgress>, System.Threading.Tasks.Task<InstallResult>> install)
        {
            _loc = loc;
            _install = install;

            Title = _loc("tools_updates_title", "Program updates");
            Width = 660;
            Height = 440;
            MinWidth = 520;
            MinHeight = 340;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x2D));
            Foreground = Brushes.White;
            ShowInTaskbar = false;

            var root = new Grid { Margin = new Thickness(20) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var header = new TextBlock
            {
                Text = _loc("tools_updates_subtitle",
                    "Checked against the official release pages. MPV and FFmpeg publish daily builds, so they are only reported when yours is more than a month old."),
                FontSize = 12,
                Foreground = Muted,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 14)
            };
            root.Children.Add(header);

            var list = new StackPanel();
            foreach (var item in items)
            {
                list.Children.Add(BuildRow(item));
            }
            var scroll = new ScrollViewer
            {
                Content = list,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            Grid.SetRow(scroll, 1);
            root.Children.Add(scroll);

            var close = new Button
            {
                Content = _loc("tools_updates_close", "Close"),
                Padding = new Thickness(18, 8, 18, 8),
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0),
                IsCancel = true,
                IsDefault = true
            };
            close.Click += (_, _) => Close();
            Grid.SetRow(close, 2);
            root.Children.Add(close);

            Content = root;
            KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) Close(); };
        }

        private Border BuildRow(ToolUpdateInfo info)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var text = new StackPanel { Margin = new Thickness(0, 0, 14, 0) };
            text.Children.Add(new TextBlock
            {
                Text = info.Name,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold
            });

            if (!string.IsNullOrEmpty(info.Installed))
            {
                text.Children.Add(new TextBlock
                {
                    Text = string.Format(_loc("tools_updates_installed", "Installed: {0}"), info.Installed),
                    FontSize = 11,
                    Foreground = Muted,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 3, 0, 0)
                });
            }

            if (!string.IsNullOrEmpty(info.Latest))
            {
                text.Children.Add(new TextBlock
                {
                    Text = string.Format(_loc("tools_updates_latest", "Latest: {0}"), info.Latest),
                    FontSize = 11,
                    Foreground = Muted,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 2, 0, 0)
                });
            }

            var status = new TextBlock
            {
                Text = Describe(info),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = StateBrush(info.State),
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 300
            };
            text.Children.Add(status);
            Grid.SetColumn(text, 0);
            grid.Children.Add(text);

            var action = new Button
            {
                Content = _loc("tools_updates_update", "Update"),
                Padding = new Thickness(16, 7, 16, 7),
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = info.State is ToolUpdateState.Available or ToolUpdateState.NotInstalled
                    ? Visibility.Visible
                    : Visibility.Collapsed
            };

            var row = new ToolUpdateRow { Info = info, Status = status, Action = action };
            action.Tag = row;
            action.Click += OnUpdateClick;
            Grid.SetColumn(action, 1);
            grid.Children.Add(action);

            _rows.Add(row);

            return new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x22, 0x26, 0x31)),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14),
                Margin = new Thickness(0, 0, 0, 10),
                Child = grid
            };
        }

        private string Describe(ToolUpdateInfo info)
        {
            string text = info.State switch
            {
                ToolUpdateState.UpToDate => _loc("tools_updates_current", "Up to date"),
                ToolUpdateState.Available => _loc("tools_updates_available", "Update available"),
                ToolUpdateState.NotInstalled => _loc("tools_updates_not_installed", "Not installed"),
                _ => _loc("tools_updates_failed", "Check failed")
            };

            if (info.State == ToolUpdateState.Failed && !string.IsNullOrEmpty(info.Detail))
            {
                text += ": " + info.Detail;
            }

            return text;
        }

        private static Brush StateBrush(ToolUpdateState state) => state switch
        {
            ToolUpdateState.UpToDate => Current,
            ToolUpdateState.Available or ToolUpdateState.NotInstalled => Outdated,
            _ => Broken
        };

        private async void OnUpdateClick(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            if (sender is not Button { Tag: ToolUpdateRow row }) return;

            _busy = true;
            SetActionsEnabled(false);
            row.Status.Foreground = Muted;
            row.Status.Text = _loc("tools_updates_updating", "Updating...");

            var progress = new Progress<DownloadProgress>(p =>
            {
                row.Status.Text = p.Percent is { } pct
                    ? $"{_loc("tools_updates_updating", "Updating...")} {pct:0}%"
                    : _loc("tools_updates_updating", "Updating...");

                if (!string.IsNullOrEmpty(p.Detail))
                {
                    row.Status.Text += " · " + p.Detail;
                }
            });

            try
            {
                var result = await _install(row.Info.Kind, progress);

                if (result.Success)
                {
                    row.Done = true;
                    row.Status.Foreground = Current;
                    row.Status.Text = _loc("tools_updates_updated", "Updated");
                    row.Action.Visibility = Visibility.Collapsed;
                }
                else
                {
                    row.Status.Foreground = Broken;
                    row.Status.Text = result.Message;
                }
            }
            catch (Exception ex)
            {
                row.Status.Foreground = Broken;
                row.Status.Text = ex.Message;
            }
            finally
            {
                _busy = false;
                SetActionsEnabled(true);
            }
        }

        private void SetActionsEnabled(bool enabled)
        {
            foreach (var row in _rows)
            {
                // Finished rows stay collapsed: their button no longer means
                // anything once the install reported success.
                row.Action.IsEnabled = enabled && !row.Done;
            }
        }
    }
}
