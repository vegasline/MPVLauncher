// Original implementation, MIT licensed.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MpvLauncher.Gui.Services;

namespace MpvLauncher.Gui
{
    /// <summary>
    /// One-line text prompt. WPF has no input box, and the alternative -
    /// Microsoft.VisualBasic's InputBox - brings a whole framework reference
    /// and an unthemed window with it. This is the same shell as the other
    /// code-built dialogs: dark title bar, palette from the active theme.
    /// </summary>
    public sealed class PromptDialog : Window
    {
        private readonly TextBox _input;

        public string Value => _input.Text.Trim();

        public PromptDialog(string title, string label, string initialValue = "")
        {
            var palette = UiTheme.Current;

            Title = title;
            Width = 440;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            Foreground = palette.TextPrimary;

            var root = new StackPanel { Margin = new Thickness(22, 18, 22, 18) };

            root.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 12,
                Foreground = palette.TextSecondary,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            });

            _input = new TextBox
            {
                Text = initialValue,
                FontSize = 13,
                Padding = new Thickness(10, 7, 10, 7),
                Background = palette.Input,
                Foreground = palette.TextPrimary,
                BorderBrush = palette.CardBorder,
                CaretBrush = palette.TextPrimary
            };
            root.Children.Add(_input);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 16, 0, 0)
            };

            var cancel = new Button
            {
                Content = "Cancel",
                Style = DialogTheme.Style(this, "SecondaryButton"),
                Padding = new Thickness(18, 8, 18, 8),
                MinWidth = 92,
                Margin = new Thickness(0, 0, 8, 0),
                IsCancel = true
            };
            cancel.Click += (_, _) => { DialogResult = false; };
            buttons.Children.Add(cancel);

            var ok = new Button
            {
                Content = "Save",
                Style = DialogTheme.Style(this, "ModernButton"),
                Padding = new Thickness(18, 8, 18, 8),
                MinWidth = 92,
                IsDefault = true
            };
            ok.Click += (_, _) => { DialogResult = true; };
            buttons.Children.Add(ok);

            root.Children.Add(buttons);
            Content = root;

            Loaded += (_, _) =>
            {
                DialogTheme.Apply(this);
                _input.Focus();
                _input.SelectAll();
            };

            KeyDown += (_, e) => { if (e.Key == Key.Escape) DialogResult = false; };
        }
    }
}
