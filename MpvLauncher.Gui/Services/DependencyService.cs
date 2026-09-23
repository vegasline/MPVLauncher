using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace MpvLauncher.Gui.Services
{
    public class DependencyService
    {
        private readonly ProcessService _processService;

        public DependencyService(ProcessService processService)
        {
            _processService = processService;
        }

        public string CheckMpvStatus()
        {
            string path = _processService.FindMpvPath();
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
                    string? line = p?.StandardOutput.ReadLine();
                    return line ?? "MPV installed";
                }
                catch { return "MPV installed"; }
            }
            return "MPV not found";
        }

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
                        // "ffmpeg version N-xxxxx ..." → ilk 3 kelime
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
