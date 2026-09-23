using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace MpvLauncher.Gui.Services
{
    /// <summary>
    /// Native messaging host: [4-byte LE length][UTF-8 JSON].
    /// Chromium passes chrome-extension://… ; Firefox passes the manifest JSON path.
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

        public static bool IsHostMode(string[] args)
        {
            bool hasHostArg = false;
            if (args != null)
            {
                foreach (var raw in args)
                {
                    if (string.IsNullOrWhiteSpace(raw)) continue;
                    string a = raw.Trim().Trim('"');

                    if (a.Equals("--host", StringComparison.OrdinalIgnoreCase)) return true;
                    if (a.Equals("--native-host", StringComparison.OrdinalIgnoreCase)) return true;
                    if (a.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase)) return true;
                    if (a.StartsWith("moz-extension://", StringComparison.OrdinalIgnoreCase)) return true;
                    if (a.Contains("com.mpv.launcher", StringComparison.OrdinalIgnoreCase)) return true;

                    if (LooksLikeNativeManifest(a))
                    {
                        hasHostArg = true;
                        return true;
                    }
                }
            }

            string? parent = GetParentProcessName();
            bool parentIsBrowser = parent != null && IsBrowserName(parent);

            bool redirected = false;
            try { redirected = Console.IsInputRedirected; } catch { }

            // Firefox native messaging: stdin is a pipe, parent is firefox, argv is the manifest path.
            // Do not treat "opened from browser downloads" (no args, stdin not redirected) as host mode.
            if (parentIsBrowser && (redirected || hasHostArg))
                return true;

            return false;
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

        private static bool IsBrowserName(string name)
        {
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

                    if (!string.IsNullOrWhiteSpace(url))
                    {
                        Log($"Message received, URL: {url} | Referrer: {referrer}");
                        SendMessage(stdout, new { status = "ok", url });
                        ConfigService.AddHistoryStatic(url);
                        LaunchMpv(url, referrer);
                        System.Threading.Thread.Sleep(1000);
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
            try { File.AppendAllText(AppPaths.HostLogFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}\r\n"); } catch { }
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

        private static void LaunchMpv(string url, string? referrer = null)
        {
            string? mpv = FindMpvPath();
            Log($"launch_mpv url: {url} | Referrer: {referrer} | MPV: {mpv}");

            if (string.IsNullOrEmpty(mpv) || !File.Exists(mpv))
            {
                Log($"ERROR: MPV not found: {mpv}");
                return;
            }

            string geometry = "960x540";
            string ontopArg = "--ontop";
            try
            {
                if (File.Exists(AppPaths.ConfigFile))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(AppPaths.ConfigFile));
                    if (doc.RootElement.TryGetProperty("Geometry", out var g) && !string.IsNullOrWhiteSpace(g.GetString()))
                        geometry = g.GetString()!;
                    if (doc.RootElement.TryGetProperty("AlwaysOnTop", out var ont) && !ont.GetBoolean())
                        ontopArg = "";
                }
            }
            catch { }

            string refArg = "";
            string refUrl = !string.IsNullOrWhiteSpace(referrer) ? referrer : (url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? url : "");
            if (!string.IsNullOrWhiteSpace(refUrl))
            {
                try
                {
                    var uri = new Uri(refUrl);
                    string origin = $"{uri.Scheme}://{uri.Authority}/";
                    refArg = $"--referrer=\"{origin}\" --ytdl-raw-options=\"referer={origin}\" ";
                }
                catch { }
            }

            const string ua = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";
            string mpvArgs = $"--force-window=yes {ontopArg} --geometry={geometry} --user-agent=\"{ua}\" {refArg}".Trim();
            string safeUrl = url.Replace("\"", "");
            string safeExe = mpv.Replace("\"", "");

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
                        string cmdLine = $"\"{safeExe}\" {mpvArgs} \"{safeUrl}\"";
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

            try
            {
                Type? shellType = Type.GetTypeFromProgID("Shell.Application");
                if (shellType != null)
                {
                    dynamic? shell = Activator.CreateInstance(shellType);
                    if (shell != null)
                    {
                        shell.ShellExecute(mpv, $"{mpvArgs} \"{url}\"", Path.GetDirectoryName(mpv) ?? "", "open", 1);
                        Log("Started via Shell.Application.");
                        return;
                    }
                }
            }
            catch (Exception exShell)
            {
                Log($"Shell.Application error: {exShell.Message}");
            }

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c start \"\" \"{mpv}\" {mpvArgs} \"{url}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                Process.Start(psi);
                Log("Started via cmd.exe start.");
            }
            catch (Exception exProc)
            {
                Log($"cmd.exe error: {exProc.Message}");
            }
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
