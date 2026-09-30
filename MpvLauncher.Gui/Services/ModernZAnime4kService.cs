using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace MpvLauncher.Gui.Services
{
    /// <summary>
    /// Installs the ModernZ mpv theme and the Anime4K shader set.
    ///
    /// Anime4K comes from resources embedded in the executable; the ModernZ
    /// theme files are downloaded from its GitHub releases, with a raw-file URL
    /// as the second choice when the release asset is not published.
    ///
    /// One of those three files is modernz.lua, which mpv executes on startup.
    /// That is why the download is validated - https only, GitHub hosts only
    /// after any redirect, and size-capped - before a byte reaches the disk.
    /// </summary>
    public class ModernZAnime4kService
    {
        private static readonly HttpClient Http = CreateHttpClient();

        private static HttpClient CreateHttpClient()
        {
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = System.Net.DecompressionMethods.All,

                // Redirects are followed because releases/latest/download/ needs
                // them, but the hop is validated afterwards so a redirect cannot
                // move the download somewhere unexpected.
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 5
            };
            var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("MpvLauncher/1.0 (Windows NT)");
            return client;
        }

        /// <summary>
        /// Hosts the theme files may come from. modernz.lua is executed by mpv,
        /// so a redirect to an untrusted origin would be remote code execution.
        /// </summary>
        private static readonly string[] AllowedDownloadHosts =
        {
            "github.com",
            "raw.githubusercontent.com",
            "objects.githubusercontent.com",
            "codeload.github.com",
            "release-assets.githubusercontent.com"
        };

        /// <summary>
        /// Upper bound for a theme file. These are a font, a config and a script;
        /// a few megabytes is already far more than any of them needs.
        /// </summary>
        private const long MaxAssetBytes = 32L * 1024 * 1024;

        /// <summary>
        /// Rejects anything that is not an https download from a GitHub host.
        /// The download is only ever written to disk after this passes, and one
        /// of the three files is a Lua script that mpv loads on startup.
        /// </summary>
        private static void AssertTrustedSource(string requestUrl, Uri? finalUri)
        {
            if (!Uri.TryCreate(requestUrl, UriKind.Absolute, out var requested))
                throw new InvalidOperationException($"Malformed download URL: {requestUrl}");

            if (requested.Scheme != Uri.UriSchemeHttps)
                throw new InvalidOperationException($"Refusing non-https download: {requested.Scheme}");

            Uri effective = finalUri ?? requested;
            bool trusted = AllowedDownloadHosts
                .Any(h => effective.Host.Equals(h, StringComparison.OrdinalIgnoreCase));
            if (!trusted)
                throw new InvalidOperationException($"Untrusted download host: {effective.Host}");
        }

        /// <summary>
        /// Installs Anime4K from the embedded resources and then the theme.
        ///
        /// The embedded shaders go in first because they always succeed, which
        /// means a theme download failure still leaves a working shader set.
        /// </summary>
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

        /// <summary>
        /// Downloads the three theme files into the folders mpv reads.
        ///
        /// Each file has two candidate URLs and the first that answers is used;
        /// a failure on one is remembered but does not stop the next file from
        /// being tried.
        /// </summary>
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
                            // Validate the source (including any redirect hop)
                            // before a single byte is written.
                            AssertTrustedSource(url, resp.RequestMessage?.RequestUri);

                            if (resp.Content.Headers.ContentLength is > MaxAssetBytes)
                                throw new InvalidOperationException($"{fileName} is larger than the size limit.");

                            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                            await using var fs = File.Create(targetPath);
                            await CopyBoundedAsync(stream, fs, fileName, ct);

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

        /// <summary>
        /// Copies a response body while enforcing the size limit on what actually
        /// arrives, since Content-Length may be absent or understated.
        /// </summary>
        private static async Task CopyBoundedAsync(Stream input, Stream output, string fileName, CancellationToken ct)
        {
            var buffer = new byte[64 * 1024];
            long total = 0;
            int read;
            while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
            {
                total += read;
                if (total > MaxAssetBytes)
                    throw new InvalidOperationException($"{fileName} exceeded the size limit.");
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
            }
        }
    }
}
