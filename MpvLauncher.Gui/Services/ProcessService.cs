using System;
using System.Diagnostics;
using System.IO;

namespace MpvLauncher.Gui.Services
{
    public class ProcessService
    {
        private readonly ConfigService _configService;

        public ProcessService(ConfigService configService)
        {
            _configService = configService;
        }

        public string FindMpvPath()
        {
            string saved = _configService.Config.MpvPath;
            if (File.Exists(saved)) return saved;

            string[] common = new[]
            {
                AppPaths.MpvExe,
                @"D:\Prog\mpv\mpv.exe",
                @"C:\Program Files\mpv\mpv.exe",
                @"C:\Program Files (x86)\mpv\mpv.exe"
            };

            foreach (var p in common)
            {
                if (File.Exists(p))
                {
                    _configService.Config.MpvPath = p;
                    _configService.Save();
                    return p;
                }
            }

            return "mpv.exe";
        }

        public (bool Success, string Message) PlayMedia(string urlOrPath)
        {
            if (string.IsNullOrWhiteSpace(urlOrPath))
                return (false, "Invalid or empty URL.");

            string url = urlOrPath.Trim();
            string mpvBinary = FindMpvPath();
            string arguments = $"--force-window=yes --geometry=960x540 \"{url}\"";

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = mpvBinary,
                    Arguments = arguments,
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(mpvBinary) ?? ""
                };

                Process.Start(psi);
                _configService.AddHistory(url);
                return (true, "MPV started.");
            }
            catch (Exception ex)
            {
                return (false, "Launch failed: " + ex.Message);
            }
        }
    }
}
