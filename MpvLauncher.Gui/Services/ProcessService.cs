using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace MpvLauncher.Gui.Services
{
    /// <summary>What came of asking mpv to identify itself.</summary>
    public enum MpvHealth
    {
        /// <summary>No binary at any known location.</summary>
        NotInstalled,

        /// <summary>
        /// A binary is there, but Windows refuses to start it. Reported
        /// separately from <see cref="Ok"/> because the file being on disk is
        /// not evidence that it works, and reporting success here is what makes
        /// the real failure surface much later as an unexplained dialog.
        /// </summary>
        NotRunnable,

        Ok
    }

    public sealed record MpvProbe(
        MpvHealth Health,
        string Version,
        string Fault,
        int? Win32Code,
        int? ExitCode = null)
    {
        /// <summary>STATUS_INVALID_IMAGE_FORMAT: imports could not be resolved.</summary>
        private const int StatusInvalidImageFormat = unchecked((int)0xC000007B);

        /// <summary>STATUS_DLL_NOT_FOUND: a statically imported library is absent.</summary>
        private const int StatusDllNotFound = unchecked((int)0xC0000135);

        /// <summary>
        /// True when Windows refused to load the image, rather than mpv running
        /// and then failing.
        ///
        /// The Windows loader resolves every static import before the entry
        /// point executes, so a missing export means the program never runs and
        /// no command-line option can influence it.
        ///
        /// Both shapes have to be recognised, and which one occurs is not up to
        /// us. Depending on how the process was created, an unresolved import
        /// either fails CreateProcess with 126/127 - a missing library, or one
        /// that exists without the export - or the process starts and the loader
        /// kills it with STATUS_INVALID_IMAGE_FORMAT. Reproduced by putting a
        /// stub vulkan-1.dll next to the real mpv.exe, which produces the exit
        /// code rather than the Win32 error.
        ///
        /// This is not hypothetical: the bundled mpv imports
        /// vkGetPhysicalDeviceProperties2 from vulkan-1.dll, which is Vulkan 1.1.
        /// A machine with an older loader - an out-of-date driver, a virtual
        /// display adapter, a remote desktop host - cannot run it at all.
        /// </summary>
        public bool IsLoaderProblem =>
            Win32Code is 126 or 127 ||
            ExitCode == StatusInvalidImageFormat ||
            ExitCode == StatusDllNotFound;

        /// <summary>
        /// A fault string worth showing a user. Raw numeric statuses mean
        /// nothing to the person reading them, so they are named.
        /// </summary>
        public string Describe()
        {
            if (ExitCode == StatusInvalidImageFormat)
                return "Windows could not load the program: an imported function is missing from a system library";
            if (ExitCode == StatusDllNotFound)
                return "Windows could not load the program: a required system library is missing";
            if (Win32Code is 126 or 127)
                return "Windows could not load the program: a required export is missing";
            return Fault;
        }
    }

    /// <summary>
    /// Locates mpv and starts it on something the user typed.
    ///
    /// This is the GUI's own path, as opposed to the browser extension's: the
    /// value came from the URL box, a dropped file or the history list, not from
    /// a web page. That distinction is why local paths and other schemes are
    /// allowed here, while the native host restricts itself to http(s).
    /// </summary>
    public class ProcessService
    {
        private const int ProbeTimeoutMs = 8000;

        private readonly ConfigService _configService;

        public ProcessService(ConfigService configService)
        {
            _configService = configService;
        }

        /// <summary>
        /// Runs "mpv --version" and classifies the outcome.
        ///
        /// Every launch path depends on this being answered by actually running
        /// the binary. Checking that the file exists is not enough: an image
        /// whose imports cannot be resolved is present and unstartable, and that
        /// is a state the UI has to be able to describe.
        /// </summary>
        public MpvProbe ProbeMpv()
        {
            string path = FindMpvPath();
            if (!File.Exists(path))
                return new MpvProbe(MpvHealth.NotInstalled, "", "not found", null);

            Process? p = null;
            try
            {
                p = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = path,
                        Arguments = "--version",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };
                p.Start();

                // Read both pipes concurrently. Draining one while the other
                // fills would deadlock, and mpv's startup chatter can be large.
                var stdout = p.StandardOutput.ReadToEndAsync();
                var stderr = p.StandardError.ReadToEndAsync();

                if (!p.WaitForExit(ProbeTimeoutMs))
                {
                    try { p.Kill(); } catch { /* already gone */ }
                    return new MpvProbe(MpvHealth.NotRunnable, "", "did not exit", null);
                }

                string first = stdout.GetAwaiter().GetResult().Split('\n').FirstOrDefault()?.Trim() ?? "";
                if (first.Length == 0)
                {
                    // Started, produced nothing. Either the loader killed it on
                    // the way in, or mpv itself failed and said why on stderr.
                    string why = stderr.GetAwaiter().GetResult().Trim();
                    if (why.Length > 200) why = why[..200];
                    var probe = new MpvProbe(
                        MpvHealth.NotRunnable,
                        "",
                        why.Length > 0 ? why : $"exit code {p.ExitCode}",
                        null,
                        p.ExitCode);
                    return probe with { Fault = probe.Describe() };
                }

                return new MpvProbe(MpvHealth.Ok, first, "", null);
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                // The loader refused the image: no process was ever created.
                return new MpvProbe(MpvHealth.NotRunnable, "", ex.Message, ex.NativeErrorCode);
            }
            catch (Exception ex)
            {
                return new MpvProbe(MpvHealth.NotRunnable, "", ex.Message, null);
            }
            finally
            {
                p?.Dispose();
            }
        }

        /// <summary>
        /// The mpv executable to use: the configured path if it still exists,
        /// then a few well-known install locations. Falling back to the bare name
        /// lets the shell resolve it from PATH, which is why a stale setting does
        /// not produce a hard failure.
        /// </summary>
        public string FindMpvPath()
        {
            string saved = _configService.Config.MpvPath;
            if (File.Exists(saved)) return saved;

            string[] common = new[]
            {
                AppPaths.MpvExe,
                @"D:\Prog\mpv\mpv.exe",
                @"C:\Program Files\mpv\mpv.exe",
                @"C:\Program Files (x86)\mpv\mpv.exe"
            };

            foreach (var p in common)
            {
                if (File.Exists(p))
                {
                    _configService.Config.MpvPath = p;
                    _configService.Save();
                    return p;
                }
            }

            return "mpv.exe";
        }

        /// <summary>
        /// Launches mpv on a URL or local path typed by the user.
        ///
        /// Arguments are collected as separate items rather than pasted into one
        /// string: the URL may contain quotes, spaces or trailing backslashes, and
        /// a hand-built command line lets any of those turn into an extra mpv
        /// option. ArgumentList performs the platform quoting itself.
        ///
        /// Unlike the extension path this accepts local paths and other schemes,
        /// because the value came from the user rather than from a web page.
        /// </summary>
        public (bool Success, string Message, MpvProbe Probe) PlayMedia(string urlOrPath)
        {
            if (string.IsNullOrWhiteSpace(urlOrPath))
                return (false, "Invalid or empty URL.",
                        new MpvProbe(MpvHealth.NotInstalled, "", "empty input", null));

            string url = urlOrPath.Trim();

            // Ask the binary to identify itself before handing it a URL. When
            // the loader refuses the image, launching it anyway raises a modal
            // "Entry Point Not Found" dialog that the user cannot connect to
            // anything, and Process.Start with UseShellExecute does not reliably
            // report the refusal as an exception.
            var probe = ProbeMpv();
            if (probe.Health == MpvHealth.NotInstalled)
                return (false, "mpv was not found.", probe);
            if (probe.Health == MpvHealth.NotRunnable)
                return (false, probe.Fault, probe);

            string mpvBinary = FindMpvPath();
            var cfg = _configService.Config;

            var argsList = new List<string>();
            string fw = cfg.ForceWindow ?? "immediate";
            if (fw.Equals("no", StringComparison.OrdinalIgnoreCase))
                argsList.Add("--force-window=no");
            else if (fw.Equals("yes", StringComparison.OrdinalIgnoreCase))
                argsList.Add("--force-window=yes");
            else
                argsList.Add("--force-window=immediate");

            if (cfg.AlwaysOnTop)
                argsList.Add("--ontop");
            if (!string.IsNullOrWhiteSpace(cfg.Geometry))
                argsList.Add($"--geometry={cfg.Geometry.Trim()}");
            if (!string.IsNullOrWhiteSpace(cfg.MpvProfile))
                argsList.Add($"--profile={cfg.MpvProfile.Trim()}");
            if (!string.IsNullOrWhiteSpace(cfg.ExtraArgs))
                argsList.AddRange(cfg.ExtraArgs.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries));
            argsList.Add(url);

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = mpvBinary,
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(mpvBinary) ?? ""
                };
                foreach (string a in argsList) psi.ArgumentList.Add(a);

                Process.Start(psi);
                _configService.AddHistory(url);
                return (true, "MPV started.", probe);
            }
            catch (Exception ex)
            {
                return (false, "Launch failed: " + ex.Message, probe);
            }
        }
    }
}
