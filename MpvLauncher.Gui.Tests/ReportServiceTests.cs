// Original implementation, MIT licensed.

using System.Text.Json;
using MpvLauncher.Gui.Services;
using Xunit;

namespace MpvLauncher.Gui.Tests
{
    /// <summary>
    /// The webhook payload is the part of the report that can be checked
    /// without a network. Discord rejects a wrong shape with a message that
    /// never says which field was wrong, so the shape is pinned here.
    /// </summary>
    public class ReportServiceTests
    {
        private static ReportRequest Sample(string kind = "bug", string message = "It crashed.") => new()
        {
            Kind = kind,
            KindLabel = "Bug",
            Message = message,
            AppVersion = "1.9.16",
            OsVersion = "Microsoft Windows NT 10.0.22631.0",
            Language = "tr",
        };

        [Fact]
        public void PayloadCarriesTheMessageAndTheContext()
        {
            string json = ReportService.BuildPayloadJson(Sample());

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string content = root.GetProperty("content").GetString()!;
            Assert.Contains("Bug", content);
            Assert.Contains("1.9.16", content);
            Assert.Contains("tr", content);

            var embed = root.GetProperty("embeds")[0];
            Assert.Equal("It crashed.", embed.GetProperty("description").GetString());
            Assert.True(embed.TryGetProperty("timestamp", out _));
        }

        [Fact]
        public void PayloadCannotMentionAnyone()
        {
            // The webhook token ships inside a public exe, so a report must not
            // be able to ping @everyone in the developer's channel.
            string json = ReportService.BuildPayloadJson(Sample(message: "@everyone look at this"));
            using var doc = JsonDocument.Parse(json);

            var parse = doc.RootElement.GetProperty("allowed_mentions").GetProperty("parse");
            Assert.Equal(0, parse.GetArrayLength());
        }

        [Fact]
        public void PayloadTruncatesAnOversizedMessage()
        {
            string json = ReportService.BuildPayloadJson(Sample(message: new string('x', 9000)));
            using var doc = JsonDocument.Parse(json);

            string description = doc.RootElement.GetProperty("embeds")[0].GetProperty("description").GetString()!;
            Assert.True(description.Length <= 3901, "length was " + description.Length);
            Assert.EndsWith("\u2026", description);
        }

        [Fact]
        public void KindChoosesTheColour()
        {
            int Colour(string kind)
            {
                using var doc = JsonDocument.Parse(ReportService.BuildPayloadJson(Sample(kind)));
                return doc.RootElement.GetProperty("embeds")[0].GetProperty("color").GetInt32();
            }

            Assert.NotEqual(Colour("bug"), Colour("feature"));
            Assert.NotEqual(Colour("feature"), Colour("suggestion"));
            Assert.NotEqual(Colour("bug"), Colour("suggestion"));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public async System.Threading.Tasks.Task SendRejectsAnEmptyMessageWithoutTouchingTheNetwork(string message)
        {
            var result = await ReportService.SendAsync(Sample(message: message));

            Assert.False(result.Ok);
            Assert.False(string.IsNullOrWhiteSpace(result.Error));
        }

        [Fact]
        public void AttachmentReportsNameAndSize()
        {
            var attachment = new ReportAttachment
            {
                Path = @"C:\Users\me\Pictures\shot.png",
                Bytes = 2 * 1024 * 1024,
            };

            Assert.Equal("shot.png", attachment.Name);
            // One decimal, separator from the current culture: this string is
            // shown in the UI, not sent anywhere.
            Assert.Equal(2.ToString("0.0") + " MB", attachment.SizeText);
        }

        [Fact]
        public void LimitsStayInsideDiscordCeilings()
        {
            Assert.True(ReportService.MaxFileBytes < 25L * 1024 * 1024);
            Assert.InRange(ReportService.MaxFiles, 1, 10);
        }
    }
}
