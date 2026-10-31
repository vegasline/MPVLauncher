// Original implementation, MIT licensed.
//
// Delivers a user report to the project's Discord webhook.
//
// There is no server behind this app, so the intake endpoint is baked in: a
// Discord webhook is the whole backend. That also means the token ships with
// the exe and must be treated as public - it is a mention channel, not a
// secret, and the payload strips @everyone for the same reason.

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MpvLauncher.Gui.Services
{
    /// <summary>One file the user attached to a report.</summary>
    public sealed class ReportAttachment
    {
        public string Path { get; init; } = "";

        public long Bytes { get; init; }

        /// <summary>File name alone: the payload never carries the folder.</summary>
        public string Name
        {
            get
            {
                try { return System.IO.Path.GetFileName(Path); }
                catch { return Path; }
            }
        }

        public string SizeText => Bytes >= 1024 * 1024
            ? (Bytes / (1024.0 * 1024.0)).ToString("0.0") + " MB"
            : Math.Max(1, Bytes / 1024) + " KB";
    }

    /// <summary>Everything one send needs, assembled by the window.</summary>
    public sealed class ReportRequest
    {
        /// <summary>"suggestion", "feature" or "bug" - picks the embed colour.</summary>
        public string Kind { get; init; } = "suggestion";

        /// <summary>The kind as the user sees it, already localized.</summary>
        public string KindLabel { get; init; } = "";

        public string Message { get; init; } = "";

        public string AppVersion { get; init; } = "";

        public string OsVersion { get; init; } = "";

        public string Language { get; init; } = "";

        public IReadOnlyList<ReportAttachment> Files { get; init; } = Array.Empty<ReportAttachment>();
    }

    /// <summary>Outcome of a send. Never throws at the caller.</summary>
    public sealed class ReportResult
    {
        public bool Ok { get; init; }

        /// <summary>Why it failed, short enough to sit in the status line.</summary>
        public string? Error { get; init; }

        public static ReportResult Success() => new() { Ok = true };

        public static ReportResult Failure(string error) => new() { Ok = false, Error = error };
    }

    public static class ReportService
    {
        /// <summary>Discord's own per-file ceiling, kept well below the 25 MB request limit.</summary>
        public const long MaxFileBytes = 8 * 1024 * 1024;

        public const int MaxFiles = 5;

        private const string WebhookBase = "https://discord.com/api/webhooks/";

        // Split so a plain text search for the endpoint does not find a working
        // URL in the binary. This is not security: see the note at the top.
        private static readonly string WebhookId = string.Concat("1565235354", "846363748");
        private static readonly string WebhookToken = string.Concat(
            "LdXW42s1L2M_xikNvC7vpmWxfOog", "ULxFmb_P1ztqP64M9jW", "-lhKNL7qhGdRqOU9g_qgx");

        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(40) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("MPVLauncher-Report/1.0");
            return client;
        }

        /// <summary>
        /// The JSON half of the multipart body.
        ///
        /// Also used by the tests: it is the part worth pinning, because a wrong
        /// shape here is rejected by Discord with a message that says nothing
        /// about which field was wrong.
        /// </summary>
        public static string BuildPayloadJson(ReportRequest request)
        {
            var headline = new StringBuilder();
            headline.Append("**").Append(request.KindLabel).Append("**");
            if (request.AppVersion.Length > 0) headline.Append(" · v").Append(request.AppVersion);
            if (request.Language.Length > 0) headline.Append(" · ").Append(request.Language);
            if (request.OsVersion.Length > 0) headline.Append(" · ").Append(request.OsVersion);
            if (request.Files.Count > 0) headline.Append(" · ").Append(request.Files.Count).Append(" file(s)");

            string message = request.Message.Trim();
            if (message.Length > 3900) message = message.Substring(0, 3900) + "\u2026";

            int colour = request.Kind switch
            {
                "bug" => 0xD9534F,
                "feature" => 0x3BA55D,
                _ => 0x5865F2,
            };

            var payload = new Dictionary<string, object?>
            {
                ["content"] = Trim(headline.ToString(), 1900),
                ["embeds"] = new object[]
                {
                    new Dictionary<string, object?>
                    {
                        ["description"] = message,
                        ["color"] = colour,
                        ["timestamp"] = DateTime.UtcNow.ToString("o"),
                    },
                },
                // A webhook in a public binary must not be able to ping anyone.
                ["allowed_mentions"] = new Dictionary<string, object?>
                {
                    ["parse"] = Array.Empty<string>(),
                },
            };

            return JsonSerializer.Serialize(payload);
        }

        /// <summary>Sends one report. Never throws; every failure comes back in the result.</summary>
        public static async Task<ReportResult> SendAsync(ReportRequest request, CancellationToken token = default)
        {
            if (string.IsNullOrWhiteSpace(request.Message))
                return ReportResult.Failure("empty message");

            try
            {
                using var form = new MultipartFormDataContent();

                form.Add(
                    new StringContent(BuildPayloadJson(request), Encoding.UTF8, "application/json"),
                    "payload_json");

                for (int i = 0; i < request.Files.Count; i++)
                {
                    var file = request.Files[i];

                    byte[] bytes;
                    try
                    {
                        bytes = await File.ReadAllBytesAsync(file.Path, token).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        return ReportResult.Failure(file.Name + ": " + ex.Message);
                    }

                    if (bytes.LongLength > MaxFileBytes)
                        return ReportResult.Failure(file.Name + " is too large");

                    var part = new ByteArrayContent(bytes);
                    part.Headers.ContentType = new MediaTypeHeaderValue(GuessContentType(file.Name));
                    form.Add(part, "files[" + i + "]", SafeFileName(file.Name));
                }

                string url = WebhookBase + WebhookId + "/" + WebhookToken + "?wait=true";

                using var response = await Http.PostAsync(url, form, token).ConfigureAwait(false);
                if (response.IsSuccessStatusCode) return ReportResult.Success();

                string body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                return ReportResult.Failure(((int)response.StatusCode) + " " + Trim(body, 160));
            }
            catch (TaskCanceledException)
            {
                return ReportResult.Failure("timed out");
            }
            catch (HttpRequestException ex)
            {
                return ReportResult.Failure(ex.Message);
            }
            catch (Exception ex)
            {
                return ReportResult.Failure(ex.Message);
            }
        }

        /// <summary>
        /// A header-safe file name. Content-Disposition is a Latin-1 header, and
        /// a raw Turkish or Japanese name makes HttpClient throw before the
        /// request leaves - with an error that names the header, not the file.
        /// </summary>
        private static string SafeFileName(string name)
        {
            var builder = new StringBuilder(name.Length);
            foreach (char c in name)
                builder.Append(c < 128 && c != '"' && c != '\\' && c != '\r' && c != '\n' ? c : '_');

            string result = builder.ToString().Trim();
            return result.Length == 0 ? "file" : result;
        }

        private static string GuessContentType(string fileName)
        {
            string ext = System.IO.Path.GetExtension(fileName).ToLowerInvariant();
            return ext switch
            {
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".gif" => "image/gif",
                ".webp" => "image/webp",
                ".bmp" => "image/bmp",
                ".mp4" => "video/mp4",
                ".mkv" => "video/x-matroska",
                ".webm" => "video/webm",
                ".mp3" => "audio/mpeg",
                ".json" => "application/json",
                ".log" or ".txt" => "text/plain",
                ".zip" => "application/zip",
                _ => "application/octet-stream",
            };
        }

        private static string Trim(string value, int max)
        {
            if (value.Length <= max) return value;
            return value.Substring(0, max - 1) + "\u2026";
        }
    }
}
