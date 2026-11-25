// Original implementation, MIT licensed.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MpvLauncher.Gui.Services
{
    /// <summary>
    /// The optional browser relay helper (mpvrelay.exe).
    ///
    /// It is downloaded on demand from the same GitHub release feed as the app,
    /// verified against the published SHA-256 and placed next to the launcher.
    /// Nothing is ever shipped inside the installer: the relay starts a browser,
    /// so it stays an explicit opt-in and the app keeps working without it.
    /// </summary>
    public sealed class RelayInstallService
    {
        /// <summary>GitHub repository that publishes the helper.</summary>
        public const string Repository = "vegasline/MPVLauncher";

        /// <summary>Release tag prefix, e.g. <c>mpvrelay-v1.0.0</c>.</summary>
        public const string TagPrefix = "mpvrelay-v";

        /// <summary>Asset name of the helper itself.</summary>
        public const string AssetName = "mpvrelay.exe";

        /// <summary>Asset name of the published checksum.</summary>
        public const string ChecksumAssetName = "mpvrelay.exe.sha256";

        private const long MaxDownloadBytes = 200L * 1024 * 1024;

        private static readonly HttpClient Http = CreateHttpClient();

        private readonly ConfigService _config;

        public RelayInstallService(ConfigService config)
        {
            _config = config;
        }

        /// <summary>Where the helper lives once installed.</summary>
        public static string HelperPath => AppPaths.RelayHelperExe;

        /// <summary>True when a helper file is present (it is not verified here).</summary>
        public static bool IsInstalled => File.Exists(HelperPath);

        public static HttpClient CreateHttpClient()
        {
            var client = new HttpClient
            {
                Timeout = TimeSpan.FromMinutes(20)
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("MPVLauncher/1.0");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return client;
        }

        /// <summary>Installed helper version recorded by the last successful install.</summary>
        public string InstalledVersion => _config.Config.RelayHelperVersion ?? "";

        /// <summary>
        /// Downloads the newest published helper, verifies it and installs it.
        /// </summary>
        /// <exception cref="InvalidOperationException">No release, bad checksum or bad size.</exception>
        public async Task<RelayInstallResult> InstallAsync(
            IProgress<string>? progress,
            CancellationToken cancellationToken)
        {
            progress?.Report("release_lookup");
            ReleaseAsset release = await FindReleaseAsync(cancellationToken).ConfigureAwait(false);
            return await InstallReleaseAsync(release, progress, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Installs the helper only when it is missing or older than the newest
        /// published release. Returns null when the installed copy is already
        /// current, so a successful run costs one GitHub query and no download.
        ///
        /// This is what the automatic path calls: the helper is never a thing the
        /// viewer has to find, download or update by hand.
        /// </summary>
        public async Task<RelayInstallResult?> EnsureInstalledAsync(
            IProgress<string>? progress,
            CancellationToken cancellationToken)
        {
            progress?.Report("release_lookup");
            ReleaseAsset release = await FindReleaseAsync(cancellationToken).ConfigureAwait(false);
            if (IsCurrent(release, out _)) return null;
            return await InstallReleaseAsync(release, progress, cancellationToken).ConfigureAwait(false);
        }

        private async Task<RelayInstallResult> InstallReleaseAsync(
            ReleaseAsset release,
            IProgress<string>? progress,
            CancellationToken cancellationToken)
        {
            progress?.Report("download");
            string temp = HelperPath + ".download";
            Directory.CreateDirectory(Path.GetDirectoryName(HelperPath)!);
            TryDelete(temp);

            string expected = await DownloadChecksumAsync(release.ChecksumUrl, cancellationToken)
                .ConfigureAwait(false);

            try
            {
                await DownloadFileAsync(release.DownloadUrl, temp, release.Size, cancellationToken)
                    .ConfigureAwait(false);

                progress?.Report("verify");
                string actual = ComputeSha256(temp);
                if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Helper checksum mismatch: expected {expected}, got {actual}.");
                }

                if (File.Exists(HelperPath)) File.Delete(HelperPath);
                File.Move(temp, HelperPath);

                _config.Config.RelayHelperVersion = release.Version;
                _config.Config.RelayHelperSha256 = expected;
                _config.Save();

                return new RelayInstallResult(release.Version, expected);
            }
            finally
            {
                TryDelete(temp);
            }
        }

        /// <summary>True when the released version matches what is installed and intact.</summary>
        public bool IsCurrent(ReleaseAsset release, out string version)
        {
            version = InstalledVersion;
            if (!IsInstalled) return false;
            if (!string.Equals(release.Version, version, StringComparison.Ordinal)) return false;

            string recorded = _config.Config.RelayHelperSha256 ?? "";
            if (string.IsNullOrWhiteSpace(recorded)) return true;
            return string.Equals(ComputeSha256(HelperPath), recorded, StringComparison.OrdinalIgnoreCase);
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch
            {
                // A leftover temp file is harmless; the next run overwrites it.
            }
        }

        internal static string ComputeSha256(string path)
        {
            using var stream = File.OpenRead(path);
            using var sha = SHA256.Create();
            byte[] hash = sha.ComputeHash(stream);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        private async Task<ReleaseAsset> FindReleaseAsync(CancellationToken cancellationToken)
        {
            string url = $"https://api.github.com/repos/{Repository}/releases?per_page=30";
            using HttpResponseMessage response = await Http.GetAsync(url, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            using JsonDocument document = await JsonDocument.ParseAsync(stream, default, cancellationToken)
                .ConfigureAwait(false);

            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("Unexpected release feed.");

            foreach (JsonElement release in document.RootElement.EnumerateArray())
            {
                string tag = release.TryGetProperty("tag_name", out JsonElement tagValue)
                    ? tagValue.GetString() ?? ""
                    : "";
                if (!tag.StartsWith(TagPrefix, StringComparison.OrdinalIgnoreCase)) continue;

                string version = tag.Substring(TagPrefix.Length);
                string? download = null;
                string? checksum = null;
                long size = 0;

                if (!release.TryGetProperty("assets", out JsonElement assets)) continue;
                foreach (JsonElement asset in assets.EnumerateArray())
                {
                    string name = asset.TryGetProperty("name", out JsonElement nameValue)
                        ? nameValue.GetString() ?? ""
                        : "";
                    string? assetUrl = asset.TryGetProperty("browser_download_url", out JsonElement urlValue)
                        ? urlValue.GetString()
                        : null;
                    if (assetUrl == null) continue;

                    if (string.Equals(name, AssetName, StringComparison.OrdinalIgnoreCase))
                    {
                        download = assetUrl;
                        size = asset.TryGetProperty("size", out JsonElement sizeValue)
                            ? sizeValue.GetInt64()
                            : 0;
                    }
                    else if (string.Equals(name, ChecksumAssetName, StringComparison.OrdinalIgnoreCase))
                    {
                        checksum = assetUrl;
                    }
                }

                if (download != null && checksum != null)
                    return new ReleaseAsset(version, download, checksum, size);
            }

            throw new InvalidOperationException(
                $"No {TagPrefix}* release with {AssetName} and {ChecksumAssetName} was found.");
        }

        private static async Task<string> DownloadChecksumAsync(string url, CancellationToken cancellationToken)
        {
            string body = await Http.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
            string token = body.Split(new[] { ' ', '\t', '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            if (token.Length != 64 || !token.All(Uri.IsHexDigit))
                throw new InvalidOperationException("Published checksum is not a SHA-256 value.");
            return token.ToLowerInvariant();
        }

        private static async Task DownloadFileAsync(
            string url,
            string path,
            long expectedSize,
            CancellationToken cancellationToken)
        {
            using HttpResponseMessage response = await Http
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            long? length = response.Content.Headers.ContentLength;
            long limit = length is > MaxDownloadBytes ? MaxDownloadBytes : MaxDownloadBytes;
            if (length is > 0 && length > limit)
                throw new InvalidOperationException("Helper download is larger than expected.");

            await using Stream source = await response.Content
                .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using FileStream target = File.Create(path);

            byte[] buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > MaxDownloadBytes)
                    throw new InvalidOperationException("Helper download is larger than expected.");
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            if (expectedSize > 0 && total != expectedSize)
                throw new InvalidOperationException(
                    $"Helper download ended at {total} bytes, expected {expectedSize}.");
        }

        /// <summary>One published helper build.</summary>
        public sealed record ReleaseAsset(string Version, string DownloadUrl, string ChecksumUrl, long Size);
    }

    /// <summary>Outcome of a successful helper install.</summary>
    public sealed record RelayInstallResult(string Version, string Sha256);
}
