using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

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
            // Plan A: the relay fetches through the viewer's own tab instead of
            // opening a browser of its own, so the host has to answer requests
            // while mpv is running. The streams are kept here for that bridge.
            BrokerStdin = stdin;
            BrokerStdout = stdout;

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

                    // The tab title, which is the site's title rather than the
                    // media URL's. Written to history so the row reads like the
                    // page it came from, and handed to mpv so the window says
                    // the same thing as the browser tab instead of a signed CDN
                    // path full of tokens.
                    string pageTitle = "";
                    if (msg.TryGetValue("title", out var titleVal))
                        pageTitle = (titleVal?.ToString() ?? "").Trim();

                    // "resolve" marks a stream the page's own player already
                    // fetched. Those tokens are usually single use, so the relay
                    // is given the page that produced it and renders that page
                    // itself instead of replaying a spent URL.
                    string kind = "";
                    if (msg.TryGetValue("kind", out var kindVal))
                        kind = (kindVal?.ToString() ?? "").Trim();

                    string pageUrl = "";
                    if (msg.TryGetValue("pageUrl", out var pageVal))
                        pageUrl = (pageVal?.ToString() ?? "").Trim();

                    // Counts only, never URLs: tells the log whether the tab had
                    // any candidate when the pick came back empty.
                    string pick = "";
                    if (msg.TryGetValue("pick", out var pickVal))
                        pick = (pickVal?.ToString() ?? "").Trim();

                    if (!string.IsNullOrWhiteSpace(url))
                    {
                        Log($"Message received, URL: {RedactUrl(url)} | Audio: {RedactUrl(audioUrl)} | Sub: {RedactUrl(subUrl)} | Referrer: {referrer} | UA: {userAgent} | Title: {pageTitle} | Kind: {kind} | Pick: {pick}");

                        bool handled = false;
                        if (RelayRouting.ForExtension(url, kind, ReadRelayEnabled(), ReadRelayBlacklist()) == MediaRoute.Relay)
                        {
                            string target = !string.IsNullOrWhiteSpace(pageUrl) ? pageUrl : url;
                            // `url` is the stream the page asked for; `target` is
                            // the page itself. Plan A needs both.
                            string streamCandidate = string.IsNullOrWhiteSpace(kind) ? "" : url;
                            if (TryPlayWithRelay(target, referrer, pageTitle, streamCandidate, audioUrl, subUrl, out string? relayError))
                            {
                                SendMessage(stdout, new { status = "ok", url });
                                ConfigService.AddHistoryStatic(target, pageTitle);
                                System.Threading.Thread.Sleep(50);
                                handled = true;
                            }
                            else
                            {
                                // The route only sends non-media URLs here, so a
                                // direct launch could do nothing but open an empty
                                // window. Report the reason instead of pretending
                                // the link was playable.
                                Log($"Relay failed ({relayError}); not launching mpv.");
                                SendMessage(stdout, new { status = "error", message = relayError ?? "relay_failed" });
                                handled = true;
                            }
                        }

                        if (!handled)
                        {
                            SendMessage(stdout, new { status = "ok", url });
                            ConfigService.AddHistoryStatic(url, pageTitle);
                            LaunchMpv(url, referrer, audioUrl, subUrl, userAgent, pageTitle);
                            System.Threading.Thread.Sleep(50);
                        }
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

        /// <summary>
        /// Sites the viewer excluded from the relay. Read from the config file for
        /// the same reason as the switch above, and falling back to the built-in
        /// defaults when the key is absent so an older config never relays them.
        /// </summary>
        private static IReadOnlyList<string> ReadRelayBlacklist()
        {
            try
            {
                if (!File.Exists(AppPaths.ConfigFile)) return RelayRouting.DefaultBlacklist;
                using var doc = JsonDocument.Parse(File.ReadAllText(AppPaths.ConfigFile));
                if (!doc.RootElement.TryGetProperty("RelayBlacklist", out var value)
                    || value.ValueKind != JsonValueKind.Array)
                {
                    return RelayRouting.DefaultBlacklist;
                }
                var hosts = new List<string>();
                foreach (var item in value.EnumerateArray())
                {
                    string entry = (item.GetString() ?? "").Trim();
                    if (entry.Length > 0) hosts.Add(entry);
                }
                return hosts;
            }
            catch
            {
                return RelayRouting.DefaultBlacklist;
            }
        }
        /// <summary>
        /// Whether the optional browser relay is switched on. Read straight from
        /// the config file because the native host is a short-lived process that
        /// has no ConfigService instance.
        /// </summary>
        private static bool ReadRelayEnabled()
        {
            try
            {
                if (!File.Exists(AppPaths.ConfigFile)) return false;
                using var doc = JsonDocument.Parse(File.ReadAllText(AppPaths.ConfigFile));
                return doc.RootElement.TryGetProperty("RelayEnabled", out var value)
                       && value.ValueKind == JsonValueKind.True;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Renders the page in the relay helper and starts mpv on the local
        /// playlist it serves. Returns false with a machine readable code so the
        /// caller can fall back to a direct launch instead of failing outright.
        /// </summary>
        private static bool TryPlayWithRelay(string pageUrl, string referrer, string pageTitle,
                                             string streamUrl, string audioUrl, string subUrl,
                                             out string? errorCode)
        {
            errorCode = null;

            if (!TryValidateMediaUrl(pageUrl, out string safePage))
            {
                errorCode = "relay_invalid_url";
                return false;
            }

            // Plan A: when the extension hands over the captured stream URL, the
            // helper never opens a browser. It asks this process for every byte
            // and this process asks the tab the viewer already has open, so the
            // site sees the same client it always saw.
            string safeStream = "";
            bool brokered = BrokerStdout != null
                && !string.IsNullOrWhiteSpace(streamUrl)
                && TryValidateMediaUrl(streamUrl, out safeStream);

            // Without a captured stream there is nothing the viewer's tab could
            // fetch, and the helper must not open a browser of its own: that is
            // exactly what the site's bot wall blocked. Refuse and let the page
            // ask the extension for a stream first.
            if (BrokerStdout != null && !brokered)
            {
                errorCode = "relay_no_stream";
                return false;
            }

            RelaySession session;
            try
            {
                if (brokered)
                {
                    Log("Relay broker mode: fetching through the viewer's own tab.");
                    var fetcher = new BridgeFetcher();
                    _brokerFetcher = fetcher;
                    EnsureReplyPump();
                    session = RelaySessionService.StartAsync(
                            safePage,
                            pageTitle,
                            progress: null,
                            headless: true,
                            CancellationToken.None,
                            detach: true,
                            streamUrl: safeStream,
                            fetcher: fetcher)
                        .GetAwaiter()
                        .GetResult();
                }
                else
                {
                    // Detached: this process exits as soon as mpv is running, and
                    // the helper then watches mpv instead of its parent.
                    session = RelaySessionService.StartAsync(
                            safePage,
                            pageTitle,
                            progress: null,
                            headless: true,
                            CancellationToken.None,
                            detach: true)
                        .GetAwaiter()
                        .GetResult();
                }
            }
            catch (RelayException ex)
            {
                _brokerFetcher = null;
                errorCode = ex.Code;
                return false;
            }
            catch (Exception ex)
            {
                _brokerFetcher = null;
                Log($"relay start failed: {ex.GetType().Name}");
                errorCode = "relay_helper_failed";
                return false;
            }

            if (brokered)
            {
                _brokerSession = session;
                if (_brokerFetcher != null)
                {
                    _ = session.PumpAsync(_brokerFetcher, CancellationToken.None);
                }

                // The helper exits when mpv does. Keep this host alive only that
                // long, so the browser port is closed with the player.
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await session.WaitForExitAsync().ConfigureAwait(false);
                    }
                    catch
                    {
                        // A dead process is exactly what is being waited for.
                    }
                    Environment.Exit(0);
                });
            }

            Log($"Relay ready for {RedactUrl(safePage)}");

            // The page is the referrer now: the helper's own fetches carry it,
            // and mpv only ever sees the loopback URL.
            Process? mpv = LaunchMpv(session.LocalUrl, session.PageUrl, audioUrl, subUrl, pageTitle: pageTitle);
            if (mpv == null)
            {
                session.Dispose();
                errorCode = "relay_mpv_failed";
                return false;
            }

            session.AttachMpv(mpv.Id);
            return true;
        }

        /// <summary>The native-messaging streams, kept for the brokered relay.</summary>
        private static Stream? BrokerStdin;
        private static Stream? BrokerStdout;

        /// <summary>The live brokered session and its fetcher, when one is running.</summary>
        private static RelaySession? _brokerSession;
        private static BridgeFetcher? _brokerFetcher;
        private static int _replyPumpStarted;

        /// <summary>
        /// Carries the helper's fetch requests to the extension and its answers
        /// back. The helper asks for a URL, this writes one native message to the
        /// open port, and the extension fetches it inside the page it is already
        /// on - the only context the site accepts.
        /// </summary>
        private sealed class BridgeFetcher : IRelayFetcher
        {
            private readonly object _gate = new object();
            private readonly Dictionary<int, TaskCompletionSource<RelayFetch>> _pending = new Dictionary<int, TaskCompletionSource<RelayFetch>>();

            public bool HasPending
            {
                get { lock (_gate) return _pending.Count > 0; }
            }

            public Task<RelayFetch> FetchAsync(int id, string url,
                                               IReadOnlyDictionary<string, string> headers,
                                               CancellationToken cancellationToken)
            {
                var waiter = new TaskCompletionSource<RelayFetch>(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (_gate) _pending[id] = waiter;

                Stream? output = BrokerStdout;
                if (output == null)
                {
                    return Task.FromResult(new RelayFetch(0, "", "", "port_closed"));
                }

                try
                {
                    SendMessage(output, new { type = "fetch", id, url });
                }
                catch (Exception error)
                {
                    lock (_gate) _pending.Remove(id);
                    return Task.FromResult(new RelayFetch(0, "", "", error.Message));
                }

                if (cancellationToken.CanBeCanceled)
                {
                    cancellationToken.Register(() =>
                    {
                        lock (_gate)
                        {
                            if (_pending.Remove(id))
                                waiter.TrySetResult(new RelayFetch(0, "", "", "cancelled"));
                        }
                    });
                }

                return waiter.Task;
            }

            public void Complete(Dictionary<string, object?> message)
            {
                int id = ReadInt(message, "id");
                if (id <= 0) return;

                TaskCompletionSource<RelayFetch>? waiter;
                lock (_gate)
                {
                    if (!_pending.TryGetValue(id, out waiter)) return;
                    _pending.Remove(id);
                }

                var reply = new RelayFetch(
                    ReadInt(message, "status"),
                    ReadString(message, "contentType"),
                    ReadString(message, "body"),
                    ReadString(message, "error"));
                waiter.TrySetResult(reply);
            }

            public void FailAll(string error)
            {
                List<TaskCompletionSource<RelayFetch>> waiters;
                lock (_gate)
                {
                    waiters = new List<TaskCompletionSource<RelayFetch>>(_pending.Values);
                    _pending.Clear();
                }
                foreach (var waiter in waiters)
                    waiter.TrySetResult(new RelayFetch(0, "", "", error));
            }

            private static int ReadInt(Dictionary<string, object?> message, string name)
            {
                if (!message.TryGetValue(name, out var value) || value == null) return 0;
                if (value is int number) return number;
                return int.TryParse(value.ToString(), out int parsed) ? parsed : 0;
            }

            private static string ReadString(Dictionary<string, object?> message, string name)
            {
                if (!message.TryGetValue(name, out var value) || value == null) return "";
                return value.ToString() ?? "";
            }
        }

        /// <summary>
        /// Reads the extension's replies for the whole life of the relay. Started
        /// before the helper starts, because the helper's very first fetch is the
        /// master playlist and it blocks until this loop answers it.
        /// </summary>
        private static void PumpBrokerReplies()
        {
            Stream? input = BrokerStdin;
            if (input == null) return;

            try
            {
                while (true)
                {
                    var message = ReadMessage(input);
                    if (message == null) break;

                    if (message.TryGetValue("cmd", out var cmd)
                        && string.Equals(cmd?.ToString(), "fetch", StringComparison.Ordinal))
                    {
                        _brokerFetcher?.Complete(message);
                    }
                    else if (message.TryGetValue("type", out var kind)
                             && string.Equals(kind?.ToString(), "diag", StringComparison.Ordinal))
                    {
                        // The extension narrates each brokered fetch here, so a
                        // fetch that never comes back leaves a trail in host.log
                        // instead of one silent 60 s timeout.
                        Log("relay diag: " + (message.TryGetValue("text", out var text) ? text?.ToString() : ""));
                    }
                }
            }
            catch (Exception error)
            {
                Log($"broker reply loop ended: {error.GetType().Name}");
            }

            // The page went away: every waiting fetch has to fail now or the
            // helper would sit on a request that can never be answered.
            _brokerFetcher?.FailAll("page_closed");
        }

        private static void EnsureReplyPump()
        {
            if (Interlocked.Exchange(ref _replyPumpStarted, 1) != 0) return;
            // Foreground on purpose: this thread is what keeps the host process
            // alive while mpv plays, so the port stays open for fetches.
            var thread = new Thread(PumpBrokerReplies)
            {
                IsBackground = false,
                Name = "mpv-relay-broker",
            };
            thread.Start();
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

        private static readonly object SendGate = new object();

        private static void SendMessage(Stream s, object response)
        {
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(response);
            byte[] len = BitConverter.GetBytes(json.Length);
            // Two threads write here: the request handler sends the status, and
            // the relay's fetcher answers the helper mid-playback. Chrome reads
            // one length-prefixed frame at a time, so the framing needs a lock.
            lock (SendGate)
            {
                s.Write(len, 0, 4);
                s.Write(json, 0, json.Length);
                s.Flush();
            }
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
            // The browser extension is never a torrent entry point, even when
            // the feature is enabled in the app. A page can hand us a .torrent
            // URL that looks like ordinary media, so the shape is rejected here
            // before any scheme check would let it through.
            if (TorrentLaunchPolicy.IsRejectedFromBrowser(url)) return false;
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

        private static Process? LaunchMpv(string url, string? referrer = null, string? audioUrl = null,
                                          string? subUrl = null, string? userAgent = null,
                                          string? pageTitle = null)
        {
            string? mpv = FindMpvPath();
            Log($"launch_mpv url: {RedactUrl(url)} | Referrer: {referrer} | MPV: {mpv}");

            if (string.IsNullOrEmpty(mpv) || !File.Exists(mpv))
            {
                Log($"ERROR: MPV not found: {mpv}");
                return null;
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
                return null;
            }

            // The browser's own User-Agent when the extension captured one: some
            // CDNs fingerprint the client and reject a hardcoded value. The page
            // walk uses the same string, because a wrapper behind a WAF can
            // answer a different page (or 403) for every other UA.
            string ua = !string.IsNullOrWhiteSpace(userAgent)
                ? userAgent.Trim()
                : MediaPageResolver.BrowserUserAgent;

            // What the popup offered may be a player page rather than a stream:
            // the site wraps vidmoly and friends in one or two pages before an
            // .m3u8 appears. Follow that chain here, once, so mpv is handed the
            // file it can actually open. Nothing resolvable means the original
            // URL goes through unchanged, exactly as before.
            string? resolved = MediaPageResolver.Resolve(safeUrl, referrer, ua);
            if (!string.IsNullOrWhiteSpace(resolved) &&
                !string.Equals(resolved, safeUrl, StringComparison.Ordinal) &&
                TryValidateMediaUrl(resolved, out string resolvedUrl))
            {
                Log($"Resolved player page to media: {RedactUrl(resolvedUrl)}");
                safeUrl = resolvedUrl;
            }

            var args = new List<string>();
            if (!string.IsNullOrWhiteSpace(forceWindowArg)) args.Add(forceWindowArg);
            if (!string.IsNullOrWhiteSpace(ontopArg)) args.Add(ontopArg);
            if (!string.IsNullOrWhiteSpace(geometry)) args.Add($"--geometry={geometry.Trim()}");
            if (!string.IsNullOrWhiteSpace(profileArg)) args.Add(profileArg.Trim());
            if (!string.IsNullOrWhiteSpace(extraArgs)) args.AddRange(CommandLineTokenize.Split(extraArgs));
            if (!string.IsNullOrWhiteSpace(audioUrl)) args.Add($"--audio-file={audioUrl.Trim()}");
            if (!string.IsNullOrWhiteSpace(subUrl)) args.Add($"--sub-file={subUrl.Trim()}");

            // Prefer the browser's own User-Agent: some CDNs fingerprint the
            // client and reject a hardcoded one that disagrees with the request.
            args.Add($"--user-agent={ua}");

            // Only what the browser actually sent. An empty referrer means no
            // --referrer argument at all, which is what several sites need: a
            // made-up origin makes yt-dlp reject the URL outright.
            args.AddRange(BuildReferrerArgs(referrer ?? ""));

            // The relay serves playlists and segments from extensionless loopback
            // paths. mpv's HLS demuxer trusts the extension by default and then
            // refuses a segment it cannot name, so both checks are relaxed - but
            // only for the relay's own URL, never for a link from the web.
            if (Uri.TryCreate(safeUrl, UriKind.Absolute, out Uri? relayUrl) && relayUrl.IsLoopback)
            {
                args.Add("--demuxer-lavf-o=allowed_segment_extensions=ALL,extension_picky=0");
            }

            // The tab title, if the extension sent one. mpv otherwise shows the
            // last segment of the media URL, which on a signed CDN link is a
            // token, not a name. Both options are set so the window title and
            // the on-screen title agree.
            string? windowTitle = NormaliseTitle(pageTitle);
            if (windowTitle != null)
            {
                args.Add($"--force-media-title={windowTitle}");
                args.Add($"--title={windowTitle}");
            }

            args.Add(safeUrl);
            Log("launch_args: " + string.Join(" ", args.Select(RedactArg)));

            string safeExe = mpv;
            string cmdLine = QuoteArgument(safeExe) + " " +
                             string.Join(" ", args.Select(QuoteArgument));

            // 1. WMI Win32_Process.Create - starts the process fully detached
            //    inside the interactive session, so the mpv window appears.
            //    The relay path needs the pid (the helper watches the process),
            //    and this is the only strategy that can hand it back, so return
            //    it as early as possible.
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
                        object?[] inParams = { cmdLine, null, null };
                        int returnVal = (int)processClass.Create(
                            cmdLine, null, null, out object? processIdObj);
                        Log($"WMI Win32_Process.Create result: {returnVal}");
                        if (returnVal == 0)
                        {
                            Log("Started via WMI.");
                            try
                            {
                                uint pid = Convert.ToUInt32(processIdObj);
                                if (pid > 0) return Process.GetProcessById((int)pid);
                            }
                            catch (Exception exPid)
                            {
                                Log($"WMI pid lookup failed: {exPid.Message}");
                            }
                            return null;
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
                var started = Process.Start(psi);
                Log("Started MPV via Process.Start (UseShellExecute).");
                return started;
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
                var started = Process.Start(psi);
                Log("Started via Process.Start (detached).");
                return started;
            }
            catch (Exception exProc)
            {
                Log($"Launch error: {exProc.Message}");
            }

            return null;
        }

        /// <summary>
        /// A tab title mpv can be given on the command line: one line, no
        /// control characters, bounded length. Null when nothing is left.
        /// </summary>
        private static string? NormaliseTitle(string? title)
        {
            if (string.IsNullOrWhiteSpace(title)) return null;

            var sb = new StringBuilder(title.Length);
            foreach (char c in title)
            {
                // Newlines would split the title across the window and the log,
                // and other control characters are not text at all.
                sb.Append(char.IsControl(c) ? ' ' : c);
            }

            string clean = string.Join(" ", sb.ToString()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

            if (clean.Length == 0) return null;
            if (clean.Length > 200) clean = clean[..197].TrimEnd() + "...";
            return clean;
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

        /// <summary>
        /// The mpv to launch. Same list, same order as the window uses: the
        /// two used to disagree, and a user with mpv in C:\mpv was served by
        /// the extension and told "not found" by the app.
        /// </summary>
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

            return MpvLocator.Resolve(
                configuredPath: null,
                layout: new AppLayout(AppPaths.Root),
                exists: File.Exists);
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
