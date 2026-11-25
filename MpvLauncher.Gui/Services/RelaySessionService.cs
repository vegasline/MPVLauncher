// Original implementation, MIT licensed.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MpvLauncher.Gui.Services
{
    /// <summary>
    /// One fetch the helper needs from the viewer's own browser tab.
    ///
    /// The stream only answers requests made inside the page's session, so the
    /// helper asks the launcher instead of fetching by itself. The launcher
    /// forwards the request to the extension that is already on the page.
    /// </summary>
    public sealed record RelayFetch(int Status, string ContentType, string BodyBase64, string? Error);

    /// <summary>Page-context fetcher the helper borrows for its playlist and segments.</summary>
    public interface IRelayFetcher
    {
        Task<RelayFetch> FetchAsync(
            int id,
            string url,
            IReadOnlyDictionary<string, string> headers,
            CancellationToken cancellationToken);
    }

    /// <summary>
    /// One relay session: the helper process, the local URL it serves, and the
    /// browser it owns.
    ///
    /// The helper is started first because the URL only exists once the page has
    /// handed over its stream. mpv therefore starts afterwards and is attached
    /// by pid, which is what lets the helper exit exactly when playback does.
    /// The job object is the backstop: if this launcher dies without unwinding,
    /// closing the process closes the helper and every browser process under it.
    /// </summary>
    public sealed class RelaySession : IDisposable
    {
        private readonly Process _process;
        private readonly WindowsJob? _job;
        private readonly string _sessionDir;
        private bool _disposed;

        internal RelaySession(Process process, WindowsJob? job, string sessionDir, string localUrl, string pageUrl)
        {
            _process = process;
            _job = job;
            _sessionDir = sessionDir;
            LocalUrl = localUrl;
            PageUrl = pageUrl;
        }

        /// <summary>What mpv is given: 127.0.0.1 only, never the remote stream.</summary>
        public string LocalUrl { get; }

        /// <summary>The page the helper rendered. Kept for history and titles.</summary>
        public string PageUrl { get; }

        /// <summary>
        /// Tells the helper which mpv process it belongs to. Without this it can
        /// only watch the launcher, and a player closed by the user would leave
        /// the browser running until the launcher exits.
        /// </summary>
        public void AttachMpv(int mpvPid)
        {
            if (_disposed || _process.HasExited) return;
            try
            {
                var line = JsonSerializer.Serialize(new { cmd = "attach", mpvPid });
                _process.StandardInput.WriteLine(line);
                _process.StandardInput.Flush();
            }
            catch (Exception)
            {
                // The helper may have exited between the check and the write.
            }
        }

        /// <summary>
        /// Completes when the helper process ends, which in brokered mode is the
        /// moment mpv closes: the host process has to outlive mpv to answer its
        /// fetches, so it watches this instead of exiting right after launch.
        /// </summary>
        public async Task WaitForExitAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // A disposed process is as good as an exited one here.
            }
        }

        /// <summary>
        /// Answers the helper's fetch requests by asking the extension to fetch the
        /// URL inside the page already open in the viewer's own browser. The helper
        /// blocks on every reply, so this runs for the whole session: no second
        /// browser is started and nothing new shows up for the site to fingerprint.
        /// </summary>
        public async Task PumpAsync(IRelayFetcher fetcher, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                string? line;
                try
                {
                    line = await _process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception)
                {
                    return;
                }

                if (line is null) return;

                bool answered;
                try
                {
                    answered = await AnswerFetchAsync(_process, line, fetcher, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception)
                {
                    return;
                }

                if (!answered) continue;
            }
        }

        /// <summary>
        /// Answers one "fetch" line from the helper. Returns true when the line was
        /// such a request, so callers can skip it and look at the next event.
        ///
        /// This runs both while the session waits for ready and for the rest of
        /// playback: the helper's very first request - the playlist - happens
        /// before it can announce a URL, so the startup loop has to answer too.
        /// </summary>
        internal static async Task<bool> AnswerFetchAsync(
            Process process,
            string line,
            IRelayFetcher fetcher,
            CancellationToken cancellationToken)
        {
            if (!line.Contains("\"type\":\"fetch\"", StringComparison.Ordinal)) return false;

            int id = 0;
            string url = "";
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using JsonDocument document = JsonDocument.Parse(line);
                JsonElement root = document.RootElement;
                if (root.TryGetProperty("id", out JsonElement idElement)
                    && idElement.TryGetInt32(out int parsedId))
                {
                    id = parsedId;
                }
                if (root.TryGetProperty("url", out JsonElement urlElement))
                {
                    url = urlElement.GetString() ?? "";
                }
                if (root.TryGetProperty("headers", out JsonElement headersElement)
                    && headersElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (JsonProperty header in headersElement.EnumerateObject())
                    {
                        if (header.Value.ValueKind == JsonValueKind.String)
                        {
                            headers[header.Name] = header.Value.GetString() ?? "";
                        }
                    }
                }
            }
            catch (JsonException)
            {
                return true;
            }

            RelayFetch reply;
            try
            {
                reply = await fetcher.FetchAsync(id, url, headers, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                reply = new RelayFetch(0, "", "", error.Message);
            }

            // A write failure means the helper is gone; the session's own reader
            // notices the same thing on the next line.
            try
            {
                string answer = JsonSerializer.Serialize(new
                {
                    cmd = "fetch",
                    id,
                    status = reply.Status,
                    contentType = reply.ContentType,
                    body = reply.BodyBase64,
                    error = reply.Error,
                });
                await process.StandardInput.WriteLineAsync(answer).ConfigureAwait(false);
                process.StandardInput.Flush();
            }
            catch (Exception)
            {
                // Ignored on purpose: the helper's exit ends the session anyway.
            }

            return true;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                if (!_process.HasExited) _process.Kill(entireProcessTree: true);
            }
            catch (Exception) { }

            try { _process.Dispose(); } catch (Exception) { }
            _job?.Dispose();

            try
            {
                if (Directory.Exists(_sessionDir)) Directory.Delete(_sessionDir, recursive: true);
            }
            catch (Exception) { }
        }
    }

    /// <summary>
    /// Starts mpvrelay.exe and waits for the local URL it publishes.
    ///
    /// Nothing here touches cookies, headers or page state: the helper reports a
    /// local 127.0.0.1 URL and everything else stays inside the browser.
    /// </summary>
    public static class RelaySessionService
    {
        public const string Contract = "1";

        /// <summary>
        /// How long the helper may take to produce a URL. The helper itself gives
        /// up earlier (45 s by default); this is only the guard for a helper that
        /// hangs before reporting anything.
        /// </summary>
        private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(75);

        public static bool IsAvailable() => File.Exists(AppPaths.RelayHelperExe);

        /// <summary>
        /// Runs a session up to the ready event.
        /// </summary>
        /// <param name="pageUrl">The page to render, never a captured stream URL.</param>
        /// <param name="headless">Ask the helper for a headless window; it reports if that was refused.</param>
        /// <param name="detach">
        /// True when the caller is a short-lived process (the native messaging
        /// host). The helper then outlives its parent and watches mpv instead of
        /// the launcher, so no job object is used: closing it would kill a
        /// session that is still playing.
        /// </param>
        /// <exception cref="RelayException">The helper failed with a code the UI can translate.</exception>
        public static async Task<RelaySession> StartAsync(
            string pageUrl,
            string? title,
            IProgress<string>? progress,
            bool headless,
            CancellationToken cancellationToken,
            bool detach = false,
            string? streamUrl = null,
            IRelayFetcher? fetcher = null)
        {
            if (!IsAvailable())
                throw new RelayException("relay_helper_missing");

            AppPaths.EnsureLayout();

            string sessionDir = Path.Combine(AppPaths.CacheDir, "relay", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sessionDir);

            var startInfo = new ProcessStartInfo
            {
                FileName = AppPaths.RelayHelperExe,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = AppPaths.BinDir,
            };

            if (fetcher is null)
            {
                startInfo.ArgumentList.Add("--page");
                startInfo.ArgumentList.Add(pageUrl);
            }
            else
            {
                // Plan A: the page is already open in the viewer's own browser.
                // The extension hands over the captured URL and answers every
                // fetch, so the helper must not start a browser of its own -
                // doing that is what got the network blocked in the first place.
                startInfo.ArgumentList.Add("--broker");
                if (!string.IsNullOrWhiteSpace(streamUrl))
                {
                    startInfo.ArgumentList.Add("--stream");
                    startInfo.ArgumentList.Add(streamUrl!);
                }
            }
            startInfo.ArgumentList.Add("--session-dir");
            startInfo.ArgumentList.Add(sessionDir);
            startInfo.ArgumentList.Add("--launcher-pid");
            startInfo.ArgumentList.Add(Environment.ProcessId.ToString());
            startInfo.ArgumentList.Add("--contract");
            startInfo.ArgumentList.Add(Contract);

            // The helper never writes to stderr, and its session folder is
            // deleted when the session ends. A log in the launcher's logs
            // folder is therefore the only trace a failed relay leaves behind.
            startInfo.ArgumentList.Add("--log-file");
            startInfo.ArgumentList.Add(Path.Combine(
                AppPaths.LogsDir, "relay-" + Path.GetFileName(sessionDir) + ".log"));

            if (!string.IsNullOrWhiteSpace(title))
            {
                startInfo.ArgumentList.Add("--title");
                startInfo.ArgumentList.Add(title.Trim());
            }

            if (headless)
                startInfo.ArgumentList.Add("--headless");

            // A detached helper stops watching this process: the native host
            // exits as soon as the URL has been handed over, so its pipe says
            // nothing about whether mpv is still playing.
            if (detach)
                startInfo.ArgumentList.Add("--detach");

            Process process;
            try
            {
                process = Process.Start(startInfo) ?? throw new RelayException("relay_helper_failed");
            }
            catch (RelayException)
            {
                TryDeleteDirectory(sessionDir);
                throw;
            }
            catch (Exception)
            {
                TryDeleteDirectory(sessionDir);
                throw new RelayException("relay_helper_failed");
            }

            // No job object for a detached session: the job is closed with this
            // process, and that would kill a helper that is playing for an mpv
            // which outlives the native host.
            WindowsJob? job = detach ? null : WindowsJob.TryCreate(process, 0);

            try
            {
                var ready = await WaitForReadyAsync(process, progress, cancellationToken, fetcher).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(ready.Url))
                    throw new RelayException(MapErrorCode(ready.Code));

                return new RelaySession(process, job, sessionDir, ready.Url, pageUrl);
            }
            catch (RelayException)
            {
                Cleanup(process, job, sessionDir);
                throw;
            }
            catch (OperationCanceledException)
            {
                Cleanup(process, job, sessionDir);
                throw;
            }
            catch (Exception)
            {
                Cleanup(process, job, sessionDir);
                throw new RelayException("relay_no_stream");
            }
        }

        private static async Task<(string? Url, string? Code)> WaitForReadyAsync(
            Process process,
            IProgress<string>? progress,
            CancellationToken cancellationToken,
            IRelayFetcher? fetcher = null)
        {
            using var timeout = new CancellationTokenSource(ReadyTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

            try
            {
                while (true)
                {
                    string? line = await process.StandardOutput.ReadLineAsync(linked.Token).ConfigureAwait(false);
                    if (line == null) return (null, null);

                    // The helper fetches the playlist before it reports ready, and
                    // that first fetch is brokered too: without an answer here the
                    // startup would block until its timeout.
                    if (fetcher != null
                        && await RelaySession.AnswerFetchAsync(process, line, fetcher, linked.Token).ConfigureAwait(false))
                    {
                        continue;
                    }

                    string? URL = ParseEvent(line, progress, out bool failed, out string? code);
                    if (failed) return (null, code);
                    if (URL != null) return (URL, null);
                }
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                return (null, null);
            }
        }

        /// <summary>
        /// Helper exit codes are only interesting to the user as a sentence, so each
        /// one is mapped to its own locale key; everything unknown keeps the generic
        /// "no stream" wording.
        /// </summary>
        private static string MapErrorCode(string? code) => code switch
        {
            "challenge_blocked" => "relay_challenge_blocked",
            "access_restricted" => "relay_access_restricted",
            "token_expired" => "relay_token_expired",
            "stream_unavailable" => "relay_stream_unavailable",
            _ => "relay_no_stream"
        };

        /// <summary>
        /// One stdout line from the helper. Unknown types are ignored on purpose:
        /// a newer helper must not break an older launcher.
        /// </summary>
        private static string? ParseEvent(string line, IProgress<string>? progress, out bool failed, out string? code)
        {
            failed = false;
            code = null;
            line = line.Trim();
            if (line.Length == 0 || line[0] != '{') return null;

            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (!root.TryGetProperty("type", out var typeElement)) return null;

                string type = typeElement.GetString() ?? "";
                switch (type)
                {
                    case "ready":
                        return root.TryGetProperty("url", out var urlElement)
                            ? urlElement.GetString()
                            : null;

                    case "progress":
                        if (root.TryGetProperty("phase", out var phase))
                            progress?.Report(phase.GetString() ?? "");
                        return null;

                    case "error":
                        failed = true;
                        code = root.TryGetProperty("code", out var codeElement)
                            ? codeElement.GetString()
                            : null;
                        return null;

                    default:
                        return null;
                }
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static void Cleanup(Process process, WindowsJob? job, string sessionDir)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (Exception) { }

            try { process.Dispose(); } catch (Exception) { }
            job?.Dispose();
            TryDeleteDirectory(sessionDir);
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            }
            catch (Exception) { }
        }

        /// <summary>
        /// Stale session folders from a crash. Only folders inside the cache the
        /// launcher owns are touched.
        /// </summary>
        public static void CleanupStaleSessions()
        {
            try
            {
                string root = Path.Combine(AppPaths.CacheDir, "relay");
                if (!Directory.Exists(root)) return;

                foreach (var dir in Directory.GetDirectories(root))
                {
                    try
                    {
                        var info = new DirectoryInfo(dir);
                        if (info.LastWriteTimeUtc < DateTime.UtcNow.AddHours(-12))
                            info.Delete(recursive: true);
                    }
                    catch (Exception) { }
                }
            }
            catch (Exception) { }
        }
    }

    /// <summary>A relay start failure carrying the helper's own code.</summary>
    public sealed class RelayException : Exception
    {
        public RelayException(string code) : base(code) => Code = code;

        /// <summary>Message key suffix: relay_* in the loca files.</summary>
        public string Code { get; }
    }
}
