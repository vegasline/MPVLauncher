using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Archives;
using SharpCompress.Common;

namespace MpvLauncher.Gui.Services
{
    public enum ToolKind
    {
        Mpv,
        YtDlp,
        Ffmpeg
    }

    public sealed class DownloadProgress
    {
        public string Stage { get; init; } = "";
        public double? Percent { get; init; }
        public string? Detail { get; init; }
    }

    public sealed class InstallResult
    {
        public bool Success { get; init; }
        public string Message { get; init; } = "";
        public string? InstalledPath { get; init; }
    }

    /// <summary>
    /// Downloads MPV / yt-dlp / FFmpeg into %APPDATA%\MPVLauncher\bin.
    /// </summary>
    public class DownloadInstallService
    {
        public static string InstallRoot => AppPaths.Root;
        public static string BinDir => AppPaths.BinDir;
        public static string CacheDir => AppPaths.CacheDir;

        private const string MpvRepo = "shinchiro/mpv-winbuild-cmake";
        private const string MpvAssetPrefix = "mpv-x86_64-v3";
        private const string YtDlpRepo = "yt-dlp/yt-dlp";
        private const string YtDlpAssetName = "yt-dlp.exe";
        private const string FfmpegRepo = "GyanD/codexffmpeg";

        private static readonly HttpClient Http = CreateHttpClient();
        private readonly ConfigService _configService;

        public DownloadInstallService(ConfigService configService)
        {
            _configService = configService;
            AppPaths.EnsureLayout();
        }

        private static HttpClient CreateHttpClient()
        {
            var handler = new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                MaxConnectionsPerServer = 8,
                EnableMultipleHttp2Connections = true
            };
            var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(30) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("MPVLauncher/1.0");
            return client;
        }

        public string GetExpectedExePath(ToolKind kind) => kind switch
        {
            ToolKind.Mpv => AppPaths.MpvExe,
            ToolKind.YtDlp => AppPaths.YtDlpExe,
            ToolKind.Ffmpeg => AppPaths.FfmpegExe,
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

        public async Task<InstallResult> InstallAsync(
            ToolKind kind,
            IProgress<DownloadProgress>? progress = null,
            CancellationToken ct = default)
        {
            try
            {
                progress?.Report(new DownloadProgress { Stage = "Resolving source...", Percent = 0 });

                var (url, fileName) = await ResolveDownloadAsync(kind, ct);

                // yt-dlp doğrudan exe olarak indiriliyor — zip açmaya gerek yok
                if (kind == ToolKind.YtDlp)
                {
                    Directory.CreateDirectory(BinDir);
                    string destExe = AppPaths.YtDlpExe;
                    progress?.Report(new DownloadProgress { Stage = "Downloading yt-dlp.exe...", Percent = 0, Detail = fileName });
                    await DownloadFileAsync(url, destExe, progress, ct);
                    progress?.Report(new DownloadProgress { Stage = "Done", Percent = 100, Detail = destExe });
                    return new InstallResult { Success = true, Message = $"yt-dlp installed: {destExe}", InstalledPath = destExe };
                }

                string archivePath = Path.Combine(CacheDir, fileName);

                progress?.Report(new DownloadProgress
                {
                    Stage = "Downloading...",
                    Percent = 0,
                    Detail = fileName
                });

                await DownloadFileAsync(url, archivePath, progress, ct);

                progress?.Report(new DownloadProgress { Stage = "Extracting...", Percent = 90, Detail = fileName });

                string installed = await Task.Run(() => ExtractAndPlace(kind, archivePath), ct);

                if (kind == ToolKind.Mpv)
                {
                    _configService.Config.MpvPath = installed;
                    _configService.Save();
                }

                TryDelete(archivePath);

                progress?.Report(new DownloadProgress { Stage = "Done", Percent = 100, Detail = installed });
                return new InstallResult
                {
                    Success = true,
                    Message = $"{kind} installed: {installed}",
                    InstalledPath = installed
                };
            }
            catch (OperationCanceledException)
            {
                return new InstallResult { Success = false, Message = "Download cancelled." };
            }
            catch (Exception ex)
            {
                return new InstallResult { Success = false, Message = "Install failed: " + ex.Message };
            }
        }

        private async Task<(string Url, string FileName)> ResolveDownloadAsync(ToolKind kind, CancellationToken ct)
        {
            switch (kind)
            {
                case ToolKind.Mpv:
                {
                    var asset = await FindGitHubAssetAsync(MpvRepo, name =>
                        name.StartsWith(MpvAssetPrefix, StringComparison.OrdinalIgnoreCase), ct);
                    return (asset.Url, asset.Name);
                }
                case ToolKind.YtDlp:
                {
                    var asset = await FindGitHubAssetAsync(YtDlpRepo, name =>
                        name.Equals(YtDlpAssetName, StringComparison.OrdinalIgnoreCase), ct);
                    return (asset.Url, asset.Name);
                    // Doğrudan exe indiriliyor, zip açmaya gerek yok
                }
                case ToolKind.Ffmpeg:
                {
                    try
                    {
                        var zip = await FindGitHubAssetAsync(FfmpegRepo, name =>
                            name.Contains("essentials_build", StringComparison.OrdinalIgnoreCase)
                            && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase), ct);
                        return (zip.Url, zip.Name);
                    }
                    catch
                    {
                        var seven = await FindGitHubAssetAsync(FfmpegRepo, name =>
                            name.Contains("essentials_build", StringComparison.OrdinalIgnoreCase)
                            && name.EndsWith(".7z", StringComparison.OrdinalIgnoreCase), ct);
                        return (seven.Url, seven.Name);
                    }
                }
                default:
                    throw new ArgumentOutOfRangeException(nameof(kind));
            }
        }

