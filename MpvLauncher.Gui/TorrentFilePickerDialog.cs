// Original implementation, MIT licensed.

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MpvLauncher.Gui.Services;

namespace MpvLauncher.Gui
{
    /// <summary>
    /// File chooser for a multi-file torrent. It is shown before playback
    /// starts, so no HTTP endpoint exists yet and nothing is downloading except
    /// the small metadata probe. The dialog deliberately shows no thumbnails or
    /// previews: choosing a file only decides which byte range will be fetched.
    /// </summary>
    public sealed class TorrentFilePickerDialog : Window
    {
        private readonly ListView _list;
        private readonly List<TorrentFileRow> _rows = new();

        public int SelectedIndex { get; private set; } = -1;

        private sealed record TorrentFileRow(int Index, string Name, string Size);

        public TorrentFilePickerDialog(
            IReadOnlyList<Services.TorrentFileEntry> files,
            string torrentName,
            System.Func<string, string, string>? loc = null)
        {
            string L(string key, string fallback) => loc == null ? fallback : loc(key, fallback);

            Title = L("torrent_picker_title", "Select a file");
            Width = 720;
            Height = 480;
            MinWidth = 520;
            MinHeight = 320;
            MaxHeight = Math.Max(360, SystemParameters.WorkArea.Height * 0.86);
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;

            var root = new Grid { Margin = new Thickness(18) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var header = new TextBlock
            {
                Text = torrentName,
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = UiTheme.Current.TextPrimary,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 0, 0, 12)
            };
            root.Children.Add(header);

            _list = new ListView
            {
                Background = UiTheme.Current.Surface,
                Foreground = UiTheme.Current.TextPrimary,
                BorderBrush = UiTheme.Current.CardBorder,
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 0, 12),
                ItemContainerStyle = BuildRowStyle()
            };
            var view = new GridView();
            view.Columns.Add(new GridViewColumn
            {
                Header = L("torrent_picker_file", "File"),
                Width = 520,
                DisplayMemberBinding = new System.Windows.Data.Binding(nameof(TorrentFileRow.Name))
            });
            view.Columns.Add(new GridViewColumn
            {
                Header = L("torrent_picker_size", "Size"),
                Width = 120,
                DisplayMemberBinding = new System.Windows.Data.Binding(nameof(TorrentFileRow.Size))
            });
            _list.View = view;
            view.ColumnHeaderContainerStyle = BuildColumnHeaderStyle();
            Grid.SetRow(_list, 1);
            root.Children.Add(_list);

            foreach (var file in files)
            {
                _rows.Add(new TorrentFileRow(file.Index, file.Name, file.DisplaySize));
            }
            _list.ItemsSource = _rows;
            if (_rows.Count > 0) _list.SelectedIndex = 0;
            _list.MouseDoubleClick += (_, _) => Accept();
            _list.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) Accept();
            };

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            var cancel = new Button
            {
                Content = L("torrent_picker_cancel", "Cancel"),
                Style = DialogTheme.Style(this, "SecondaryButton"),
                Padding = new Thickness(18, 8, 18, 8),
                Margin = new Thickness(0, 0, 8, 0),
                IsCancel = true
            };
            cancel.Click += (_, _) => { DialogResult = false; Close(); };
            var select = new Button
            {
                Content = L("torrent_picker_play", "Play selected file"),
                Style = DialogTheme.Style(this, "ModernButton"),
                Padding = new Thickness(18, 8, 18, 8),
                IsDefault = true
            };
            select.Click += (_, _) => Accept();
            buttons.Children.Add(cancel);
            buttons.Children.Add(select);
            Grid.SetRow(buttons, 2);
            root.Children.Add(buttons);

            Content = root;
            KeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape) { DialogResult = false; Close(); }
            };

            // The owner is assigned after construction, so the palette is
            // applied twice: once now for the brushes, once when the styles
            // become reachable through the owner.
            DialogTheme.Apply(this);
            Loaded += (_, _) => DialogTheme.Apply(this);
        }

        private static SolidColorBrush Brush(byte r, byte g, byte b)
            => new(Color.FromRgb(r, g, b));

        /// <summary>
        /// Replaces the system ListView item style, whose "selected" state is a
        /// pale highlight that turns white text invisible on this dark window.
        /// Every state is given an explicit background and foreground so the
        /// row stays readable whether it is hovered, selected, or both.
        /// </summary>
        private static Style BuildRowStyle()
        {
            var style = new Style(typeof(ListViewItem));

            style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
            style.Setters.Add(new Setter(Control.ForegroundProperty, UiTheme.Current.TextPrimary));
            style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(6, 4, 6, 4)));
            style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));

            var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Control.BackgroundProperty, UiTheme.Current.Surface));
            hover.Setters.Add(new Setter(Control.ForegroundProperty, UiTheme.Current.TextPrimary));
            style.Triggers.Add(hover);

            var selected = new Trigger { Property = ListViewItem.IsSelectedProperty, Value = true };
            selected.Setters.Add(new Setter(Control.BackgroundProperty, UiTheme.Current.Accent));
            selected.Setters.Add(new Setter(Control.ForegroundProperty, UiTheme.Current.OnAccent));
            style.Triggers.Add(selected);

            var selectedHover = new MultiTrigger();
            selectedHover.Conditions.Add(new Condition(ListViewItem.IsSelectedProperty, true));
            selectedHover.Conditions.Add(new Condition(IsMouseOverProperty, true));
            selectedHover.Setters.Add(new Setter(Control.BackgroundProperty, UiTheme.Current.AccentHover));
            selectedHover.Setters.Add(new Setter(Control.ForegroundProperty, UiTheme.Current.OnAccent));
            style.Triggers.Add(selectedHover);

            return style;
        }

        private static Style BuildColumnHeaderStyle()
        {
            var style = new Style(typeof(GridViewColumnHeader));

            style.Setters.Add(new Setter(Control.BackgroundProperty, UiTheme.Current.Surface));
            style.Setters.Add(new Setter(Control.ForegroundProperty, UiTheme.Current.TextSecondary));
            style.Setters.Add(new Setter(Control.BorderBrushProperty, UiTheme.Current.CardBorder));
            style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0, 0, 0, 1)));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 6, 8, 6)));
            style.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold));
            style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Left));

            var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Control.BackgroundProperty, Brush(0x2E, 0x34, 0x40)));
            hover.Setters.Add(new Setter(Control.ForegroundProperty, Brush(0xD5, 0xDC, 0xE8)));
            style.Triggers.Add(hover);

            return style;
        }

        private void Accept()
        {
            if (_list.SelectedItem is not TorrentFileRow row) return;
            SelectedIndex = row.Index;
            DialogResult = true;
            Close();
        }
    }
}
