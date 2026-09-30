using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace MpvLauncher.Gui.Services
{
    /// <summary>
    /// Locates mpv and starts it on something the user typed.
    ///
    /// This is the GUI's own path, as opposed to the browser extension's: the
    /// value came from the URL box, a dropped file or the history list, not from
    /// a web page. That distinction is why local paths and other schemes are
    /// allowed here, while the native host restricts itself to http(s).
    /// </summary>
    public class ProcessService
    {
        private readonly ConfigService _configService;

        public ProcessService(ConfigService configService)
        {
            _configService = configService;
        }

        /// <summary>
        /// The mpv executable to use: the configured path if it still exists,
        /// then a few well-known install locations. Falling back to the bare name
        /// lets the shell resolve it from PATH, which is why a stale setting does
        /// not produce a hard failure.
        /// </summary>
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

        /// <summary>
        /// Launches mpv on a URL or local path typed by the user.
        ///
        /// Arguments are collected as separate items rather than pasted into one
        /// string: the URL may contain quotes, spaces or trailing backslashes, and
        /// a hand-built command line lets any of those turn into an extra mpv
        /// option. ArgumentList performs the platform quoting itself.
        ///
        /// Unlike the extension path this accepts local paths and other schemes,
        /// because the value came from the user rather than from a web page.
        /// </summary>
        public (bool Success, string Message) PlayMedia(string urlOrPath)
        {
            if (string.IsNullOrWhiteSpace(urlOrPath))
                return (false, "Invalid or empty URL.");

            string url = urlOrPath.Trim();
            string mpvBinary = FindMpvPath();
            var cfg = _configService.Config;

            var argsList = new List<string>();
            string fw = cfg.ForceWindow ?? "immediate";
            if (fw.Equals("no", StringComparison.OrdinalIgnoreCase))
                argsList.Add("--force-window=no");
            else if (fw.Equals("yes", StringComparison.OrdinalIgnoreCase))
                argsList.Add("--force-window=yes");
            else
                argsList.Add("--force-window=immediate");

            if (cfg.AlwaysOnTop)
                argsList.Add("--ontop");
            if (!string.IsNullOrWhiteSpace(cfg.Geometry))
                argsList.Add($"--geometry={cfg.Geometry.Trim()}");
            if (!string.IsNullOrWhiteSpace(cfg.MpvProfile))
                argsList.Add($"--profile={cfg.MpvProfile.Trim()}");
            if (!string.IsNullOrWhiteSpace(cfg.ExtraArgs))
                argsList.AddRange(cfg.ExtraArgs.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries));
            argsList.Add(url);

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = mpvBinary,
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(mpvBinary) ?? ""
                };
                foreach (string a in argsList) psi.ArgumentList.Add(a);

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
