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
    }
}
