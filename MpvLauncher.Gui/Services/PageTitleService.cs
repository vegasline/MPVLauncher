// Original implementation, MIT licensed.

using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MpvLauncher.Gui.Services
{
    /// <summary>
    /// Asks a web page for its &lt;title&gt; so a history row can read like the
    /// browser tab it came from instead of repeating its own link.
    ///
    /// This is the fallback path, used for a link the user typed or pasted into
    /// the launcher. When the browser extension starts a playback it already
    /// knows the tab title and sends it along, which is both cheaper and more
    /// accurate: the tab title is the site's title, while a bare media URL
    /// (an .m3u8, a direct stream) has no document of its own to ask.
    /// </summary>
    public static class PageTitleService
    {
        /// <summary>What a browser sends; some sites answer 403 to an empty UA.</summary>
        private const string UserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";

        /// <summary>
        /// Enough to cover the head of a page - titles sit inside the first few
        /// kilobytes - without downloading a video that turned out to be a plain
        /// file served as text/plain.
        /// </summary>
        private const int MaxBytes = 192 * 1024;

        private static readonly Regex TitleTag = new(
            @"<title[^>]*>(.*?)</title>",
            // CultureInvariant is not optional here: IgnoreCase folds with the
            // current culture, and under tr-TR "TITLE" lowercases to "tıtle"
            // (dotless i), so <TITLE> would never match <title>.
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled |
            RegexOptions.CultureInvariant);

        private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

        private static readonly HttpClient Http = CreateClient();

        /// <summary>
        /// Media that is never a page: asking one of these for a title would
        /// download megabytes of stream and find nothing.
        /// </summary>
        private static readonly string[] MediaExtensions =
        {
            ".m3u8", ".mpd", ".ts", ".mp4", ".mkv", ".webm", ".mov", ".avi",
            ".mp3", ".m4a", ".aac", ".flac", ".ogg", ".opus", ".wav"
        };

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

            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
            client.DefaultRequestHeaders.TryAddWithoutValidation(
                "Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
            return client;
        }

        /// <summary>
        /// True for a URL worth asking. Anything that is plainly a media file is
        /// skipped before a request is made.
        /// </summary>
        public static bool CanQuery(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return false;
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;

            string path = uri.AbsolutePath.ToLowerInvariant();
            foreach (string ext in MediaExtensions)
            {
                if (path.EndsWith(ext, StringComparison.Ordinal)) return false;
            }

            return true;
        }

        /// <summary>
        /// The page's title, or null when there is none to be had. Never throws:
        /// a failed lookup simply leaves the history row showing its link.
        /// </summary>
        public static async Task<string?> FetchAsync(string? url, CancellationToken cancellationToken = default)
        {
            if (!CanQuery(url)) return null;

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url!.Trim());
                // The whole page is not needed - the head carries the title.
                request.Headers.TryAddWithoutValidation("Range", "bytes=0-" + (MaxBytes - 1));

                using var response = await Http
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);

                if (!response.IsSuccessStatusCode) return null;

                string mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
                if (mediaType.Length > 0 &&
                    !mediaType.Contains("html", StringComparison.OrdinalIgnoreCase) &&
                    !mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                await using var stream = await response.Content
                    .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

                var buffer = new byte[MaxBytes];
                int total = 0;
                while (total < buffer.Length)
                {
                    int read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), cancellationToken)
                        .ConfigureAwait(false);
                    if (read <= 0) break;
                    total += read;
                }

                if (total == 0) return null;

                string html = Decode(buffer, total, response.Content.Headers.ContentType?.CharSet);
                return ExtractTitle(html);
            }
            catch
            {
                // Timeout, DNS failure, TLS problem, a page that never ends:
                // all mean "no title", not an error worth showing.
                return null;
            }
        }

        /// <summary>
        /// Reads the &lt;title&gt; out of HTML and makes it presentable: tags are
        /// stripped, entities decoded, runs of whitespace collapsed. Returns null
        /// for an empty or uninformative title.
        /// </summary>
        public static string? ExtractTitle(string? html)
        {
            if (string.IsNullOrWhiteSpace(html)) return null;

            var match = TitleTag.Match(html);
            if (!match.Success) return null;

            string title = match.Groups[1].Value;
            // A stray tag inside the title (rare but real) is markup, not text.
            title = Regex.Replace(title, "<[^>]+>", " ");
            title = WebUtility.HtmlDecode(title);
            title = Whitespace.Replace(title, " ").Trim();

            if (title.Length == 0) return null;
            // Some pages serve their own URL as the title; that is no better
            // than the link, so treat it as "nothing found". Only a real scheme
            // counts: Uri.TryCreate treats a bare word like "Video" as a file
            // path, which would throw away legitimate one-word titles.
            if (title.Contains("://", StringComparison.Ordinal) &&
                Uri.TryCreate(title, UriKind.Absolute, out var asUri) &&
                (asUri.Scheme == Uri.UriSchemeHttp || asUri.Scheme == Uri.UriSchemeHttps || asUri.IsFile))
            {
                return null;
            }

            if (title.Length > 200) title = title[..197].TrimEnd() + "...";
            return title;
        }

        /// <summary>
        /// Bytes to text. UTF-8 is assumed when the server does not say, which is
        /// right for almost everything; an explicit legacy charset falls back to
        /// Latin-1 rather than producing question marks everywhere.
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
