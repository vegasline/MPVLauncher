using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace MpvLauncher.Gui.Services
{
    /// <summary>
    /// Native messaging host: [4-byte LE length][UTF-8 JSON].
    /// Chromium passes the calling extension's origin as the first argument;
    /// Firefox passes the path of its native-messaging manifest JSON.
    /// </summary>
    public static class NativeHostService
    {
        private static readonly HashSet<string> BrowserProcessNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "firefox", "firefox-bin",
            "chrome", "chrome.exe",
            "brave", "msedge", "msedgewebview2",
            "chromium", "opera", "vivaldi", "browser"
        };

        /// <summary>Rotates the log once it passes this size.</summary>
        private const long MaxLogBytes = 2 * 1024 * 1024;

        /// <summary>
        /// Strips the query string before a URL reaches the log.
        ///
        /// Signed media URLs carry their access token in the query, so logging
        /// them in full writes a working credential to disk that stays valid
        /// until it expires. Host and path are enough to debug a failure.
        /// </summary>
        private static string RedactUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return "";
            try
            {
                if (Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                    (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                {
                    return $"{uri.Scheme}://{uri.Host}{uri.AbsolutePath}" +
                           (string.IsNullOrEmpty(uri.Query) ? "" : "?<redacted>");
                }
            }
            catch { }
            // Not a URL we can parse (file://, UNC, ...) - show only its length.
            return $"<non-http url, {url.Length} chars>";
        }

        public static bool IsHostMode(string[] args)
        {
            bool hasHostArg = false;
            bool explicitFlag = false;
            if (args != null)
            {
                foreach (var raw in args)
                {
                    if (string.IsNullOrWhiteSpace(raw)) continue;
                    string a = raw.Trim().Trim('"');

                    if (a.Equals("--host", StringComparison.OrdinalIgnoreCase)) { explicitFlag = true; continue; }
                    if (a.Equals("--native-host", StringComparison.OrdinalIgnoreCase)) { explicitFlag = true; continue; }

                    // The two calling conventions the browsers actually use:
                    // Chromium passes the calling extension's origin, Firefox
                    // passes the path of its native messaging manifest. Both are
                    // placed there by the browser, and only after it has checked
                    // its own allowed_origins / allowed_extensions list - so this
                    // is evidence a browser launched us, not weaker evidence than
                    // the manifest path.
                    if (a.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase)) { hasHostArg = true; continue; }
                    if (a.StartsWith("moz-extension://", StringComparison.OrdinalIgnoreCase)) { hasHostArg = true; continue; }

                    if (LooksLikeNativeManifest(a)) hasHostArg = true;
                }
            }

            // Host mode talks over stdin, so without a pipe there is nothing to
            // read and no browser could have launched us this way.
            bool redirected = false;
            try { redirected = Console.IsInputRedirected; } catch { }
            if (!redirected) return false;

            // A pipe plus one of: a browser-style argument, an explicit flag, or
            // a browser as the parent process. Anything less means this is an
            // ordinary launch and the window should open.
            //
            // Note that a local process which can pass "--host" and write to our
            // stdin could equally start mpv itself, so refusing that case buys
            // no real isolation - which is why it is allowed rather than letting
            // it break Chromium, whose parent process is not reliably reported.
            return hasHostArg
                || explicitFlag
                || IsBrowserName(GetParentProcessName());
        }

        private static bool LooksLikeNativeManifest(string path)
        {
            try
            {
                if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return false;
                if (!File.Exists(path)) return false;
                string txt = File.ReadAllText(path);
                return txt.Contains("com.mpv.launcher", StringComparison.OrdinalIgnoreCase)
                    || txt.Contains("\"stdio\"", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsBrowserName(string? name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string n = name.Replace(".exe", "", StringComparison.OrdinalIgnoreCase);
            return BrowserProcessNames.Contains(n) || BrowserProcessNames.Contains(name);
        }

        public static void Run(string[]? args = null)
        {
            AppPaths.EnsureLayout();

            Log($"Native host started. Args: {string.Join(" ", args ?? Array.Empty<string>())}");

            var stdin = Console.OpenStandardInput();
            var stdout = Console.OpenStandardOutput();

            try
            {
                var msg = ReadMessage(stdin);
                if (msg != null && msg.TryGetValue("url", out var urlVal))
                {
                    string url = urlVal?.ToString() ?? "";
                    string referrer = "";
                    if (msg.TryGetValue("referrer", out var refVal))
                        referrer = refVal?.ToString() ?? "";

                    string audioUrl = "";
                    if (msg.TryGetValue("audioUrl", out var audioVal))
                        audioUrl = audioVal?.ToString() ?? "";

                    string subUrl = "";
                    if (msg.TryGetValue("subUrl", out var subVal))
                        subUrl = subVal?.ToString() ?? "";

                    // Headers captured from the page's own request. CDNs such as
                    // video.twimg.com reject a playback whose Referer does not
                    // match the page that issued the request, so the exact
                    // values are forwarded instead of a guessed origin.
                    //
                    // No credentials: the extension never captures cookies, and
                    // none are accepted here, so nothing secret is written to
                    // disk. Media behind a login will not play from mpv.
                    string userAgent = "";
                    if (msg.TryGetValue("userAgent", out var uaVal))
                        userAgent = uaVal?.ToString() ?? "";

                    if (!string.IsNullOrWhiteSpace(url))
                    {
                        Log($"Message received, URL: {RedactUrl(url)} | Audio: {RedactUrl(audioUrl)} | Sub: {RedactUrl(subUrl)} | Referrer: {referrer} | UA: {userAgent}");
                        SendMessage(stdout, new { status = "ok", url });
                        ConfigService.AddHistoryStatic(url);
                        LaunchMpv(url, referrer, audioUrl, subUrl, userAgent);
                        System.Threading.Thread.Sleep(50);
                    }
                    else
                    {
                        SendMessage(stdout, new { status = "error", message = "Empty URL." });
                    }
                }
                else
                {
                    SendMessage(stdout, new { status = "error", message = "Invalid message." });
                }
            }
            catch (Exception ex)
            {
                Log($"ERROR: {ex}");
                try { SendMessage(stdout, new { status = "error", message = ex.Message }); } catch { }
            }

            Log("Native host exited.");
        }

        private static void Log(string msg)
        {
            try
            {
                // Rotate rather than append forever: the host runs once per
                // playback, so an unrotated log grows without bound.
                var file = new FileInfo(AppPaths.HostLogFile);
                if (file.Exists && file.Length > MaxLogBytes)
                {
                    string old = AppPaths.HostLogFile + ".1";
                    if (File.Exists(old)) File.Delete(old);
                    File.Move(AppPaths.HostLogFile, old);
                }
                File.AppendAllText(AppPaths.HostLogFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}\r\n");
            }
            catch { }
        }

        private static Dictionary<string, object?>? ReadMessage(Stream s)
        {
            byte[] lenBuf = new byte[4];
            int read = 0;
            while (read < 4)
            {
                int r = s.Read(lenBuf, read, 4 - read);
                if (r == 0) return null;
                read += r;
            }

            int length = BitConverter.ToInt32(lenBuf, 0);
            if (length <= 0 || length > 1_048_576) return null;

            byte[] body = new byte[length];
            read = 0;
            while (read < length)
            {
                int r = s.Read(body, read, length - read);
                if (r == 0) break;
                read += r;
            }

            return JsonSerializer.Deserialize<Dictionary<string, object?>>(
                Encoding.UTF8.GetString(body, 0, read));
        }

        private static void SendMessage(Stream s, object response)
        {
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(response);
            byte[] len = BitConverter.GetBytes(json.Length);
            s.Write(len, 0, 4);
            s.Write(json, 0, json.Length);
            s.Flush();
        }

        /// <summary>
        /// Rejects anything that is not plain web media.
        ///
        /// The URL reaches this process straight from a web page, and mpv will
        /// happily follow whatever scheme it is handed. Left unfiltered, a
        /// "\\\\host\\share\\clip.mp4" value makes mpv's HTTP client answer an
        /// NTLM challenge for a remote server - handing the user's network
        /// credentials to whoever chose that URL - and a file:// value opens
        /// arbitrary local paths. Only http(s) is accepted here; the GUI's own
        /// URL box keeps accepting local files because that is the user typing.
        /// </summary>
        private static bool TryValidateMediaUrl(string url, out string normalized)
        {
            normalized = "";
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;
            if (string.IsNullOrEmpty(uri.Host)) return false;
            normalized = uri.AbsoluteUri;
            return true;
        }

        /// <summary>
        /// Quotes one argument the way CommandLineToArgvW expects.
        ///
        /// Two properties of that parser drive the implementation. A quotation
        /// mark always ends the current argument and backslashes are ordinary
        /// characters in both quoted and unquoted text, so once '"' is removed
        /// no backslash handling is needed at all - the previous code that only
        /// deleted '"' left a value ending in '\' able to escape the closing
        /// quote and swallow the next argument, which is how a page-supplied
        /// URL could smuggle extra options into mpv. And a '"' cannot survive
        /// inside an argument no matter how it is escaped, so it is dropped:
        /// media URLs are normalised through Uri.AbsoluteUri first, which
        /// already percent-encodes it as %22.
        /// </summary>
        private static string QuoteArgument(string arg)
        {
            string value = arg.Replace("\"", "");
            if (value.Length == 0) return "\"\"";
            if (value.IndexOfAny(new[] { ' ', '\t' }) < 0) return value;
            return "\"" + value + "\"";
        }

        /// <summary>
        /// Splits a user-supplied option string (config "ExtraArgs") into tokens,
        /// honouring double quotes so a value containing spaces stays one token.
        /// </summary>
        private static List<string> SplitArgs(string commandLine)
        {
            var result = new List<string>();
            var current = new StringBuilder();
            bool inQuotes = false, any = false;

            foreach (char c in commandLine)
            {
                if (c == '"') { inQuotes = !inQuotes; any = true; continue; }
                if (!inQuotes && char.IsWhiteSpace(c))
                {
                    if (any) { result.Add(current.ToString()); current.Clear(); any = false; }
                    continue;
                }
                current.Append(c);
                any = true;
            }
            if (any) result.Add(current.ToString());
            return result;
        }

        /// <summary>
        /// Builds the Referer options, or nothing at all.
        ///
        /// <paramref name="referrer"/> is used verbatim and only when it is
        /// non-empty. Nothing is invented here.
        ///
        /// An earlier version replaced a cross-site referrer with the media
        /// host's own origin, to satisfy CDNs that allowlist their own domain
        /// (video.twimg.com answers 403 for a referrer from anywhere else).
        /// That guess breaks the opposite case: for a page URL that mpv hands to
        /// yt-dlp, a made-up origin makes the extractor refuse the URL outright
        /// - yt-dlp answers "Unsupported URL" for
        /// https://vidmoly.biz/embed-xxx.html when given --referer
        /// https://vidmoly.biz/, while the same URL works with no referrer or
        /// with the referrer the embedding site really used.
        ///
        /// So the rule is the faithful one: forward the Referer the browser
        /// actually sent, and send nothing when it sent nothing. The caller is
        /// responsible for passing only a header value it observed.
        /// </summary>
        private static List<string> BuildReferrerArgs(string referrer)
        {
            var outArgs = new List<string>();
            if (string.IsNullOrWhiteSpace(referrer)) return outArgs;

            string clean = referrer.Trim();
            if (!clean.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return outArgs;

            // One token each, unquoted here: QuoteArgument does the escaping,
            // so a referrer can never be read as a separate mpv option.
            outArgs.Add($"--referrer={clean}");
            outArgs.Add($"--ytdl-raw-options=referer={clean}");
            return outArgs;
        }

        private static void LaunchMpv(string url, string? referrer = null, string? audioUrl = null,
                                      string? subUrl = null, string? userAgent = null)
        {
            string? mpv = FindMpvPath();
            Log($"launch_mpv url: {RedactUrl(url)} | Referrer: {referrer} | MPV: {mpv}");

            if (string.IsNullOrEmpty(mpv) || !File.Exists(mpv))
            {
                Log($"ERROR: MPV not found: {mpv}");
                return;
            }

            string geometry = "960x540";
            string ontopArg = "--ontop";
            string forceWindowArg = "--force-window=immediate";
            string profileArg = "";
            string extraArgs = "";

            try
            {
                if (File.Exists(AppPaths.ConfigFile))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(AppPaths.ConfigFile));
                    var root = doc.RootElement;
                    if (root.TryGetProperty("Geometry", out var g) && !string.IsNullOrWhiteSpace(g.GetString()))
                        geometry = g.GetString()!;
                    if (root.TryGetProperty("AlwaysOnTop", out var ont) && !ont.GetBoolean())
                        ontopArg = "";
                    if (root.TryGetProperty("ForceWindow", out var fw))
                    {
                        string? fwStr = fw.ValueKind == JsonValueKind.String ? fw.GetString() : (fw.ValueKind == JsonValueKind.True ? "yes" : (fw.ValueKind == JsonValueKind.False ? "no" : null));
                        if (!string.IsNullOrWhiteSpace(fwStr))
                        {
                            if (fwStr.Equals("no", StringComparison.OrdinalIgnoreCase))
                                forceWindowArg = "--force-window=no";
                            else if (fwStr.Equals("yes", StringComparison.OrdinalIgnoreCase))
                                forceWindowArg = "--force-window=yes";
                            else
                                forceWindowArg = "--force-window=immediate";
                        }
                    }
                    if (root.TryGetProperty("MpvProfile", out var prof) && !string.IsNullOrWhiteSpace(prof.GetString()))
                        profileArg = $"--profile={prof.GetString()!.Trim()}";
                    if (root.TryGetProperty("ExtraArgs", out var extra) && !string.IsNullOrWhiteSpace(extra.GetString()))
                        extraArgs = extra.GetString()!.Trim();
                }
            }
            catch { }

            // The URL arrives from a web page, so it is validated and every
            // argument is tokenised before anything is formatted into a command
            // line. Building one interpolated string let a page inject extra
            // mpv options - and mpv's --script executes code.
            if (!TryValidateMediaUrl(url, out string safeUrl))
            {
                Log($"ERROR: refusing non-http(s) media URL: {RedactUrl(url)}");
                return;
            }

            var args = new List<string>();
            if (!string.IsNullOrWhiteSpace(forceWindowArg)) args.Add(forceWindowArg);
            if (!string.IsNullOrWhiteSpace(ontopArg)) args.Add(ontopArg);
            if (!string.IsNullOrWhiteSpace(geometry)) args.Add($"--geometry={geometry.Trim()}");
            if (!string.IsNullOrWhiteSpace(profileArg)) args.Add(profileArg.Trim());
            if (!string.IsNullOrWhiteSpace(extraArgs)) args.AddRange(SplitArgs(extraArgs));
            if (!string.IsNullOrWhiteSpace(audioUrl)) args.Add($"--audio-file={audioUrl.Trim()}");
            if (!string.IsNullOrWhiteSpace(subUrl)) args.Add($"--sub-file={subUrl.Trim()}");

            // Prefer the browser's own User-Agent: some CDNs fingerprint the
            // client and reject a hardcoded one that disagrees with the request.
            string ua = !string.IsNullOrWhiteSpace(userAgent)
                ? userAgent.Trim()
                : "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";
            args.Add($"--user-agent={ua}");

            // Only what the browser actually sent. An empty referrer means no
            // --referrer argument at all, which is what several sites need: a
            // made-up origin makes yt-dlp reject the URL outright.
            args.AddRange(BuildReferrerArgs(referrer ?? ""));

            args.Add(safeUrl);
            Log("launch_args: " + string.Join(" ", args.Select(RedactArg)));

            string safeExe = mpv;
            string cmdLine = QuoteArgument(safeExe) + " " +
                             string.Join(" ", args.Select(QuoteArgument));

            // 1. WMI Win32_Process.Create - starts the process fully detached
            //    inside the interactive session, so the mpv window appears.
            try
            {
                Type? locatorType = Type.GetTypeFromProgID("WbemScripting.SWbemLocator");
                if (locatorType != null)
                {
                    dynamic? locator = Activator.CreateInstance(locatorType);
                    if (locator != null)
                    {
                        dynamic service = locator.ConnectServer(".", @"root\cimv2");
                        dynamic processClass = service.Get("Win32_Process");
                        int returnVal = (int)processClass.Create(cmdLine);
                        Log($"WMI Win32_Process.Create result: {returnVal}");
                        if (returnVal == 0)
                        {
                            Log("Started via WMI.");
                            return;
                        }
                    }
                }
            }
            catch (Exception exWmi)
            {
                Log($"WMI error: {exWmi.Message}");
            }

            // 2. Fallback: shell execute, so the window is shown normally.
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = safeExe,
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(safeExe) ?? ""
                };
                foreach (string a in args) psi.ArgumentList.Add(a);
                Process.Start(psi);
                Log("Started MPV via Process.Start (UseShellExecute).");
                return;
            }
            catch (Exception exDirect)
            {
                Log($"Direct start failed: {exDirect.Message}");
            }

            // 3. Last resort: start detached without a shell. cmd.exe is
            //    deliberately not used - a page-supplied URL containing "&" or
            //    "|" would be executed as a shell command there.
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = safeExe,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(safeExe) ?? ""
                };
                foreach (string a in args) psi.ArgumentList.Add(a);
                Process.Start(psi);
                Log("Started via Process.Start (detached).");
            }
            catch (Exception exProc)
            {
                Log($"Launch error: {exProc.Message}");
            }
        }

        /// <summary>
        /// Redacts a single argument for the log, leaving option names readable
        /// so a failing launch can still be diagnosed.
        /// </summary>
        private static string RedactArg(string arg)
        {
            if (string.IsNullOrEmpty(arg)) return arg;
            int eq = arg.IndexOf('=');
            if (arg.Contains("://"))
                return eq >= 0 ? arg[..(eq + 1)] + RedactUrl(arg[(eq + 1)..]) : RedactUrl(arg);
            return arg;
        }

        private static string? FindMpvPath()
        {
            try
            {
                if (File.Exists(AppPaths.ConfigFile))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(AppPaths.ConfigFile));
                    if (doc.RootElement.TryGetProperty("MpvPath", out var p))
                    {
                        string? v = p.GetString();
                        if (!string.IsNullOrEmpty(v) && File.Exists(v)) return v;
                    }
                }
            }
            catch { }

            var commonPaths = new[]
            {
                AppPaths.MpvExe,
                @"D:\Prog\mpv\mpv.exe",
                @"C:\mpv\mpv.exe",
                @"D:\mpv\mpv.exe",
                @"C:\Program Files\mpv\mpv.exe",
                @"C:\Program Files (x86)\mpv\mpv.exe"
            };

            foreach (var p in commonPaths)
            {
                if (File.Exists(p)) return p;
            }

            string? pathEnv = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrEmpty(pathEnv))
            {
                foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                {
                    try
                    {
                        string candidate = Path.Combine(dir.Trim(), "mpv.exe");
                        if (File.Exists(candidate)) return candidate;
                    }
                    catch { }
                }
            }

            return null;
        }

        private static string? GetParentProcessName()
        {
            try
            {
                int pid = Environment.ProcessId;
                int parentPid = 0;

                IntPtr snap = CreateToolhelp32Snapshot(0x2, 0);
                if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return null;

                try
                {
                    var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
                    if (!Process32First(snap, ref entry)) return null;
                    do
                    {
                        if (entry.th32ProcessID == (uint)pid)
                        {
                            parentPid = (int)entry.th32ParentProcessID;
                            break;
                        }
                    } while (Process32Next(snap, ref entry));
                }
                finally
                {
                    CloseHandle(snap);
                }

                if (parentPid <= 0) return null;
                using var parent = Process.GetProcessById(parentPid);
                return parent.ProcessName;
            }
            catch
            {
                return null;
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct PROCESSENTRY32
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }
    }
}
