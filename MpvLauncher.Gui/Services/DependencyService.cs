using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace MpvLauncher.Gui.Services
{
    /// <summary>
    /// Reports which external tools are available.
    ///
    /// Every method here answers for the settings page: it locates a binary and
    /// returns a short human-readable status string. None of them installs
    /// anything - that is DownloadInstallService's job - and none of them throw:
    /// a missing tool is a normal state the UI has to render, not an error.
    ///
    /// The returned strings are deliberately not localised, because they are
    /// built from the tool's own output ("mpv v0.41.0", "yt-dlp 2025.x") which
    /// the user needs verbatim.
    /// </summary>
    public class DependencyService
    {
        private readonly ProcessService _processService;

        public DependencyService(ProcessService processService)
        {
            _processService = processService;
        }

        /// <summary>
        /// Asks mpv for its version.
        ///
        /// The distinction between "the file is there" and "the file runs" is
        /// the whole point of this method. An mpv whose static imports cannot be
        /// resolved exists, downloads correctly, and still cannot be started, so
        /// reporting it as installed here is what turns a loader failure into an
        /// unexplained dialog much later, with nothing on screen tying the two
        /// together.
        ///
        /// Not localised on purpose, like every status on this page: the useful
        /// part of each string is the tool's own output, which has to be verbatim
        /// to be checkable against upstream.
        /// </summary>
        public string CheckMpvStatus()
        {
            var probe = _processService.ProbeMpv();

            return probe.Health switch
            {
                MpvHealth.NotInstalled => "MPV not found",
                MpvHealth.Ok => probe.Version,
                _ => probe.IsLoaderProblem
                    // Says what the state is and that it is not the user's mpv
                    // settings, because the natural reaction is to start changing
                    // options that cannot possibly matter here.
                    ? $"MPV cannot start: {probe.Describe()} " +
                      "(unresolved Windows import - update the graphics driver / Vulkan runtime)"
                    : $"MPV cannot start: {probe.Describe()}"
            };
        }

        /// <summary>
        /// Locates yt-dlp: the copy the installer put in the tools folder first,
        /// then one sitting next to mpv (a common manual install layout), and
        /// finally a bare name for the status line to display.
        /// </summary>
        public string FindYtdlPath()
        {
            string bundled = AppPaths.YtDlpExe;
            if (File.Exists(bundled)) return bundled;

            string mpvPath = _processService.FindMpvPath();
            string? dir = Path.GetDirectoryName(mpvPath);
            if (!string.IsNullOrEmpty(dir))
            {
                string cand = Path.Combine(dir, "yt-dlp.exe");
                if (File.Exists(cand)) return cand;
            }
            return "yt-dlp.exe";
        }

        /// <summary>Same lookup order as <see cref="FindYtdlPath"/>, for ffmpeg.</summary>
        public string FindFfmpegPath()
        {
            string bundled = AppPaths.FfmpegExe;
            if (File.Exists(bundled)) return bundled;

            string mpvPath = _processService.FindMpvPath();
            string? dir = Path.GetDirectoryName(mpvPath);
            if (!string.IsNullOrEmpty(dir))
            {
                string cand = Path.Combine(dir, "ffmpeg.exe");
                if (File.Exists(cand)) return cand;
            }
            return "ffmpeg.exe";
        }

        /// <summary>Runs "yt-dlp --version" and returns its first line.</summary>
        public string CheckYtdlStatus()
        {
            string path = FindYtdlPath();
            if (File.Exists(path))
            {
                try
                {
                    var p = Process.Start(new ProcessStartInfo
                    {
                        FileName = path,
                        Arguments = "--version",
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });
                    string? ver = p?.StandardOutput.ReadLine();
                    return ver ?? "yt-dlp installed";
                }
                catch { return "yt-dlp installed"; }
            }
            return "yt-dlp not found";
        }

        /// <summary>
        /// Runs "ffmpeg -version". That prints its banner on stderr, so the
        /// first line of stdout is often empty - hence the fallback strings.
        /// </summary>
        public string CheckFfmpegStatus()
        {
            string path = FindFfmpegPath();
            if (File.Exists(path))
            {
                try
                {
                    var p = Process.Start(new ProcessStartInfo
                    {
                        FileName = path,
                        Arguments = "-version",
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });
                    string? line = p?.StandardOutput.ReadLine();
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        // "ffmpeg version N-xxxxx ..." -> keep the first three words
                        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 3)
                            return $"{parts[0]} {parts[1]} {parts[2]}";
                        return line.Length > 60 ? line[..60] + "…" : line;
                    }
                    return "FFmpeg installed";
                }
                catch { return "FFmpeg installed"; }
            }
            return "FFmpeg not found";
        }

        /// <summary>
        /// Runs "yt-dlp -U", which makes it replace itself in place.
        ///
        /// Wrapped in Task.Run because WaitForExit blocks, and given a 30 second
        /// budget so a stalled download cannot freeze the UI thread. The tool
        /// writes to both streams, so both are captured for the status line.
        /// </summary>
        public async Task<(bool Success, string Output)> UpdateYtdlAsync()
        {
            string path = FindYtdlPath();
            if (!File.Exists(path))
                return (false, "yt-dlp.exe not found. Download it first.");

            try
            {
                return await Task.Run(() =>
                {
                    var p = Process.Start(new ProcessStartInfo
                    {
                        FileName = path,
                        Arguments = "-U",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });
                    p?.WaitForExit(30000);
                    string outText = p?.StandardOutput.ReadToEnd() ?? "";
                    return (true, string.IsNullOrWhiteSpace(outText) ? "Update finished." : outText);
                });
            }
            catch (Exception ex)
            {
                return (false, "Update failed: " + ex.Message);
            }
        }
    }
}
