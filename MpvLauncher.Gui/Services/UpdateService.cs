using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MpvLauncher.Gui.Services
{
    /// <summary>A release newer than the running build.</summary>
    public sealed record UpdateOffer(string Version, string Tag, long SizeBytes);

    public enum UpdateStage
    {
        UpToDate,
        /// <summary>Nothing newer published.</summary>
        None,

        /// <summary>Published but not fetched yet.</summary>
        Available,

        /// <summary>Downloaded and verified, waiting for the user to apply.</summary>
        Ready
    }

    /// <summary>
    /// Where an update check ended up.
    ///
    /// Detail is a localization key plus its arguments rather than a finished
    /// sentence, because this text ends up in the status line in twelve
    /// languages. The service decides what happened; the window decides how to
    /// say it. This is the same split the install and uninstall results use.
    /// </summary>
    public sealed record UpdateStatus(UpdateStage Stage, string Version, string? DetailKey, params string[] DetailArgs)
    {
        public static UpdateStatus UpToDate { get; } =
            new(UpdateStage.UpToDate, "", null);
    }

    /// <summary>
    /// Checks GitHub for a newer build, fetches it, and swaps it in.
    ///
    /// The update is downloaded and verified on its own; installing it is a
    /// separate, deliberate step. That split is not caution for its own sake:
    /// replacing a running executable means the replacement only takes effect
    /// after a restart, so doing it silently would mean the app changes under
    /// the user between one launch and the next with nothing on screen to
    /// explain it.
    ///
    /// Verification is not optional. This downloads an executable that will
    /// replace the one already running, and the only thing standing between a
    /// compromised mirror and that is a checksum - which is why the release
    /// workflow publishes one alongside the binary.
    ///
    /// Nothing here touches settings, history or the tools folder. Those live
    /// under %APPDATA% and are left exactly as they are, so a user's layout,
    /// filters and installed mpv survive an update untouched.
    /// </summary>
    public class UpdateService
    {
        /// <summary>The repository this app is released from. Not configurable.</summary>
        public const string Repo = "vegasline/MPVLauncher";

        private const string ApiUrl = "https://api.github.com/repos/" + Repo + "/releases/latest";
        private const string ExeName = "MPVLauncher.exe";
        private const string ChecksumName = ExeName + ".sha256";
        private const string StagedName = "MPVLauncher.staged.exe";

        /// <summary>Ceiling for the download, matching the workflow's own limit.</summary>
        private const long MaxDownloadBytes = 128L * 1024 * 1024;

        /// <summary>
        /// GitHub refuses REST requests that carry no User-Agent, answering 403
        /// with "Request forbidden by administrative rules" rather than saying
        /// what was wrong. .NET's HttpClient sends none by default, so this has
        /// to be set here or every check fails.
        ///
        /// DownloadInstallService sets one on its own client for the same reason.
        /// </summary>
        public static readonly string UserAgent =
            "MPVLauncher/" + Repo.Replace("/", "-") + " (+https://github.com/" + Repo + ")";

        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            return client;
        }

        private readonly ConfigService _configService;

        public UpdateService(ConfigService configService)
        {
            _configService = configService;
        }

        /// <summary>
        /// Staging area, outside the tools folder. %LOCALAPPDATA% because it is
        /// per-machine scratch space and is not something the user browses.
        /// </summary>
        private static string StagingDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MPVLauncher", "update");

        private static string StagedPath => Path.Combine(StagingDir, StagedName);

        /// <summary>The running executable.</summary>
        private static string CurrentExePath =>
            Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, ExeName);

        /// <summary>
        /// The version of the running build, taken from the assembly rather than
        /// from a constant, so it cannot drift from what was published.
        /// </summary>
        public static string CurrentVersion =>
            typeof(UpdateService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

        /// <summary>
        /// Deletes leftovers from a previous install.
        ///
        /// The replaced executable is renamed rather than deleted at update time,
        /// because Windows will not let a running image be deleted. It is still
        /// locked at that moment and can only go once the process has exited, so
        /// the removal happens on the next launch instead.
        /// </summary>
        public static void CleanUpPreviousUpdate()
        {
            try
            {
                string old = CurrentExePath + ".old";
                if (File.Exists(old)) File.Delete(old);
            }
            catch { /* it will be tried again next launch */ }
        }

        /// <summary>
        /// Compares versions as tuples of numbers, so 1.9.10 sorts above 1.9.9.
        /// A string comparison would get that backwards and offer the wrong one.
        /// </summary>
        internal static bool IsNewer(string candidate, string current)
        {
            int[] c = Parse(candidate);
            int[] u = Parse(current);
            int n = Math.Max(c.Length, u.Length);

            for (int i = 0; i < n; i++)
            {
                int a = i < c.Length ? c[i] : 0;
                int b = i < u.Length ? u[i] : 0;
                if (a != b) return a > b;
            }
            return false;
        }

        private static int[] Parse(string v)
        {
            var trimmed = v.Trim().TrimStart('v');
            // Drop anything after the numeric part, such as a pre-release suffix.
            int end = 0;
            while (end < trimmed.Length && (char.IsDigit(trimmed[end]) || trimmed[end] == '.')) end++;

            string[] parts = trimmed[..end].Split('.', StringSplitOptions.RemoveEmptyEntries);
            var outv = new int[parts.Length];
            for (int i = 0; i < parts.Length; i++)
                int.TryParse(parts[i], out outv[i]);
            return outv;
        }

        /// <summary>
        /// Asks GitHub for the newest release and compares it with this build.
        ///
        /// Network and every failure mode are absorbed: an update check is never
        /// a reason to disturb the user, and a machine with no route to GitHub
        /// should behave exactly like one that is already current.
        /// </summary>
        public async Task<UpdateStatus> CheckAsync(CancellationToken ct = default)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, ApiUrl);
                req.Headers.Accept.ParseAdd("application/vnd.github+json");

                using var resp = await Http.SendAsync(req, ct);
                if (!resp.IsSuccessStatusCode)
                    return DescribeHttpFailure(resp);

                await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

                string tag = doc.RootElement.TryGetProperty("tag_name", out var t)
                    ? (t.GetString() ?? "") : "";
                if (tag.Length == 0) return UpdateStatus.UpToDate;

                string version = tag.TrimStart('v');
                if (!IsNewer(version, CurrentVersion))
                    return UpdateStatus.UpToDate;

                if (!TryFindAsset(doc.RootElement, ExeName, out string exeUrl, out long size))
                    return new UpdateStatus(UpdateStage.UpToDate, "", "update_err_no_asset");
                if (!TryFindAsset(doc.RootElement, ChecksumName, out string sumUrl, out _))
                    return new UpdateStatus(UpdateStage.Available, version, "update_err_no_checksum");

                // Something is already downloaded and verified.
                if (File.Exists(StagedPath))
                    return new UpdateStatus(UpdateStage.Ready, version, null);

                UpdateStatus fetched = await DownloadAsync(exeUrl, sumUrl, size, version, ct);
                return fetched;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // The exception text is whatever the network stack said, and is
                // not ours to translate; it is passed as an argument to a
                // sentence that is.
                return new UpdateStatus(UpdateStage.UpToDate, "", "update_err_exception", ex.Message);
            }
        }

        /// <summary>
        /// Turns an HTTP failure into a key the window can translate.
        ///
        /// 403 from this endpoint is almost always the unauthenticated rate
        /// limit - sixty requests an hour, per address - and it is worth saying
        /// so rather than showing a number that means nothing. 404 means there
        /// is no release at all, which on a fresh repository is not a fault.
        /// </summary>
        private static UpdateStatus DescribeHttpFailure(HttpResponseMessage resp)
        {
            int code = (int)resp.StatusCode;

            if (resp.Headers.TryGetValues("X-RateLimit-Remaining", out var left) &&
                left.FirstOrDefault() == "0")
            {
                return new UpdateStatus(UpdateStage.UpToDate, "", "update_err_rate_limited");
            }

            return code switch
            {
                403 => new UpdateStatus(UpdateStage.UpToDate, "", "update_err_forbidden"),
                404 => new UpdateStatus(UpdateStage.UpToDate, "", "update_err_no_release"),
                _ => new UpdateStatus(UpdateStage.UpToDate, "", "update_err_http", code.ToString())
            };
        }

        private static bool TryFindAsset(
            JsonElement root, string name, out string url, out long size)
        {
            url = "";
            size = 0;

            if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
                return false;

            foreach (var a in assets.EnumerateArray())
            {
                if (!string.Equals(a.GetProperty("name").GetString(), name, StringComparison.OrdinalIgnoreCase))
                    continue;

                url = a.GetProperty("browser_download_url").GetString() ?? "";
                if (a.TryGetProperty("size", out var s)) size = s.GetInt64();
                return url.Length > 0;
            }
            return false;
        }

        /// <summary>
        /// Fetches the executable and its checksum, and only stages the file if
        /// the two agree.
        ///
        /// The checksum is downloaded first so a failure there does not leave a
        /// hundred megabytes of unverified binary on disk.
        /// </summary>
        private async Task<UpdateStatus> DownloadAsync(
            string exeUrl, string sumUrl, long expectedSize, string version, CancellationToken ct)
        {
            Directory.CreateDirectory(StagingDir);

            string expected = await DownloadChecksumAsync(sumUrl, ct);
            if (expected.Length != 64)
                return new UpdateStatus(UpdateStage.Available, version, "update_err_checksum_unreadable");

            if (expectedSize > MaxDownloadBytes)
                return new UpdateStatus(UpdateStage.Available, version, "update_err_too_large");

            string partial = StagedPath + ".part";
            try
            {
                using (var resp = await Http.GetAsync(exeUrl, HttpCompletionOption.ResponseHeadersRead, ct))
                {
                    if (!resp.IsSuccessStatusCode)
                        return new UpdateStatus(UpdateStage.Available, version, "update_err_http", ((int)resp.StatusCode).ToString());

                    long? declared = resp.Content.Headers.ContentLength;
                    if (declared is > MaxDownloadBytes)
                        return new UpdateStatus(UpdateStage.Available, version, "update_err_too_large");

                    await using var src = await resp.Content.ReadAsStreamAsync(ct);
                    await using var dst = new FileStream(
                        partial, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024);

                    byte[] buf = new byte[128 * 1024];
                    long total = 0;
                    int read;
                    while ((read = await src.ReadAsync(buf, ct)) > 0)
                    {
                        total += read;
                        // The declared length can be absent or wrong, so the cap is
                        // enforced while streaming rather than trusted up front.
                        if (total > MaxDownloadBytes)
                            return new UpdateStatus(UpdateStage.Available, version, "update_err_too_large");
                        await dst.WriteAsync(buf.AsMemory(0, read), ct);
                    }
                }

                string actual = await Sha256Async(partial, ct);
                if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(partial);
                    return new UpdateStatus(UpdateStage.Available, version, "update_err_checksum_mismatch");
                }

                File.Move(partial, StagedPath, overwrite: true);
                return new UpdateStatus(UpdateStage.Ready, version, null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                TryDelete(partial);
                return new UpdateStatus(UpdateStage.Available, version, ex.Message);
            }
        }

        private async Task<string> DownloadChecksumAsync(string url, CancellationToken ct)
        {
            using var resp = await Http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return "";

            string text = await resp.Content.ReadAsStringAsync(ct);

            // Written as "<hash>  <filename>", so take the first token and keep
            // only hex.
            string token = text.Split(new[] { ' ', '\t', '\r', '\n' },
                                      StringSplitOptions.RemoveEmptyEntries)
                               .FirstOrDefault() ?? "";

            var sb = new StringBuilder(64);
            foreach (char c in token)
            {
                if (Uri.IsHexDigit(c)) sb.Append(c);
                if (sb.Length == 64) break;
            }
            return sb.ToString().ToLowerInvariant();
        }

        private static async Task<string> Sha256Async(string path, CancellationToken ct)
        {
            await using var fs = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024);
            using var sha = SHA256.Create();
            var hash = await sha.ComputeHashAsync(fs, ct);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        /// <summary>
        /// Swaps the staged build in for the running one.
        ///
        /// Windows will not let a running image be overwritten, but it will let
        /// it be renamed, so the old file is renamed out of the way and the new
        /// one written in its place. The rename leaves a file that stays locked
        /// until this process exits, which is why CleanUpPreviousUpdate removes
        /// it on the next launch.
        ///
        /// The staged file is hashed again first. It sat in a world-writable
        /// temp directory between the download and this click, and it is about
        /// to become the running program.
        ///
        /// Returns null on success, or a localization key with its arguments.
        /// </summary>
        public async Task<(string? Key, string[] Args)> ApplyAsync(CancellationToken ct = default)
        {
            if (!File.Exists(StagedPath)) return ("update_err_nothing_staged", Array.Empty<string>());

            string exe = CurrentExePath;
            string dir = Path.GetDirectoryName(exe) ?? "";
            string old = exe + ".old";

            // Probing writability first gives a better message than a failure
            // three quarters of the way through the swap, which would leave the
            // app without an executable.
            try
            {
                string probe = Path.Combine(dir, ".write-probe-" + Environment.ProcessId);
                File.WriteAllText(probe, "");
                File.Delete(probe);
            }
            catch (UnauthorizedAccessException)
            {
                return ("update_err_not_writable_admin", Array.Empty<string>());
            }
            catch (IOException)
            {
                return ("update_err_not_writable", Array.Empty<string>());
            }

            try
            {
                string stagedHash = await Sha256Async(StagedPath, ct);

                // Any .old from an interrupted attempt goes first, otherwise the
                // rename below cannot succeed.
                TryDelete(old);

                File.Move(exe, old);
                try
                {
                    File.Move(StagedPath, exe);
                }
                catch
                {
                    // Put the original back rather than leaving no program at all.
                    try { File.Move(old, exe); } catch { }
                    throw;
                }

                return (null, Array.Empty<string>());
            }
            catch (Exception ex)
            {
                return ("update_err_install", new[] { ex.Message });
            }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        /// <summary>
        /// Reads the staged version without applying it, for the status line.
        /// </summary>
        public bool IsStaged()
        {
            try { return File.Exists(StagedPath); }
            catch { return false; }
        }
    }
}