        private async Task<(string Name, string Url)> FindGitHubAssetAsync(
            string repo,
            Func<string, bool> predicate,
            CancellationToken ct)
        {
            string apiUrl = $"https://api.github.com/repos/{repo}/releases/latest";
            using var req = new HttpRequestMessage(HttpMethod.Get, apiUrl);
            req.Headers.Accept.ParseAdd("application/vnd.github+json");

            using var resp = await Http.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            if (!doc.RootElement.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException($"{repo}: release asset list is empty.");

            foreach (var asset in assets.EnumerateArray())
            {
                string name = asset.GetProperty("name").GetString() ?? "";
                if (!predicate(name)) continue;

                string url = asset.GetProperty("browser_download_url").GetString()
                    ?? throw new InvalidOperationException($"{name}: download URL missing.");
                return (name, url);
            }

            throw new InvalidOperationException($"{repo}: matching asset not found.");
        }

        private static async Task DownloadFileAsync(
            string url,
            string destination,
            IProgress<DownloadProgress>? progress,
            CancellationToken ct)
        {
            using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();

            long? total = resp.Content.Headers.ContentLength;
            await using var input = await resp.Content.ReadAsStreamAsync(ct);
            await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 512 * 1024, true);

            var buffer = new byte[512 * 1024];
            long readTotal = 0;
            int read;
            int lastReported = -1;

            while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
                readTotal += read;

                if (total is > 0)
                {
                    int pct = (int)(readTotal * 85.0 / total.Value);
                    if (pct != lastReported)
                    {
                        lastReported = pct;
                        progress?.Report(new DownloadProgress
                        {
                            Stage = "Downloading...",
                            Percent = pct,
                            Detail = FormatBytes(readTotal) + (total.HasValue ? $" / {FormatBytes(total.Value)}" : "")
                        });
                    }
                }
                else if (readTotal % (2 * 1024 * 1024) < buffer.Length)
                {
                    progress?.Report(new DownloadProgress
                    {
                        Stage = "Downloading...",
                        Percent = null,
                        Detail = FormatBytes(readTotal)
                    });
                }
            }
        }

        private string ExtractAndPlace(ToolKind kind, string archivePath)
        {
            string extractDir = Path.Combine(CacheDir, "extract_" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(extractDir);

            try
            {
                ExtractArchive(archivePath, extractDir);

                return kind switch
                {
                    ToolKind.Mpv => PlaceTree(extractDir, "mpv.exe"),
                    ToolKind.YtDlp => PlaceTree(extractDir, "yt-dlp.exe"),
                    ToolKind.Ffmpeg => PlaceFfmpeg(extractDir),
                    _ => throw new ArgumentOutOfRangeException(nameof(kind))
                };
            }
            finally
            {
                TryDeleteDir(extractDir);
            }
        }

        private static void ExtractArchive(string archivePath, string extractDir)
        {
            ArchiveFactory.WriteToDirectory(archivePath, extractDir, new ExtractionOptions
            {
                ExtractFullPath = true,
                Overwrite = true
            });
        }

        private static string PlaceTree(string extractDir, string exeName)
        {
            string? found = Directory.EnumerateFiles(extractDir, exeName, SearchOption.AllDirectories)
                .FirstOrDefault();

            if (found == null)
                throw new FileNotFoundException($"{exeName} was not found in the archive.");

            string srcDir = Path.GetDirectoryName(found)!;
            Directory.CreateDirectory(BinDir);
            CopyDirectoryContents(srcDir, BinDir);
            return Path.Combine(BinDir, exeName);
        }

        private static void CopyDirectoryContents(string sourceDir, string destDir)
        {
            Directory.CreateDirectory(destDir);
            foreach (string file in Directory.EnumerateFiles(sourceDir))
            {
                File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)), overwrite: true);
            }

            foreach (string dir in Directory.EnumerateDirectories(sourceDir))
            {
                string name = Path.GetFileName(dir);
                CopyDirectoryContents(dir, Path.Combine(destDir, name));
            }
        }

        private static string PlaceFfmpeg(string extractDir)
        {
            string? ffmpeg = Directory.EnumerateFiles(extractDir, "ffmpeg.exe", SearchOption.AllDirectories)
                .FirstOrDefault();
            if (ffmpeg == null)
                throw new FileNotFoundException("ffmpeg.exe was not found in the archive.");

            string srcBin = Path.GetDirectoryName(ffmpeg)!;
            Directory.CreateDirectory(BinDir);

            foreach (string name in new[] { "ffmpeg.exe", "ffprobe.exe", "ffplay.exe" })
            {
                string src = Path.Combine(srcBin, name);
                if (File.Exists(src))
                    File.Copy(src, Path.Combine(BinDir, name), overwrite: true);
            }

            return Path.Combine(BinDir, "ffmpeg.exe");
        }

        private static string FormatBytes(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB" };
            double v = bytes;
            int i = 0;
            while (v >= 1024 && i < units.Length - 1)
            {
                v /= 1024;
                i++;
            }
            return $"{v:0.##} {units[i]}";
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        private static void TryDeleteDir(string path)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
        }
    }
}
