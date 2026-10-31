// Original implementation, MIT licensed.

using System;
using MpvLauncher.Gui.Services;
using Xunit;

namespace MpvLauncher.Gui.Tests
{
    /// <summary>
    /// The page walk is the only part of the resolver that can be tested without
    /// a network, so it carries the shapes that were seen in the wild: a plain
    /// player page, a relative playlist, and the wrapper that hides its iframe
    /// in <c>document.write(atob(unescape("..."))))</c>.
    /// </summary>
    public class MediaPageResolverTests
    {
        private static readonly Uri Page =
            new("https://www.dizibox.live/player/moly/moly.php?h=a7508f");

        private static readonly Uri Embed = new("https://vidmoly.biz/embed-75by17wqif1q.html");

        [Fact]
        public void MediaUrlsAreRecognisedByExtension()
        {
            Assert.True(MediaPageResolver.IsMediaUrl("https://cdn.example/hls/master.m3u8?t=abc&e=1"));
            Assert.True(MediaPageResolver.IsMediaUrl("https://cdn.example/v.mp4"));
            Assert.True(MediaPageResolver.IsMediaUrl("https://cdn.example/live.mpd#t=1"));
            Assert.False(MediaPageResolver.IsMediaUrl("https://www.dizibox.live/player/moly/moly.php?h=1"));
            Assert.False(MediaPageResolver.IsMediaUrl("https://vidmoly.biz/embed-75by17wqif1q.html"));
        }

        [Fact]
        public void FindNextReadsAPlayerConfiguration()
        {
            string html = "player.setup({ sources: [{ file: 'https://cdn.example/hls/master.m3u8?t=abc&amp;s=1' }] });";
            Assert.Equal("https://cdn.example/hls/master.m3u8?t=abc&s=1",
                MediaPageResolver.FindNext(html, Embed));
        }

        [Fact]
        public void PlaylistWinsOverAProgressiveFile()
        {
            string html =
                "<video src=\"https://cdn.example/clip.mp4\"></video>" +
                "<script>var src = 'https://cdn.example/master.m3u8';</script>";

            Assert.Equal("https://cdn.example/master.m3u8", MediaPageResolver.FindNext(html, Embed));
        }

        [Fact]
        public void RelativePlaylistIsResolvedAgainstThePage()
        {
            string html = "<script>player.load('/hls/master.m3u8');</script>";
            Assert.Equal("https://vidmoly.biz/hls/master.m3u8", MediaPageResolver.FindNext(html, Embed));
        }

        [Fact]
        public void IframeIsFollowedWhenThePageHasNoMedia()
        {
            string html = "<iframe width=\"640\" height=\"360\" src=\"https://vidmoly.biz/embed-abc.html\" allowfullscreen></iframe>";
            Assert.Equal("https://vidmoly.biz/embed-abc.html", MediaPageResolver.FindNext(html, Page));
        }

        [Fact]
        public void ScriptRedirectIsFollowed()
        {
            string html = "<script>window.location.replace('https://vidmoly.biz/embed-abc.html');</script>";
            Assert.Equal("https://vidmoly.biz/embed-abc.html", MediaPageResolver.FindNext(html, Page));
        }

        [Fact]
        public void ObfuscatedWrapperIsDecoded()
        {
            // The wrapper the site actually serves: the iframe exists only inside
            // a base64 payload that a script writes into the document.
            const string decoded = "<html><body><iframe src=\"https://vidmoly.biz/embed-75by17wqif1q.html\"></iframe></body></html>";
            string script = "<script>document.write(atob(unescape(\"" + PercentEncode(decoded) + "\")));</script>";

            Assert.Equal("https://vidmoly.biz/embed-75by17wqif1q.html", MediaPageResolver.FindNext(script, Page));
        }

        [Fact]
        public void ObfuscatedWrapperCanCarryTheMediaDirectly()
        {
            const string decoded = "<script>var player = { file: 'https://cdn.example/master.m3u8?t=1' };</script>";
            string script = "<script>document.write(atob(unescape(\"" + PercentEncode(decoded) + "\")));</script>";

            Assert.Equal("https://cdn.example/master.m3u8?t=1", MediaPageResolver.FindNext(script, Page));
        }

        [Fact]
        public void DecodePayloadRejectsGarbage()
        {
            Assert.Null(MediaPageResolver.DecodePayload("not base64 %%%"));
        }

        [Fact]
        public void PageWithNothingPlayableYieldsNull()
        {
            Assert.Null(MediaPageResolver.FindNext("<html><body><p>Nothing here</p></body></html>", Page));
        }

        /// <summary>
        /// What the site's own beat does: percent-encode the base64, then let the
        /// page decode it. Encoding the base64 again is the same transformation.
        /// </summary>
        private static string PercentEncode(string text) =>
            Uri.EscapeDataString(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text)));
    }
}
