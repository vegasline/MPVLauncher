// Original implementation, MIT licensed.

using MpvLauncher.Gui.Services;
using Xunit;

namespace MpvLauncher.Gui.Tests
{
    /// <summary>
    /// The title parser is the only part of the page-title lookup that can be
    /// tested without a network, so it carries the cases that decide what a
    /// history row ends up showing.
    /// </summary>
    public class PageTitleServiceTests
    {
        [Fact]
        public void ExtractTitleReadsTheTag()
        {
            Assert.Equal("Paws & Palaces 4. Bölüm İzle - Anime izle",
                PageTitleService.ExtractTitle(
                    "<html><head><title>Paws &amp; Palaces 4. Bölüm İzle - Anime izle</title></head></html>"));
        }

        [Fact]
        public void ExtractTitleDecodesEntitiesAndCollapsesWhitespace()
        {
            Assert.Equal("Tom & Jerry's \"Show\"",
                PageTitleService.ExtractTitle(
                    "<title>\n   Tom &amp; Jerry&#39;s &quot;Show&quot;\n</title>"));
        }

        [Fact]
        public void ExtractTitleIgnoresAttributesAndCase()
        {
            Assert.Equal("Video", PageTitleService.ExtractTitle("<TITLE lang=\"tr\">Video</TITLE>"));
        }

        [Fact]
        public void ExtractTitleStripsNestedMarkup()
        {
            Assert.Equal("Hello world", PageTitleService.ExtractTitle("<title>Hello <b>world</b></title>"));
        }

        [Fact]
        public void MissingOrEmptyTitleYieldsNull()
        {
            Assert.Null(PageTitleService.ExtractTitle("<html><body>no title here</body></html>"));
            Assert.Null(PageTitleService.ExtractTitle("<title>   </title>"));
            Assert.Null(PageTitleService.ExtractTitle(""));
            Assert.Null(PageTitleService.ExtractTitle(null));
        }

        [Fact]
        public void UrlAsTitleIsNotATitle()
        {
            Assert.Null(PageTitleService.ExtractTitle("<title>https://example.com/watch?v=1</title>"));
        }

        [Fact]
        public void VeryLongTitlesAreCut()
        {
            string longTitle = new string('a', 400);
            string? title = PageTitleService.ExtractTitle($"<title>{longTitle}</title>");

            Assert.NotNull(title);
            Assert.Equal(200, title!.Length);
            Assert.EndsWith("...", title);
        }

        [Theory]
        [InlineData("https://www.youtube.com/watch?v=h9i3bOA2IRU", true)]
        [InlineData("https://optraco.top/plateau/abc/index.html", true)]
        [InlineData("https://cdn.example.com/stream/master.m3u8", false)]
        [InlineData("https://cdn.example.com/movie.MP4", false)]
        [InlineData("file:///C:/video.mkv", false)]
        [InlineData("ftp://example.com/page", false)]
        [InlineData("not a url", false)]
        [InlineData("", false)]
        public void CanQueryOnlyAcceptsPages(string url, bool expected)
        {
            Assert.Equal(expected, PageTitleService.CanQuery(url));
        }
    }
}
