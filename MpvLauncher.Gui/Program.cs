using System;
using MpvLauncher.Gui.Services;

namespace MpvLauncher.Gui
{
    /// <summary>
    /// Single entry point for the executable. The same binary serves two roles:
    /// the desktop GUI, and the native messaging host the browser extension talks
    /// to. Which one runs is decided before any window is created, because in
    /// host mode there is no UI and the process talks over stdin/stdout.
    /// </summary>
    public static class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            // Both roles need the data folders to exist first: the host writes
            // its log there, and the GUI reads its templates from there.
            AppPaths.EnsureLayout();

            // Host mode is checked first and returns without ever creating a
            // window. See NativeHostService.IsHostMode for how a legitimate
            // caller is recognised.
            if (NativeHostService.IsHostMode(args))
            {
                NativeHostService.Run(args);
                return;
            }

            // Extract only what is missing, so a user-edited file in
            // %APPDATA% is not overwritten on every start.
            ResourceSeeder.ExtractMissing();

            var app = new App();
            app.InitializeComponent();
            app.Run();
        }
    }
}
