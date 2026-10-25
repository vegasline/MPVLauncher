// Original implementation, MIT licensed.

using System;
using MpvLauncher.Gui.Services;

namespace MpvLauncher.Gui.Tests;

/// <summary>
/// The update check compares three tools that version themselves differently:
/// yt-dlp publishes dated versions, while MPV and FFmpeg publish continuous
/// daily builds with no date in their version string. These tests pin the two
/// comparison rules, including the cases that must not produce a false
/// "update available" - which would invite a pointless download on every check.
/// </summary>
public class ToolUpdateTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    /* ---------------------------------------------------------- versions */

    [Fact]
    public void A_newer_ytdlp_tag_counts_as_available()
    {
        Assert.True(ToolUpdateService.TryParseVersion("2025.10.22", out var installed));
        Assert.True(ToolUpdateService.TryParseVersion("2025.11.01", out var latest));

        Assert.True(latest > installed);
    }

    [Fact]
    public void An_identical_ytdlp_version_is_not_an_update()
    {
        Assert.True(ToolUpdateService.TryParseVersion("2025.10.22", out var installed));
        Assert.True(ToolUpdateService.TryParseVersion("2025.10.22", out var latest));

        Assert.False(latest > installed);
    }

    [Fact]
    public void Version_text_with_a_prefix_and_a_build_suffix_still_parses()
    {
        // mpv prints "mpv v0.41.0-1227-gabc123" and the tag may carry a "v".
        Assert.True(ToolUpdateService.TryParseVersion("v0.41.0-1227-gabc123", out var version));
        Assert.Equal(new Version(0, 41, 0), version);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("yt-dlp not found")]
    [InlineData("MPV cannot start: exit code 1")]
    public void Text_without_a_numeric_version_is_rejected(string text)
    {
        Assert.False(ToolUpdateService.TryParseVersion(text, out _));
    }

    /* ------------------------------------------------------ build dates */

    [Fact]
    public void A_recent_build_is_current_even_if_upstream_published_yesterday()
    {
        // Installing a daily build and checking the next day must not report an
        // update; both projects publish every day, so that would never settle.
        Assert.False(ToolUpdateService.IsBuildStale(
            installedAt: Now.AddDays(-3),
            releasedAt: Now.AddDays(-1),
            now: Now));
    }

    [Fact]
    public void An_old_build_with_a_newer_release_is_reported()
    {
        Assert.True(ToolUpdateService.IsBuildStale(
            installedAt: Now.AddDays(-45),
            releasedAt: Now.AddDays(-1),
            now: Now));
    }

    [Fact]
    public void An_old_build_is_current_when_upstream_has_published_nothing_since()
    {
        Assert.False(ToolUpdateService.IsBuildStale(
            installedAt: Now.AddDays(-45),
            releasedAt: null,
            now: Now));
    }

    [Fact]
    public void The_same_day_release_does_not_count_as_newer()
    {
        // Time zones and the delay between a build and its publication mean the
        // release can look a few hours newer than the file that came from it.
        Assert.False(ToolUpdateService.IsBuildStale(
            installedAt: Now,
            releasedAt: Now.AddHours(6),
            now: Now.AddDays(45)));
    }

    [Fact]
    public void A_custom_staleness_window_is_honoured()
    {
        Assert.True(ToolUpdateService.IsBuildStale(
            installedAt: Now.AddDays(-10),
            releasedAt: Now.AddDays(-1),
            now: Now,
            staleDays: 7));
    }

    /* ----------------------------------------------------------- display */

    [Fact]
    public void The_installed_label_shows_only_the_version_token()
    {
        Assert.Equal("v0.41.0-1227-gabc123", ToolUpdateService.CompactVersion(
            "mpv v0.41.0-1227-gabc123 Copyright © 2000-2026 mpv/MPlayer projects"));

        Assert.Equal("9.0.2-essentials_build-www.gyan.dev", ToolUpdateService.CompactVersion(
            "ffmpeg version 9.0.2-essentials_build-www.gyan.dev Copyright (c) 2000-2026"));

        Assert.Equal("2026.10.21", ToolUpdateService.CompactVersion("2026.10.21"));
    }

    [Fact]
    public void A_label_without_a_version_is_shortened_instead_of_dropped()
    {
        string text = "MPV cannot start: exit code 1 (the file may be missing or blocked)";

        string compact = ToolUpdateService.CompactVersion(text);

        Assert.True(compact.Length <= 48);
        Assert.StartsWith("MPV cannot start", compact);
    }

    /* -------------------------------------------------------- whole rule */

    [Fact]
    public void The_current_ffmpeg_release_is_current_however_old_the_install()
    {
        // gyan.dev's latest release is versioned; an install of that same
        // version from two months ago must not be offered as an update just
        // because the file is old.
        Assert.Equal(ToolUpdateState.UpToDate, ToolUpdateService.CompareInstalled(
            installedVersion: "ffmpeg version 9.0.2-essentials_build-www.gyan.dev",
            releaseTag: "9.0.2",
            releasedAt: Now.AddDays(-60),
            installedAt: Now.AddDays(-60),
            now: Now));
    }

    [Fact]
    public void A_newer_versioned_release_is_an_update()
    {
        Assert.Equal(ToolUpdateState.Available, ToolUpdateService.CompareInstalled(
            installedVersion: "ffmpeg version 9.0.2-essentials_build-www.gyan.dev",
            releaseTag: "9.1.0",
            releasedAt: Now.AddDays(-1),
            installedAt: Now.AddDays(-5),
            now: Now));
    }

    [Fact]
    public void An_mpv_date_tag_falls_back_to_the_build_date_rule()
    {
        // "20261021" has no dots, so it is not a version; the installed mpv
        // string parses but the pair cannot be compared.
        Assert.Equal(ToolUpdateState.UpToDate, ToolUpdateService.CompareInstalled(
            installedVersion: "mpv v0.41.0-1227-gabc123",
            releaseTag: "20261021",
            releasedAt: Now.AddDays(-1),
            installedAt: Now.AddDays(-3),
            now: Now));

        Assert.Equal(ToolUpdateState.Available, ToolUpdateService.CompareInstalled(
            installedVersion: "mpv v0.41.0-1227-gabc123",
            releaseTag: "20261021",
            releasedAt: Now.AddDays(-1),
            installedAt: Now.AddDays(-45),
            now: Now));
    }
}
