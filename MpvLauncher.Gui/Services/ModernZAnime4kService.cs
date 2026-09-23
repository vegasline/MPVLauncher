using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace MpvLauncher.Gui.Services
{
    public class ModernZAnime4kService
    {
        private static readonly HttpClient Http = CreateHttpClient();

        private static HttpClient CreateHttpClient()
        {
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = System.Net.DecompressionMethods.All,
                AllowAutoRedirect = true
            };
            var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("MpvLauncher/1.0 (Windows NT)");
            return client;
        }

        public async Task<InstallResult> InstallAllAsync(IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
        {
            try
            {
                progress?.Report(new DownloadProgress { Stage = "Installing Anime4K & Configs...", Percent = 10 });
                ResourceSeeder.ExtractAnime4k(overwrite: true);

                progress?.Report(new DownloadProgress { Stage = "Downloading ModernZ Theme...", Percent = 30 });
                var modernZResult = await InstallModernZAsync(progress, ct);
                if (!modernZResult.Success)
                {
                    return modernZResult;
                }

                progress?.Report(new DownloadProgress { Stage = "Theme + Anime4K installed successfully.", Percent = 100 });
                return new InstallResult
                {
                    Success = true,
                    Message = "ModernZ theme and Anime4K installed successfully to %APPDATA%\\mpv",
                    InstalledPath = AppPaths.MpvAppDataDir
                };
            }
            catch (Exception ex)
            {
                return new InstallResult
                {
                    Success = false,
                    Message = $"Installation error: {ex.Message}"
                };
            }
        }

        public async Task<InstallResult> InstallModernZAsync(IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
        {
            var files = new (string FileName, string DestDir, string[] Urls)[]
            {
                (
                    "modernz-icons.ttf",
                    AppPaths.MpvFontsDir,
                    new[]
                    {
                        "https://github.com/Samillion/ModernZ/releases/latest/download/modernz-icons.ttf",
                        "https://raw.githubusercontent.com/Samillion/ModernZ/main/modernz-icons.ttf"
                    }
                ),
                (
                    "modernz.conf",
                    AppPaths.MpvScriptOptsDir,
                    new[]
                    {
                        "https://github.com/Samillion/ModernZ/releases/latest/download/modernz.conf",
                        "https://raw.githubusercontent.com/Samillion/ModernZ/main/modernz.conf"
                    }
                ),
                (
                    "modernz.lua",
                    AppPaths.MpvScriptsDir,
                    new[]
                    {
                        "https://github.com/Samillion/ModernZ/releases/latest/download/modernz.lua",
                        "https://raw.githubusercontent.com/Samillion/ModernZ/main/modernz.lua"
                    }
                )
            };

            int index = 0;
            foreach (var (fileName, destDir, urls) in files)
            {
                Directory.CreateDirectory(destDir);
                string targetPath = Path.Combine(destDir, fileName);

                progress?.Report(new DownloadProgress
                {
                    Stage = $"Downloading {fileName}...",
                    Percent = 30 + (index * 20),
                    Detail = targetPath
                });

                bool downloaded = false;
                Exception? lastEx = null;

                foreach (var url in urls)
                {
                    try
                    {
                        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                        if (resp.IsSuccessStatusCode)
                        {
                            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                            await using var fs = File.Create(targetPath);
                            await stream.CopyToAsync(fs, ct);
                            downloaded = true;
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        lastEx = ex;
                    }
                }

                if (!downloaded)
                {
                    return new InstallResult
                    {
                        Success = false,
                        Message = $"Failed to download {fileName}: {lastEx?.Message ?? "Not found"}"
                    };
                }

                index++;
            }

            return new InstallResult
            {
                Success = true,
                Message = "ModernZ files downloaded successfully."
            };
        }
    }
}
