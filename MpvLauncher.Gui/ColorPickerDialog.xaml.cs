using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MpvLauncher.Gui
{
    /// <summary>
    /// Modal colour picker used by the appearance editor.
    ///
    /// The window has a fixed Height rather than SizeToContent, and that is not
    /// a style preference. A transparent WPF window sized to its content
    /// measures itself only after it has already arranged, so it opens with no
    /// height and draws nothing at all - the two attributes cannot be combined,
    /// and transparency is what the rounded card is made of.
    ///
    /// Offers a fixed palette of swatches plus a hex field, which covers
    /// everything the settings page can set without pulling in a colour-picker
    /// dependency. It is shown with ShowDialog and the chosen value is read
    /// back from <see cref="ResultColor"/>; cancelling leaves that untouched.
    /// </summary>
    public partial class ColorPickerDialog : Window
    {
        /// <summary>
        /// Guards the hex field against reacting to its own updates. Without it,
        /// normalising what the user typed would immediately rewrite the field
        /// under the caret.
        /// </summary>
        private bool _isUpdating = true;

        /// <summary>The chosen colour as "#RRGGBB", or the default if cancelled.</summary>
        public string ResultColor { get; private set; } = "#6366F1";

        /// <summary>
        /// The preset palette: pinks and reds, the indigo/violet/blue accents,
        /// cyan/green/amber, the neutral backgrounds, and the text colours.
        /// </summary>
        private static readonly string[] Swatches = new[]
        {
            "#F472B6", "#EC4899", "#FB7185", "#F43F5E",
            "#6366F1", "#8B5CF6", "#A855F7", "#3B82F6",
            "#06B6D4", "#10B981", "#F59E0B", "#EF4444",
            "#12141A", "#1A1E27", "#212634", "#2A1824",
            "#2E3648", "#0D0F18", "#F0F2F5", "#9AA3B2"
        };

        public ColorPickerDialog(string? initialColor = null)
        {
            _isUpdating = true;
            InitializeComponent();
            PopulateSwatches();
            SetInitialColor(initialColor ?? "#6366F1");
            _isUpdating = false;
            SyncColor();
        }

        /**
         * Fills the swatch panel, one button per preset.
         *
         * The hex value travels on the button's Tag so the click handler does not
         * have to capture it in a closure per button.
         */
        private void PopulateSwatches()
        {
            if (PanelSwatches == null) return;
            PanelSwatches.Children.Clear();
            foreach (var hex in Swatches)
            {
                var btn = new Button
                {
                    Width = 22,
                    Height = 22,
                    Margin = new Thickness(0, 0, 6, 6),
                    Background = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!,
                    BorderBrush = Brushes.WhiteSmoke,
                    BorderThickness = new Thickness(1),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    Tag = hex
                };
                btn.Click += (s, e) =>
                {
                    if (s is Button b && b.Tag is string c)
                    {
                        ParseAndSetColor(c);
                    }
                };
                PanelSwatches.Children.Add(btn);
            }
        }

        private void SetInitialColor(string colorStr)
        {
            ParseAndSetColor(colorStr);
        }

        /**
         * Turns any supported colour notation into slider positions.
         *
         * The stored config uses CSS-style "rgba(r,g,b,a)" values, which
         * ColorConverter does not understand, so those are unpacked by hand.
         * Anything unparseable falls back to the accent colour rather than
         * throwing - the field is free text and the user may still be typing.
         */
        private void ParseAndSetColor(string colorStr)
        {
            try
            {
                colorStr = colorStr.Trim();
                if (colorStr.StartsWith("rgba(", StringComparison.OrdinalIgnoreCase) && colorStr.EndsWith(")", StringComparison.Ordinal))
                {
                    string[] parts = colorStr[5..^1].Split(',');
                    if (parts.Length == 4)
                    {
                        byte r = byte.Parse(parts[0].Trim(), CultureInfo.InvariantCulture);
                        byte g = byte.Parse(parts[1].Trim(), CultureInfo.InvariantCulture);
                        byte b = byte.Parse(parts[2].Trim(), CultureInfo.InvariantCulture);
                        double a = double.Parse(parts[3].Trim(), CultureInfo.InvariantCulture);
                        byte alpha = (byte)Math.Clamp(a * 255, 0, 255);
                        UpdateSliders(alpha, r, g, b);
                        return;
                    }
                }

                var c = (Color)ColorConverter.ConvertFromString(colorStr);
                UpdateSliders(c.A, c.R, c.G, c.B);
            }
            catch
            {
                UpdateSliders(255, 99, 102, 241);
            }
        }

        /**
         * Moves the four channel sliders and the preview.
         *
         * Suppresses the change handlers while doing so, otherwise each slider
         * move would re-serialise the colour and write back into the field the
         * user is still editing.
         */
        private void UpdateSliders(byte a, byte r, byte g, byte b)
        {
            if (SliderA == null || SliderR == null || SliderG == null || SliderB == null) return;

            bool wasUpdating = _isUpdating;
            _isUpdating = true;
            SliderA.Value = a;
            SliderR.Value = r;
            SliderG.Value = g;
            SliderB.Value = b;
            _isUpdating = wasUpdating;

            SyncColor();
        }

        private void Slider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isUpdating) return;
            SyncColor();
        }

        private void SyncColor()
        {
            if (SliderA == null || SliderR == null || SliderG == null || SliderB == null) return;
            if (TxtValA == null || TxtValR == null || TxtValG == null || TxtValB == null) return;
            if (ColorPreviewBorder == null || TxtHexResult == null) return;

            byte a = (byte)SliderA.Value;
            byte r = (byte)SliderR.Value;
            byte g = (byte)SliderG.Value;
            byte b = (byte)SliderB.Value;

            TxtValA.Text = $"{Math.Round(a / 255.0 * 100)}%";
            TxtValR.Text = r.ToString();
            TxtValG.Text = g.ToString();
            TxtValB.Text = b.ToString();

            var color = Color.FromArgb(a, r, g, b);
            ColorPreviewBorder.Background = new SolidColorBrush(color);

            string hex = a == 255 ? $"#{r:X2}{g:X2}{b:X2}" : $"#{a:X2}{r:X2}{g:X2}{b:X2}";
            ResultColor = hex;

            if (!_isUpdating)
            {
                _isUpdating = true;
                TxtHexResult.Text = hex;
                _isUpdating = false;
            }
        }

        private void TxtHexResult_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isUpdating) return;
            if (TxtHexResult == null) return;

            string txt = TxtHexResult.Text.Trim();
            if (txt.Length is 4 or 7 or 9 && txt.StartsWith('#'))
            {
                try
                {
                    var c = (Color)ColorConverter.ConvertFromString(txt);
                    _isUpdating = true;
                    if (SliderA != null) SliderA.Value = c.A;
                    if (SliderR != null) SliderR.Value = c.R;
                    if (SliderG != null) SliderG.Value = c.G;
                    if (SliderB != null) SliderB.Value = c.B;
                    _isUpdating = false;

                    SyncColor();
                }
                catch { }
            }
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
