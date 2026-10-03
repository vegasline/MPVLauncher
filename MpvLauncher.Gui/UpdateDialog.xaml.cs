using System.Windows;

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