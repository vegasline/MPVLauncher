using System;
using System.Windows;

namespace MpvLauncher.Gui
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            {
                MessageBox.Show($"Critical error: {args.ExceptionObject}", "MPV Launcher", MessageBoxButton.OK, MessageBoxImage.Error);
            };

            DispatcherUnhandledException += (_, args) =>
            {
                MessageBox.Show($"UI error: {args.Exception.Message}\n\n{args.Exception.StackTrace}", "MPV Launcher", MessageBoxButton.OK, MessageBoxImage.Error);
                args.Handled = true;
            };

            base.OnStartup(e);
        }
    }
}
