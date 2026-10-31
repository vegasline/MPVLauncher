// Original implementation, MIT licensed.

using System;
using System.Collections.Generic;
using System.Linq;
using MpvLauncher.Gui.Services;
using Xunit;

namespace MpvLauncher.Gui.Tests
{
    /// <summary>
    /// Both processes write the history file, so the merge decides what the list
    /// shows after a playback started from the browser. These are the rules: the
    /// union wins, the newer timestamp wins, a real title beats a bare link, and
    /// a pin is never lost.
    /// </summary>
    public class HistoryMergeTests
    {
        private static HistoryItem Row(string url, string title, int minutesAgo, bool pinned = false) =>
            new()
            {
                Url = url,
                Title = title,
                Timestamp = new DateTime(2026, 10, 30, 12, 0, 0).AddMinutes(-minutesAgo),
                Pinned = pinned
            };

        [Fact]
        public void HostRowAppearsWhileTheWindowIsOpen()
        {
            var memory = new List<HistoryItem> { Row("https://a.example/one.m3u8", "One", 10) };
            var disk = new List<HistoryItem>
            {
                Row("https://b.example/two.m3u8", "Two", 0),
                Row("https://a.example/one.m3u8", "One", 10)
            };

            var merged = ConfigService.MergeHistory(memory, disk, 50);

            Assert.Equal(2, merged.Count);
            Assert.Equal("https://b.example/two.m3u8", merged[0].Url);
        }

        [Fact]
        public void NewerTimestampWinsForTheSameUrl()
        {
            var memory = new List<HistoryItem> { Row("https://a.example/one.m3u8", "One", 30) };
            var disk = new List<HistoryItem> { Row("https://a.example/one.m3u8", "One", 1) };

            var merged = ConfigService.MergeHistory(memory, disk, 50);

            Assert.Single(merged);
            Assert.Equal(new DateTime(2026, 10, 30, 11, 59, 0), merged[0].Timestamp);
        }

        [Fact]
        public void RealTitleBeatsTheBareLink()
        {
            var memory = new List<HistoryItem> { Row("https://a.example/one.m3u8", "https://a.example/one.m3u8", 5) };
            var disk = new List<HistoryItem> { Row("https://a.example/one.m3u8", "VisionQuest 1. Bölüm", 5) };

            var merged = ConfigService.MergeHistory(memory, disk, 50);

            Assert.Equal("VisionQuest 1. Bölüm", merged[0].Title);
        }

        [Fact]
        public void PinSurvivesAFileThatDoesNotKnowAboutItYet()
        {
            var memory = new List<HistoryItem> { Row("https://a.example/one.m3u8", "One", 20, pinned: true) };
            var disk = new List<HistoryItem> { Row("https://a.example/one.m3u8", "One", 20, pinned: false) };

            var merged = ConfigService.MergeHistory(memory, disk, 50);

            Assert.True(merged[0].Pinned);
        }

        [Fact]
        public void PinnedRowsSortFirstAndTheLimitIsKept()
        {
            var memory = new List<HistoryItem>
            {
                Row("https://a.example/1", "1", 1),
                Row("https://a.example/2", "2", 2),
                Row("https://a.example/pinned", "Pinned", 90, pinned: true)
            };

            var merged = ConfigService.MergeHistory(memory, Array.Empty<HistoryItem>(), 2);

            Assert.Equal(2, merged.Count);
            Assert.Equal("https://a.example/pinned", merged[0].Url);
            Assert.True(merged[0].Pinned);
        }
    }
}
