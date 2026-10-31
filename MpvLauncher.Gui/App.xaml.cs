using System;
using System.Windows;

namespace MpvLauncher.Gui
{
    /// <summary>
    /// WPF application object.
    ///
    /// Its only job here is to install the last-chance exception handlers. There
    /// is no startup logic beyond that: single-instance handling and service
    /// construction live in MainWindow so the ordering stays visible in one
    /// place.
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            // Checked before anything else, and before base.OnStartup, because
            // StartupUri would otherwise build the whole window for a process
            // whose only job is to delete a file.
            if (e.Args.Length == 2 &&
                e.Args[0] == Services.UpdateService.PurgeArgument &&
                int.TryParse(e.Args[1], out int parentPid))
            {
                Services.UpdateService.PurgeOldExecutable(parentPid);
                Shutdown(0);
                return;
            }
            // Fires on any thread and cannot be recovered from, so it only
            // reports. The process will terminate straight afterwards.
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            {
                MessageBox.Show(
                    $"Critical error: {args.ExceptionObject}",
                    "MPV Launcher",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            };

            // Fires on the UI thread and can be continued from, which is what
            // keeps a failed click or a bad theme from killing the window.
            DispatcherUnhandledException += (_, args) =>
            {
                MessageBox.Show(
                    $"UI error: {args.Exception.Message}\n\n{args.Exception.StackTrace}",
                    "MPV Launcher",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                args.Handled = true;
            };

            base.OnStartup(e);
        }

        /// <summary>
        /// Brings the existing window forward.
        ///
        /// A second launch of the exe finds the mutex taken and signals instead
        /// of opening another window; the waiting first copy lands here. Only
        /// the window object knows whether it was hidden to the tray, so the
        /// restore happens here rather than in Program.
        /// </summary>
        internal void ActivateMainWindow()
        {
            if (MainWindow is not Window window) return;

            if (window.WindowState == WindowState.Minimized)
                window.WindowState = WindowState.Normal;

            window.Show();

            // Activate() alone loses to the foreground lock when the request
            // came from another process, so topmost is toggled for one frame.
            window.Topmost = true;
            window.Activate();
            window.Topmost = false;
            window.Focus();
        }
    }
}
