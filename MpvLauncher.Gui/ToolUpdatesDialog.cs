// Original implementation, MIT licensed.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
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
    ///
    /// The height follows the content instead of being fixed: three rows plus a
    /// long failure message did not fit in the old 440px box and forced a
    /// scrollbar over three items.
    /// </summary>
    public sealed class ToolUpdatesDialog : Window
    {
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
            Width = 720;
            MinWidth = 560;
            SizeToContent = SizeToContent.Height;
            MaxHeight = Math.Max(420, SystemParameters.WorkArea.Height * 0.86);
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.NoResize;
            FontSize = 12;

            // Frameless, like the main window: a system caption bar on a
            // translucent theme was the one piece that never matched.
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;

            var palette = UiTheme.Current;

            var shell = new Border
            {
                Background = palette.Popup,
                BorderBrush = palette.CardBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Margin = new Thickness(14),
                Effect = new DropShadowEffect
                {
                    BlurRadius = 26,
                    ShadowDepth = 0,
                    Opacity = 0.5,
                    Color = Colors.Black
                }
            };

            var layout = new Grid();
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            shell.Child = layout;

            var titleBar = new Grid { Height = 42, Background = Brushes.Transparent };
            titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var title = new TextBlock
            {
                Text = _loc("tools_updates_title", "Program updates"),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = palette.TextPrimary,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(18, 0, 0, 0)
            };
            titleBar.Children.Add(title);

            var closeGlyph = new Button
            {
                Width = 38,
                Height = 30,
                Margin = new Thickness(0, 0, 8, 0),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                ToolTip = _loc("tools_updates_close", "Close"),
                Cursor = System.Windows.Input.Cursors.Hand,
                Content = new System.Windows.Shapes.Path
                {
                    Data = Geometry.Parse("M 0,0 L 10,10 M 10,0 L 0,10"),
                    Stroke = palette.TextSecondary,
                    StrokeThickness = 1.4,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            closeGlyph.Click += (_, _) => Close();

            var closeButtonTemplate = new ControlTemplate(typeof(Button));
            var border = new FrameworkElementFactory(typeof(Border), "chrome");
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
            presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(presenter);
            closeButtonTemplate.VisualTree = border;
            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BackgroundProperty, palette.Surface, "chrome"));
            closeButtonTemplate.Triggers.Add(hover);
            closeGlyph.Template = closeButtonTemplate;

            Grid.SetColumn(closeGlyph, 1);
            titleBar.Children.Add(closeGlyph);

            titleBar.MouseLeftButtonDown += (_, e) =>
            {
                if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
                {
                    try { DragMove(); } catch { }
                }
            };

            layout.Children.Add(titleBar);

            var root = new Grid { Margin = new Thickness(24, 0, 24, 22) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(root, 1);
            layout.Children.Add(root);

            var header = new TextBlock
            {
                Text = _loc("tools_updates_subtitle",
                    "Checked against the official release pages. MPV and FFmpeg publish daily builds, so they are only reported when yours is more than a month old."),
                FontSize = 12,
                Foreground = palette.TextSecondary,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 16)
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
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MaxHeight = Math.Max(260, SystemParameters.WorkArea.Height * 0.55),
                Margin = new Thickness(-4, 0, -4, 0),
                Padding = new Thickness(4, 0, 4, 0)
            };
            ScrollAutoHide.SetEnabled(scroll, true);
            Grid.SetRow(scroll, 1);
            root.Children.Add(scroll);

            var close = new Button
            {
                Content = _loc("tools_updates_close", "Close"),
                Style = DialogTheme.Style(this, "SecondaryButton"),
                Padding = new Thickness(22, 9, 22, 9),
                MinWidth = 110,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 16, 0, 0),
                IsCancel = true,
                IsDefault = true
            };
            close.Click += (_, _) => Close();
            Grid.SetRow(close, 2);
            root.Children.Add(close);

            Content = shell;
            KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) Close(); };
            Loaded += (_, _) => DialogTheme.Apply(this);
        }

        private Border BuildRow(ToolUpdateInfo info)
        {
            var palette = UiTheme.Current;

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var text = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };
            text.Children.Add(new TextBlock
            {
                Text = info.Name,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = palette.TextPrimary
            });

            if (!string.IsNullOrEmpty(info.Installed))
            {
                text.Children.Add(new TextBlock
                {
                    Text = string.Format(_loc("tools_updates_installed", "Installed: {0}"), info.Installed),
                    FontSize = 11,
                    Foreground = palette.TextSecondary,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 4, 0, 0)
                });
            }

            if (!string.IsNullOrEmpty(info.Latest))
            {
                text.Children.Add(new TextBlock
                {
                    Text = string.Format(_loc("tools_updates_latest", "Latest: {0}"), info.Latest),
                    FontSize = 11,
                    Foreground = palette.TextSecondary,
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
                Margin = new Thickness(0, 8, 0, 0)
            };
            text.Children.Add(status);
            Grid.SetColumn(text, 0);
            grid.Children.Add(text);

            var action = new Button
            {
                Content = _loc("tools_updates_update", "Update"),
                Style = DialogTheme.Style(this, "ModernButton"),
                Padding = new Thickness(18, 8, 18, 8),
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
                Background = palette.Surface,
                BorderBrush = palette.CardBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(16),
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

        private static Brush StateBrush(ToolUpdateState state)
        {
            var palette = UiTheme.Current;
            return state switch
            {
                ToolUpdateState.UpToDate => palette.Success,
                ToolUpdateState.Available or ToolUpdateState.NotInstalled => palette.Warning,
                _ => palette.Danger
            };
        }

        private async void OnUpdateClick(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            if (sender is not Button { Tag: ToolUpdateRow row }) return;

            _busy = true;
            SetActionsEnabled(false);
            row.Status.Foreground = UiTheme.Current.TextSecondary;
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
                    row.Status.Foreground = UiTheme.Current.Success;
                    row.Status.Text = _loc("tools_updates_updated", "Updated");
                    row.Action.Visibility = Visibility.Collapsed;
                }
                else
                {
                    row.Status.Foreground = UiTheme.Current.Danger;
                    row.Status.Text = result.Message;
                }
            }
            catch (Exception ex)
            {
                row.Status.Foreground = UiTheme.Current.Danger;
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
