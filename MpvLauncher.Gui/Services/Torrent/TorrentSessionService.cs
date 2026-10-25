// Original implementation, MIT licensed.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MpvLauncher.Gui.Services
{
    public sealed record TorrentStartRequest(
        TorrentSource Source,
        AppConfig Config,
        int LauncherPid,
        int? SelectFile);

    /// <summary>
    /// Owns the mpvtorrent child process for one playback session.
    ///
    /// The helper is a line-oriented JSON process: events on stdout, commands
    /// on stdin. This class never interprets a line as a command and never
    /// passes a helper field to a shell; it only parses numbers and strings.
    /// Shutdown has three layers - a polite close command, a bounded wait, then
    /// a process-tree kill - and the helper independently watches the launcher
    /// PID, so a launcher crash cannot leave an orphan.
    /// </summary>
    public sealed class TorrentSessionService : IAsyncDisposable
    {
        public event Action<TorrentHelperEvent>? EventReceived;
        public event Action<TorrentHelperEvent>? Ready;
        public event Action<int>? Exited;

        private Process? _process;
        private string _sessionDir = "";
        private readonly StringBuilder _stderr = new();
        private TaskCompletionSource<bool>? _handshake;
        private int _stopping;

        public bool IsRunning => _process is { HasExited: false };
        public int? ProcessId => _process?.Id;
        public string SessionDirectory => _sessionDir;

        public static string SessionsRoot => Path.Combine(AppPaths.TorrentCacheDir, "sessions");
        public static string TrackerFile => Path.Combine(AppPaths.TorrentCacheDir, "trackers.txt");
        public static string HelperLogFile => Path.Combine(AppPaths.LogsDir, "mpvtorrent.log");

        /// <summary>
        /// Removes folders left by a crashed launcher or a killed helper. The
        /// helper normally deletes its own folder; this is the belt-and-braces
        /// pass that runs before every new session.
        /// </summary>
        public static void CleanStaleSessions()
        {
            try
            {
                if (!Directory.Exists(SessionsRoot)) return;
                foreach (string dir in Directory.EnumerateDirectories(SessionsRoot))
                {
                    try { Directory.Delete(dir, recursive: true); } catch { /* in use */ }
                }
            }
            catch
            {
                // Cleanup is best effort; it must never stop playback.
            }
        }

        public async Task<(bool Ok, string Message)> StartAsync(TorrentStartRequest request, CancellationToken ct = default)
        {
            if (IsRunning) return (false, "A torrent session is already running.");

            if (!File.Exists(TorrentHelperInstallService.HelperPath))
                return (false, "mpvtorrent is not installed.");

            if (!TorrentHelperInstallService.VerifyInstalledHash(request.Config.TorrentHelperSha256))
                return (false, "The mpvtorrent helper failed its SHA256 check and was not started. Reinstall it from Settings.");

            if (request.Config.TorrentStreamingEnabled == false)
                return (false, "Torrent streaming is disabled.");

            try
            {
                CleanStaleSessions();
                Directory.CreateDirectory(SessionsRoot);
                Directory.CreateDirectory(AppPaths.TorrentCacheDir);

                _sessionDir = Path.Combine(SessionsRoot, Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(_sessionDir);

                // The tracker file is written on every start so edits in the
                // settings box take effect without a separate save button.
                File.WriteAllText(
                    TrackerFile,
                    TorrentSettings.NormalizeTrackerList(request.Config.TorrentTrackerList),
                    new UTF8Encoding(false));

                Directory.CreateDirectory(AppPaths.LogsDir);

                int? selectFile = request.SelectFile;
                if (selectFile is < 0) selectFile = null;

                var args = TorrentHelperArguments.Build(
                    request.Source.Value,
                    _sessionDir,
                    AppPaths.TorrentCacheDir,
                    TrackerFile,
                    HelperLogFile,
                    request.LauncherPid,
                    request.Config.TorrentForwardWindowMiB,
                    request.Config.TorrentUploadLimitKiB,
                    request.Config.TorrentMaxConnections,
                    request.Config.TorrentMaxHalfOpen,
                    request.Config.TorrentTailWaitSeconds,
                    selectFile);

                var psi = new ProcessStartInfo
                {
                    FileName = TorrentHelperInstallService.HelperPath,
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WorkingDirectory = AppPaths.BinDir,
                    StandardOutputEncoding = new UTF8Encoding(false),
                    StandardErrorEncoding = new UTF8Encoding(false)
                };
                foreach (string arg in args) psi.ArgumentList.Add(arg);

                var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
                _handshake = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _stopping = 0;

                process.Exited += (_, _) =>
                {
                    _handshake?.TrySetResult(false);
                    CleanupSessionDirectory();
                    Exited?.Invoke(process.ExitCode);
                };

                if (!process.Start())
                {
                    CleanupSessionDirectory();
                    return (false, "mpvtorrent could not be started.");
                }

                _process = process;
                _ = ReadStdoutAsync(process, ct);
                _ = ReadStderrAsync(process);

                var completed = await Task.WhenAny(
                    _handshake.Task,
                    Task.Delay(TimeSpan.FromSeconds(10), ct));
                if (completed != _handshake.Task)
                {
                    await StopAsync();
                    return (false, "mpvtorrent did not answer within 10 seconds.");
                }

                if (!await _handshake.Task)
                {
                    await StopAsync();
                    return (false, "mpvtorrent exited during startup. See the log for details.");
                }

                return (true, "Torrent session started.");
            }
            catch (OperationCanceledException)
            {
                await StopAsync();
                return (false, "Torrent session start was cancelled.");
            }
            catch (Exception ex)
            {
                await StopAsync();
                return (false, "Could not start mpvtorrent: " + ex.Message);
            }
        }

        public async Task SendSelectAsync(int fileIndex, CancellationToken ct = default)
        {
            var process = _process;
            if (process is null || process.HasExited) return;
            try
            {
                await process.StandardInput.WriteLineAsync(
                    "{\"cmd\":\"select\",\"fileIndex\":" + fileIndex.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}");
                await process.StandardInput.FlushAsync(ct);
            }
            catch (IOException)
            {
                // The helper died between the check and the write; the exit
                // handler reports it.
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private async Task ReadStdoutAsync(Process process, CancellationToken ct)
        {
            try
            {
                while (true)
                {
                    string? line = await process.StandardOutput.ReadLineAsync(ct);
                    if (line is null) break;
                    if (line.Length > 1_000_000) continue;
                    var ev = TorrentHelperEvent.Parse(line);
                    if (ev is null) continue;

                    if (ev.Type == "hello")
                    {
                        bool compatible = ev.Contract == TorrentHelperArguments.ContractVersion;
                        _handshake?.TrySetResult(compatible);
                        if (!compatible)
                        {
                            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
                            return;
                        }
                        continue;
                    }

                    EventReceived?.Invoke(ev);
                    if (ev.Type == "ready") Ready?.Invoke(ev);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (IOException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            finally
            {
                _handshake?.TrySetResult(false);
            }
        }

        private async Task ReadStderrAsync(Process process)
        {
            try
            {
                string? line;
                while ((line = await process.StandardError.ReadLineAsync()) is not null)
                {
                    if (_stderr.Length > 8192) _stderr.Remove(0, _stderr.Length - 4096);
                    _stderr.AppendLine(line);
                }
            }
            catch (IOException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public async Task StopAsync(TimeSpan? grace = null)
        {
            if (Interlocked.Exchange(ref _stopping, 1) == 1) return;
            var process = _process;
            _process = null;

            if (process is null)
            {
                CleanupSessionDirectory();
                return;
            }

            try
            {
                if (!process.HasExited)
                {
                    await process.StandardInput.WriteLineAsync("{\"cmd\":\"close\"}");
                    await process.StandardInput.FlushAsync();
                }
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }

            bool exited = false;
            try
            {
                using var timeout = new CancellationTokenSource(grace ?? TimeSpan.FromSeconds(8));
                await process.WaitForExitAsync(timeout.Token);
                exited = true;
            }
            catch (OperationCanceledException)
            {
            }

            if (!exited)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
                try { await process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(3)).Token); } catch { }
            }

            try { process.Dispose(); } catch { }
            CleanupSessionDirectory();
        }

        private void CleanupSessionDirectory()
        {
            string dir = _sessionDir;
            _sessionDir = "";
            if (string.IsNullOrEmpty(dir)) return;
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // The helper deletes its own download folder; anything left is
                // removed by CleanStaleSessions on the next start.
            }
        }

        public async ValueTask DisposeAsync() => await StopAsync();
    }
}
