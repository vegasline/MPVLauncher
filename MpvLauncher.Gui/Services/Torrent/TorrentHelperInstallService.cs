// Original implementation, MIT licensed.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MpvLauncher.Gui.Services
{
    public sealed record TorrentHelperInstallResult(
        bool Success,
        string Message,
        string Version = "",
        string Sha256 = "",
        string? Path = null);

    /// <summary>
    /// Downloads the optional mpvtorrent helper from this project's GitHub
    /// releases.
    ///
    /// Two rules make this safe enough to run without elevation:
    ///   - the executable is only moved into place after its SHA256 matches the
    ///     published .sha256 asset; a mismatch deletes the temporary file;
    ///   - the download is capped, written next to the destination and moved
    ///     atomically, so a dropped connection never leaves a half binary that
    ///     looks installed.
    /// The release is looked up by tag prefix `mpvtorrent-v`, which keeps the
    /// helper's release cadence independent of the launcher's.
    /// </summary>
    public sealed class TorrentHelperInstallService
    {
        public const string Repository = "vegasline/MPVLauncher";
        public const string TagPrefix = "mpvtorrent-v";
        public const string AssetName = "mpvtorrent.exe";
        public const string ChecksumAssetName = "mpvtorrent.exe.sha256";
        private const long MaxDownloadBytes = 160L * 1024 * 1024;

        private static readonly HttpClient Http = CreateHttpClient();
        private readonly ConfigService _configService;

        public TorrentHelperInstallService(ConfigService configService) => _configService = configService;

        public static string HelperPath => AppPaths.TorrentHelperExe;
        public static bool IsInstalled => File.Exists(HelperPath);

        public static HttpClient CreateHttpClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("MPVLauncher/1.0");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return client;
        }

        public async Task<TorrentHelperInstallResult> InstallAsync(
            IProgress<DownloadProgress>? progress = null,
            CancellationToken ct = default)
        {
            try
            {
                progress?.Report(new DownloadProgress { Stage = "Looking up the helper release..." });
                var release = await FindReleaseAsync(ct);
                if (release is null)
                    return new TorrentHelperInstallResult(false, "No mpvtorrent release found on GitHub.");

                if (IsCurrent(release, out string currentVersion))
                    return new TorrentHelperInstallResult(true, $"mpvtorrent {currentVersion} is already installed.", currentVersion);

                string expected = await DownloadChecksumAsync(release.ChecksumUrl, ct);
                if (expected.Length != 64)
                    return new TorrentHelperInstallResult(false, "The release checksum file is missing or malformed; refusing to install.");

                Directory.CreateDirectory(Path.GetDirectoryName(HelperPath)!);
                string temp = HelperPath + ".download";
                TryDelete(temp);

                progress?.Report(new DownloadProgress { Stage = "Downloading mpvtorrent...", Detail = release.Version });
                await DownloadFileAsync(release.DownloadUrl, temp, progress, ct);

                progress?.Report(new DownloadProgress { Stage = "Verifying SHA256..." });
                string actual = ComputeSha256(temp);
                if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(temp);
                    return new TorrentHelperInstallResult(false,
                        "SHA256 mismatch: the downloaded helper was deleted and will not be executed.");
                }

                File.Move(temp, HelperPath, overwrite: true);
                _configService.Config.TorrentHelperVersion = release.Version;
                _configService.Config.TorrentHelperSha256 = actual;
                _configService.Save();

                progress?.Report(new DownloadProgress { Stage = "Done", Percent = 100, Detail = release.Version });
                return new TorrentHelperInstallResult(true,
                    $"mpvtorrent {release.Version} installed.", release.Version, actual, HelperPath);
            }
            catch (OperationCanceledException)
            {
                return new TorrentHelperInstallResult(false, "Installation cancelled.");
            }
            catch (Exception ex)
            {
                return new TorrentHelperInstallResult(false, "Could not install mpvtorrent: " + ex.Message);
            }
        }

        /// <summary>
        /// True when the installed binary is exactly the configured version and
        /// hash. A missing stored hash counts as stale: the first installation
        /// after this feature shipped has no hash yet.
        /// </summary>
        public bool IsCurrent(ReleaseAsset release, out string version)
        {
            version = _configService.Config.TorrentHelperVersion;
            if (!IsInstalled || string.IsNullOrWhiteSpace(version)) return false;
            if (!string.Equals(version, release.Version, StringComparison.OrdinalIgnoreCase)) return false;
            if (string.IsNullOrWhiteSpace(_configService.Config.TorrentHelperSha256)) return false;
            return string.Equals(
                ComputeSha256(HelperPath),
                _configService.Config.TorrentHelperSha256,
                StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The launcher refuses to start the helper if the file no longer
        /// matches the hash recorded at install time. This catches a replaced
        /// binary in %APPDATA%, not just a bad download.
        /// </summary>
        public static bool VerifyInstalledHash(string expectedSha256)
        {
            if (string.IsNullOrWhiteSpace(expectedSha256)) return false;
            if (!File.Exists(HelperPath)) return false;
            try
            {
                return string.Equals(ComputeSha256(HelperPath), expectedSha256, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        public static string ComputeSha256(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
            using var sha = SHA256.Create();
            byte[] hash = sha.ComputeHash(stream);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        private async Task<ReleaseAsset?> FindReleaseAsync(CancellationToken ct)
        {
            string url = $"https://api.github.com/repos/{Repository}/releases?per_page=50";
            using var response = await Http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"GitHub returned HTTP {(int)response.StatusCode}.");

            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                string tag = item.TryGetProperty("tag_name", out var tagValue) ? tagValue.GetString() ?? "" : "";
                if (!tag.StartsWith(TagPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                if (item.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) continue;
                if (item.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True) continue;
                if (!item.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) continue;

                string? exeUrl = null;
                string? sumUrl = null;
                long size = 0;
                foreach (var asset in assets.EnumerateArray())
                {
                    string name = asset.TryGetProperty("name", out var nameValue) ? nameValue.GetString() ?? "" : "";
                    string download = asset.TryGetProperty("browser_download_url", out var dl) ? dl.GetString() ?? "" : "";
                    if (!Uri.TryCreate(download, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) continue;
                    if (name.Equals(AssetName, StringComparison.OrdinalIgnoreCase))
                    {
                        exeUrl = download;
                        if (asset.TryGetProperty("size", out var sizeValue) && sizeValue.TryGetInt64(out long s)) size = s;
                    }
                    else if (name.Equals(ChecksumAssetName, StringComparison.OrdinalIgnoreCase))
                    {
                        sumUrl = download;
                    }
                }

                if (exeUrl is null || sumUrl is null) continue;
                if (size <= 0 || size > MaxDownloadBytes) continue;

                return new ReleaseAsset(tag[TagPrefix.Length..], exeUrl, sumUrl, size);
            }
            return null;
        }

        private static async Task<string> DownloadChecksumAsync(string url, CancellationToken ct)
        {
            string text = await Http.GetStringAsync(url, ct);
            string token = text.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            token = token.Trim().ToLowerInvariant();
            return token.Length == 64 && token.All(c => Uri.IsHexDigit(c)) ? token : "";
        }

        private static async Task DownloadFileAsync(
            string url,
            string destination,
            IProgress<DownloadProgress>? progress,
            CancellationToken ct)
        {
            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"Download returned HTTP {(int)response.StatusCode}.");
            if (response.Content.Headers.ContentLength is long declared && declared > MaxDownloadBytes)
                throw new InvalidOperationException("The helper download is larger than the allowed size.");

            await using var input = await response.Content.ReadAsStreamAsync(ct);
            await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, true);

            byte[] buffer = new byte[1024 * 1024];
            long total = 0;
            long? expected = response.Content.Headers.ContentLength;
            int read;
            while ((read = await input.ReadAsync(buffer, ct)) > 0)
            {
                total += read;
                if (total > MaxDownloadBytes)
                    throw new InvalidOperationException("The helper download is larger than the allowed size.");
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
                if (expected is long length && length > 0)
                {
                    double percent = Math.Min(100, total * 100.0 / length);
                    progress?.Report(new DownloadProgress
                    {
                        Stage = "Downloading mpvtorrent...",
                        Percent = percent,
                        Detail = string.Format(CultureInfo.InvariantCulture, "{0:0.0} / {1:0.0} MiB", total / 1048576.0, length / 1048576.0)
                    });
                }
            }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
        }

        public sealed record ReleaseAsset(string Version, string DownloadUrl, string ChecksumUrl, long Size);
    }
}
