using System;
using System.Windows;
using System.Windows.Controls;

namespace MpvLauncher.Gui
{
    /// <summary>
    /// Modal notice shown at launch when a newer build has been downloaded and
    /// is waiting to be installed.
    ///
    /// A system MessageBox would have done the job, but it does not carry the
    /// application's own chrome, so a user who launched the app from a dark
    /// themed window would be interrupted by an unrelated-looking box. The text
    /// is filled in by the window rather than translated here, because the
    /// strings live in the same locale files as everything else.
    ///
    /// Read <see cref="InstallNow"/> to find out what was chosen. Shown with
    /// ShowDialog; "Later" closes it without installing.
    /// </summary>
    public partial class UpdateDialog : Window
    {
        /// <summary>
        /// Whether the user asked for the update to be installed now, rather
        /// than dismissing the notice. Defaults to false so a closed window is
        /// never read as consent.
        /// </summary>
        public bool InstallNow { get; private set; }

        /// <summary>
        /// Builds the window and loads its XAML.
        ///
        /// Written out rather than left to a generated constructor. The compiler
        /// emits the other half of this partial class, but only ColorPickerDialog
        /// calls InitializeComponent itself - a class with no declared constructor
        /// gets one that does not, so the controls named in the XAML are never
        /// assigned. That failure is invisible: the window constructs, every field
        /// is simply null, and the first line that touches one throws a
        /// NullReferenceException with nothing pointing at the XAML.
        /// </summary>
        public UpdateDialog()
        {
            InitializeComponent();
        }

        /// <summary>Sets the notice text.</summary>
        /// <param name="heading">Short title line.</param>
        /// <param name="body">Explanation, normally carrying the version number.</param>
        /// <param name="installButton">Label for the installing button.</param>
        /// <param name="laterButton">Label for the dismissing button.</param>
        public void SetText(string heading, string body, string installButton, string laterButton)
        {
            TxtHeading.Text = heading;
            TxtBody.Text = body;
            BtnInstall.Content = installButton;
            BtnLater.Content = laterButton;

            FitHeightToContent();
        }

        /// <summary>
        /// Sizes the window to what the text actually needs.
        ///
        /// A fixed height cannot work here: it has to be tall enough for the
        /// longest of twelve translations, which leaves the short ones sitting in
        /// a mostly empty box, and no single number is right for all of them.
        /// SizeToContent would do this on its own, but it cannot be combined with
        /// AllowsTransparency - which is what the rounded card is made of - so the
        /// content is measured here instead and the height set from that.
        ///
        /// Measured before the window is shown, against the width the dialog will
        /// actually have, because the body wraps and the number of lines it needs
        /// depends on it.
        ///
        /// The measured element is the window's content, whose grid already
        /// carries its own 22px margin, so its desired height is the finished
        /// height and nothing is added to it. Adding the margin again - which is
        /// what an earlier version did - leaves every language sitting above a
        /// band of empty space, and a check that repeated the same arithmetic
        /// reports a perfect fit.
        /// </summary>
        private void FitHeightToContent()
        {
            try
            {
                var root = (FrameworkElement)Content;

                root.Measure(new Size(Width, double.PositiveInfinity));
                double needed = root.DesiredSize.Height;

                // Never shorter than the content needs and never taller than the
                // text is worth: a floor stops a one-word body collapsing the
                // window, and a ceiling stops a long one stretching it down the
                // screen.
                Height = Math.Max(180, Math.Min(needed, 520));
            }
            catch (Exception ex)
            {
                // Leave the XAML height in place rather than risk a window that
                // cannot be measured being given a size that hides it.
                System.Diagnostics.Debug.WriteLine($"update dialog size: {ex.Message}");
            }
        }

        private void BtnInstall_Click(object sender, RoutedEventArgs e)
        {
            InstallNow = true;
            DialogResult = true;
            Close();
        }

        private void BtnLater_Click(object sender, RoutedEventArgs e)
        {
            // Explicitly false rather than left at its default: the two paths
            // should not be distinguishable by omission.
            InstallNow = false;
            DialogResult = false;
            Close();
        }
    }
}