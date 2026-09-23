using System;
using MpvLauncher.Gui.Services;

namespace MpvLauncher.Gui
{
    /// <summary>
    /// Tek exe giriş noktası: GUI veya native messaging host (--host).
    /// </summary>
    public static class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            AppPaths.EnsureLayout();
            ResourceSeeder.ExtractMissing();

            if (NativeHostService.IsHostMode(args))
            {
                NativeHostService.Run(args);
                return;
            }

            var app = new App();
            app.InitializeComponent();
            app.Run();
        }
    }
}
