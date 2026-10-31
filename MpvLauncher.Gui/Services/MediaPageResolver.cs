// Original implementation, MIT licensed.

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace MpvLauncher.Gui.Services
{
    /// <summary>
    /// Turns a player page into the stream it actually plays.
    ///
    /// Streaming sites rarely link a media file. What they put in an iframe, and
    /// what the extension therefore offers as a row, is a *page*: a wrapper that
    /// loads a second page, which finally names an .m3u8. mpv cannot open any of
    /// those, so the row looked fine and did nothing.
    ///
    /// This walks that chain before mpv is started: fetch the page, look for a
    /// media URL, otherwise follow its iframe or script redirect, up to three
    /// hops. One of the wrappers met in the wild hides its iframe in
    /// <c>document.write(atob(unescape("..."))))</c>, so those payloads are
    /// decoded too - otherwise the chain stops at the first page.
    ///
    /// Everything here is best effort and bounded: a failed lookup returns null
    /// and the original URL is handed to mpv unchanged, which is exactly what
    /// happened before. Nothing is cached, because playlist links are usually
    /// signed and expire.
    /// </summary>
    public static class MediaPageResolver
    {
        /// <summary>
        /// The UA the walk presents when the message carried none. Kept current
        /// on purpose: wrappers behind a WAF refuse older Chrome versions, and
        /// the same string is handed to mpv so page and stream agree.
        /// </summary>
        public const string BrowserUserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Safari/537.36";

        /// <summary>Wrapper pages seen in practice are two deep; three leaves room.</summary>
        private const int MaxDepth = 3;

        /// <summary>Enough for a player page, small enough not to pull a video.</summary>
        private const int MaxBytes = 512 * 1024;

        private static readonly string[] MediaExtensions =
        {
            ".m3u8", ".mpd", ".mp4", ".m4v", ".webm", ".mkv", ".flv", ".mov"
        };

        /// <summary>
        /// A URL-shaped token that ends in a media extension. Relative paths are
        /// matched too, because a page may point at /hls/master.m3u8.
        /// </summary>
        private static readonly Regex MediaCandidate = new(
            @"[^\s""'<>\\]+?\.(?:m3u8|mpd|mp4|m4v|webm|mkv|flv|mov)(?:\?[^\s""'<>\\]*)?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>An iframe, frame or embed source: the next page in the chain.</summary>
        private static readonly Regex FrameSource = new(
            @"<(?:iframe|frame|embed)\b[^>]*?\bsrc\s*=\s*[""'](?<url>[^""']+)[""']",
            RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant |
            RegexOptions.Singleline);

        /// <summary>A script redirect, which some wrappers use instead of an iframe.</summary>
        private static readonly Regex ScriptRedirect = new(
            @"(?:location(?:\.href)?|location\.replace|location\.assign)\s*(?:=|\()\s*[""'](?<url>https?://[^""']+)[""']",
            RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>The obfuscated wrapper: document.write(atob(unescape("..."))).</summary>
        private static readonly Regex AtobPayload = new(
            @"atob\s*\(\s*unescape\s*\(\s*[""'](?<payload>[^""']+)[""']\s*\)\s*\)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            var client = new HttpClient(new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                AllowAutoRedirect = true
            })
            {
                Timeout = TimeSpan.FromSeconds(8)
            };

            // A browser UA, because several wrappers answer 403 to anything else.
            // The per-call request replaces it with the page's own UA when known.
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
            client.DefaultRequestHeaders.TryAddWithoutValidation(
                "Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
            return client;
        }

        /// <summary>
        /// True when the URL is already the stream: used to skip the lookup and to
        /// recognise the success case while walking a chain.
        /// </summary>
        public static bool IsMediaUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return false;

            string path = uri.AbsolutePath;
            foreach (string ext in MediaExtensions)
            {
                if (path.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>
        /// The media URL behind <paramref name="url"/>, or null when the chain
        /// does not lead anywhere. A URL that is media already is returned as is.
        /// Never throws.
        /// </summary>
        public static string? Resolve(string? url, string? referrer = null, string? userAgent = null)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var start)) return null;
            if (start.Scheme != Uri.UriSchemeHttp && start.Scheme != Uri.UriSchemeHttps) return null;

            string current = url.Trim();
            if (IsMediaUrl(current)) return current;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { current };
            string pageReferrer = referrer ?? "";

            for (int depth = 0; depth < MaxDepth; depth++)
            {
                var page = Fetch(current, pageReferrer, userAgent);
                if (page == null) return null;

                string? next = FindNext(page.Value.Html, page.Value.BaseUri);
                if (next == null) return null;

                if (IsMediaUrl(next) && LooksPlayable(next, pageReferrer, userAgent))
                    return next;

                if (!seen.Add(next)) return null;

                pageReferrer = current;
                current = next;
            }

            return null;
        }

        /// <summary>
        /// The best URL to play from one document: a media URL first, then an
        /// iframe, then a script redirect. Public for tests, which drive it with
        /// fixtures instead of the network.
        /// </summary>
        internal static string? FindNext(string html, Uri baseUri)
        {
            var documents = new List<string>(ExpandScripts(html));

            // A playlist anywhere wins, including inside a decoded wrapper.
            foreach (string document in documents)
            {
                string? media = PickMedia(document, baseUri);
                if (media != null) return media;
            }

            foreach (string document in documents)
            {
                var frame = FrameSource.Match(document);
                if (!frame.Success) continue;

                string? nested = Absolutise(WebUtility.HtmlDecode(frame.Groups["url"].Value).Trim(), baseUri);
                if (nested != null) return nested;
            }

            foreach (string document in documents)
            {
                var redirect = ScriptRedirect.Match(document);
                if (!redirect.Success) continue;

                string? target = Absolutise(WebUtility.HtmlDecode(redirect.Groups["url"].Value).Trim(), baseUri);
                if (target != null) return target;
            }

            return null;
        }

        /// <summary>
        /// The document plus every payload its scripts decode. The wrapper page
        /// this was written for contains no markup at all outside the decoded
        /// text, so searching the raw HTML finds nothing.
        /// </summary>
        internal static IEnumerable<string> ExpandScripts(string html)
        {
            var documents = new List<string>();
            Collect(html, documents, 0);
            return documents;

            static void Collect(string document, List<string> into, int depth)
            {
                if (depth > 2) return;
                into.Add(document);

                foreach (Match match in AtobPayload.Matches(document))
                {
                    string? decoded = DecodePayload(match.Groups["payload"].Value);
                    if (decoded != null && !into.Contains(decoded))
                        Collect(decoded, into, depth + 1);
                }
            }
        }

        /// <summary>Percent-decodes the script string, then base64-decodes it.</summary>
        internal static string? DecodePayload(string payload)
        {
            try
            {
                string unescaped = Uri.UnescapeDataString(payload);
                byte[] bytes = Convert.FromBase64String(unescaped);
                return Encoding.UTF8.GetString(bytes);
            }
            catch
            {
                // Not this wrapper's encoding, or a broken payload: nothing to add.
                return null;
            }
        }

        /// <summary>
        /// The first media URL in a document, playlists before plain files: a page
        /// that offers both usually wants its HLS variant played.
        /// </summary>
        internal static string? PickMedia(string document, Uri baseUri)
        {
            var ranked = new List<(int Rank, int Index, string Url)>();
            foreach (Match match in MediaCandidate.Matches(document))
            {
                string raw = WebUtility.HtmlDecode(match.Value).Trim().Trim('\\');
                string? absolute = Absolutise(raw, baseUri);
                if (absolute == null) continue;

                ranked.Add((Rank(absolute), match.Index, absolute));
            }

            if (ranked.Count == 0) return null;

            ranked.Sort((a, b) => a.Rank != b.Rank ? a.Rank.CompareTo(b.Rank) : a.Index.CompareTo(b.Index));
            return ranked[0].Url;
        }

        private static int Rank(string url)
        {
            if (url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase)) return 0;
            if (url.Contains(".mpd", StringComparison.OrdinalIgnoreCase)) return 1;
            return 2;
        }

        /// <summary>Resolves a relative or protocol-relative reference against the page.</summary>
        private static string? Absolutise(string value, Uri baseUri)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return null;

            if (!Uri.TryCreate(baseUri, value, out var absolute)) return null;
            if (absolute.Scheme != Uri.UriSchemeHttp && absolute.Scheme != Uri.UriSchemeHttps) return null;

            return absolute.AbsoluteUri;
        }

        /// <summary>
        /// Guards against a plain-file candidate that is really another page. A
        /// playlist is trusted without a second request, since a signed link may
        /// only answer to a ranged GET.
        /// </summary>
        private static bool LooksPlayable(string url, string? referrer, string? userAgent)
        {
            if (url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase) ||
                url.Contains(".mpd", StringComparison.OrdinalIgnoreCase))
                return true;

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.TryAddWithoutValidation("Range", "bytes=0-1");
                ApplyIdentity(request, referrer, userAgent);

                using var response = Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead)
                    .GetAwaiter().GetResult();

                string type = response.Content.Headers.ContentType?.MediaType ?? "";
                if (type.Length == 0) return true;
                return !type.Contains("html", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                // Unknown is not a reason to refuse: the caller falls back to the
                // page URL, and the stream may still play.
                return true;
            }
        }

        private static (string Html, Uri BaseUri)? Fetch(string url, string? referrer, string? userAgent)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.TryAddWithoutValidation("Range", "bytes=0-" + (MaxBytes - 1));
                ApplyIdentity(request, referrer, userAgent);

                using var response = Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead)
                    .GetAwaiter().GetResult();
                if (!response.IsSuccessStatusCode) return null;

                string type = response.Content.Headers.ContentType?.MediaType ?? "";
                if (type.Length > 0 &&
                    !type.Contains("html", StringComparison.OrdinalIgnoreCase) &&
                    !type.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
                {
                    // Not a document: whatever this is, it is not a wrapper page.
                    return null;
                }

                using var stream = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
                var buffer = new byte[MaxBytes];
                int total = 0;
                while (total < buffer.Length)
                {
                    int read = stream.Read(buffer, total, buffer.Length - total);
                    if (read <= 0) break;
                    total += read;
                }

                if (total == 0) return null;

                string charset = response.Content.Headers.ContentType?.CharSet ?? "";
                string html = Decode(buffer, total, charset);

                Uri baseUri = response.RequestMessage?.RequestUri ?? new Uri(url);
                return (html, baseUri);
            }
            catch
            {
                return null;
            }
        }

        private static void ApplyIdentity(HttpRequestMessage request, string? referrer, string? userAgent)
        {
            // Always an explicit header: without one the runtime sends its own
            // "Chrome/124" default, and some wrappers now reject that version.
            request.Headers.TryAddWithoutValidation(
                "User-Agent",
                string.IsNullOrWhiteSpace(userAgent) ? BrowserUserAgent : userAgent.Trim());

            if (!string.IsNullOrWhiteSpace(referrer) &&
                Uri.TryCreate(referrer.Trim(), UriKind.Absolute, out var refUri) &&
                (refUri.Scheme == Uri.UriSchemeHttp || refUri.Scheme == Uri.UriSchemeHttps))
            {
                request.Headers.TryAddWithoutValidation("Referer", refUri.AbsoluteUri);
            }
        }

        /// <summary>
        /// Bytes to text. UTF-8 when the server does not say, Latin-1 for an
        /// unknown legacy charset, so a Turkish page does not come back as
        /// question marks.
        /// </summary>
        private static string Decode(byte[] buffer, int length, string? charset)
        {
            string name = (charset ?? "").Trim().Trim('"');
            if (name.Length == 0) return Encoding.UTF8.GetString(buffer, 0, length);

            try
            {
                return Encoding.GetEncoding(name).GetString(buffer, 0, length);
            }
            catch
            {
                return Encoding.Latin1.GetString(buffer, 0, length);
            }
        }
    }
}
