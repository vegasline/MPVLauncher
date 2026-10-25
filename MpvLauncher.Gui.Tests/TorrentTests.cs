// Original implementation, MIT licensed.

using System.Reflection;
using MpvLauncher.Gui.Services;

namespace MpvLauncher.Gui.Tests;

/// <summary>
/// The torrent feature is optional and its entry points are spread across the
/// URL box, drag/drop, the file picker, history and the browser host. These
/// tests pin the parts that must not depend on a running helper: source
/// recognition, the master-switch gate, argument building, settings clamps and
/// the stdout protocol parser.
/// </summary>
public class TorrentTests
{
    private const string HexHash = "0123456789abcdef0123456789abcdef01234567";
    private const string Base32Hash = "abcdefghijklmnopqrstuvwxyz234567";

    /* ------------------------------------------------------------- sources */

    [Fact]
    public void Magnet_links_are_recognised_and_their_info_hash_extracted()
    {
        Assert.True(TorrentSourceParser.TryParse(
            "magnet:?xt=urn:btih:" + HexHash + "&dn=example", out var source, out _));

        Assert.Equal(TorrentSourceKind.Magnet, source.Kind);
        Assert.Equal(HexHash, source.InfoHash);
    }

    [Fact]
    public void Base32_magnet_hashes_are_normalised_to_lower_case()
    {
        Assert.True(TorrentSourceParser.TryParse(
            "magnet:?xt=urn:btih:" + Base32Hash.ToUpperInvariant(), out var source, out _));

        Assert.Equal(Base32Hash, source.InfoHash);
    }

    [Fact]
    public void A_magnet_without_an_info_hash_is_not_a_torrent_source()
    {
        Assert.False(TorrentSourceParser.TryParse("magnet:?dn=missing", out _, out string error));
        Assert.Equal("magnet_missing_hash", error);
    }

    [Fact]
    public void Http_torrent_urls_are_accepted_but_other_media_urls_are_not()
    {
        Assert.True(TorrentSourceParser.TryParse("https://example.com/a.torrent", out var source, out _));
        Assert.Equal(TorrentSourceKind.TorrentUrl, source.Kind);

        Assert.False(TorrentSourceParser.IsTorrentLike("https://example.com/video.mkv"));
        Assert.False(TorrentSourceParser.IsTorrentLike("https://example.com/archive.torrent.zip"));
    }

    [Fact]
    public void Bare_info_hashes_are_accepted_in_both_encodings()
    {
        Assert.True(TorrentSourceParser.TryParse(HexHash, out var hex, out _));
        Assert.Equal(TorrentSourceKind.InfoHash, hex.Kind);

        Assert.True(TorrentSourceParser.TryParse(Base32Hash, out var b32, out _));
        Assert.Equal(TorrentSourceKind.InfoHash, b32.Kind);
    }

    [Fact]
    public void Local_torrent_files_are_accepted_by_path()
    {
        Assert.True(TorrentSourceParser.TryParse(@"C:\Media\example.torrent", out var source, out _));
        Assert.Equal(TorrentSourceKind.TorrentFile, source.Kind);

        Assert.True(TorrentSourceParser.IsTorrentLike("media/sub/example.torrent"));
    }

    [Fact]
    public void Non_http_schemes_are_not_treated_as_local_torrent_paths()
    {
        Assert.False(TorrentSourceParser.IsTorrentLike("ftp://example.com/a.torrent"));
        Assert.False(TorrentSourceParser.IsTorrentLike("file:///C:/media/a.torrent"));
    }

    [Fact]
    public void Log_descriptions_never_contain_a_magnet_passkey_or_url_path()
    {
        TorrentSourceParser.TryParse("magnet:?xt=urn:btih:" + HexHash + "&tr=https://private/passkey", out var magnet, out _);
        TorrentSourceParser.TryParse("https://example.com/signed/secret.torrent?token=abc", out var url, out _);

        string magnetLog = TorrentSourceParser.DescribeForLog(magnet);
        string urlLog = TorrentSourceParser.DescribeForLog(url);

        Assert.DoesNotContain("passkey", magnetLog);
        Assert.Contains(HexHash[..12], magnetLog);
        Assert.DoesNotContain("secret", urlLog);
        Assert.DoesNotContain("token", urlLog);
    }

    /* --------------------------------------------------------------- gate */

    [Fact]
    public void The_feature_switch_wins_over_an_installed_helper()
    {
        var config = new AppConfig { TorrentStreamingEnabled = false };

        Assert.Equal(TorrentLaunchBlock.FeatureDisabled, TorrentLaunchPolicy.Check(config, helperInstalled: true));
    }

    [Fact]
    public void An_enabled_feature_without_the_helper_is_blocked_before_anything_starts()
    {
        var config = new AppConfig { TorrentStreamingEnabled = true };

        Assert.Equal(TorrentLaunchBlock.HelperMissing, TorrentLaunchPolicy.Check(config, helperInstalled: false));
        Assert.Equal(TorrentLaunchBlock.None, TorrentLaunchPolicy.Check(config, helperInstalled: true));
    }

    [Fact]
    public void The_browser_host_always_rejects_torrent_shapes()
    {
        Assert.True(TorrentLaunchPolicy.IsRejectedFromBrowser("magnet:?xt=urn:btih:" + HexHash));
        Assert.True(TorrentLaunchPolicy.IsRejectedFromBrowser("https://example.com/a.torrent"));
        Assert.True(TorrentLaunchPolicy.IsRejectedFromBrowser(HexHash));
        Assert.False(TorrentLaunchPolicy.IsRejectedFromBrowser("https://example.com/episode.mkv"));
    }

    [Fact]
    public void Native_messaging_refuses_torrent_sources_even_when_the_feature_is_on()
    {
        // TryValidateMediaUrl is private on purpose: the only caller is the
        // native-message handler. Invoking it here proves the torrent check is
        // still ahead of the scheme allow-list.
        Assert.False(NativeHostAccepts("magnet:?xt=urn:btih:" + HexHash));
        Assert.False(NativeHostAccepts("https://example.com/a.torrent"));
        Assert.False(NativeHostAccepts(HexHash));
        Assert.True(NativeHostAccepts("https://example.com/episode.mkv"));
        Assert.False(NativeHostAccepts("file:///C:/video.mkv"));
        Assert.False(NativeHostAccepts(@"\\server\share\video.mkv"));
    }

    [Fact]
    public void The_default_download_folder_is_under_the_app_cache_and_a_custom_one_is_trimmed()
    {
        var config = new AppConfig();
        Assert.EndsWith("downloads", TorrentLaunchPolicy.DefaultDownloadDirectory);
        Assert.Equal(TorrentLaunchPolicy.DefaultDownloadDirectory, TorrentLaunchPolicy.EffectiveDownloadDirectory(config));

        config.TorrentDownloadDir = "  D:\\Torrents  ";
        Assert.Equal("D:\\Torrents", TorrentLaunchPolicy.EffectiveDownloadDirectory(config));
    }

    [Fact]
    public void The_last_file_key_is_case_insensitive_and_empty_when_unknown()
    {
        Assert.Equal("abc", TorrentLaunchPolicy.LastFileKey("  ABC "));
        Assert.Equal("", TorrentLaunchPolicy.LastFileKey(""));
    }

    /* ----------------------------------------------------------- settings */

    [Theory]
    [InlineData(-10, 0)]
    [InlineData(0, 0)]
    [InlineData(64, 64)]
    [InlineData(128, 128)]
    [InlineData(9999, 128)]
    public void Upload_is_off_at_zero_and_never_exceeds_the_cap(int input, int expected) =>
        Assert.Equal(expected, TorrentSettings.NormalizeUploadKiB(input));

    [Theory]
    [InlineData(2, 32)]
    [InlineData(8, 8)]
    [InlineData(64, 64)]
    [InlineData(200, 128)]
    public void The_forward_window_falls_back_to_the_default_below_its_minimum(int input, int expected) =>
        Assert.Equal(expected, TorrentSettings.NormalizeForwardWindowMiB(input));

    [Theory]
    [InlineData(2, 64)]
    [InlineData(16, 16)]
    [InlineData(999, 512)]
    public void The_mpv_buffer_is_clamped(int input, int expected) =>
        Assert.Equal(expected, TorrentSettings.NormalizeMpvBufferMiB(input));

    [Theory]
    [InlineData(0, 45)]
    [InlineData(5, 5)]
    [InlineData(45, 45)]
    [InlineData(500, 100)]
    public void Connection_limits_are_clamped(int input, int expected) =>
        Assert.Equal(expected, TorrentSettings.NormalizeMaxConnections(input));

    [Fact]
    public void Half_open_attempts_never_exceed_the_connection_limit()
    {
        Assert.Equal(8, TorrentSettings.NormalizeHalfOpen(0, 45));
        Assert.Equal(20, TorrentSettings.NormalizeHalfOpen(50, 100));
        Assert.Equal(5, TorrentSettings.NormalizeHalfOpen(20, 5));
    }

    [Theory]
    [InlineData(0, 7)]
    [InlineData(5, 5)]
    [InlineData(7, 7)]
    [InlineData(20, 10)]
    public void The_cache_pause_wait_is_clamped(int input, int expected) =>
        Assert.Equal(expected, TorrentSettings.NormalizeCachePauseWait(input));

    [Theory]
    [InlineData(0, 0)]
    [InlineData(5, 5)]
    [InlineData(30, 30)]
    [InlineData(31, 30)]
    [InlineData(-1, 0)]
    public void The_tail_wait_is_clamped_to_zero_through_thirty_seconds(int input, int expected) =>
        Assert.Equal(expected, TorrentSettings.NormalizeTailWait(input));

    [Fact]
    public void Playback_starts_at_once_when_the_tail_wait_is_zero()
    {
        Assert.Equal(0, TorrentSettings.DefaultTailWaitSeconds);
        Assert.Equal(30, TorrentSettings.MaxTailWaitSeconds);
    }

    [Fact]
    public void Tracker_normalisation_drops_invalid_and_duplicate_lines_but_keeps_comments()
    {
        string text = "# my trackers\n" +
                      "udp://tracker.example:1337/announce\n" +
                      "udp://tracker.example:1337/announce\n" +
                      "not a url\n" +
                      "https://tracker.example/announce\n";

        string result = TorrentSettings.NormalizeTrackerList(text);

        Assert.Contains("# my trackers", result);
        Assert.Equal(1, CountOccurrences(result, "udp://tracker.example:1337/announce"));
        Assert.Contains("https://tracker.example/announce", result);
        Assert.DoesNotContain("not a url", result);
    }

    [Fact]
    public void An_empty_tracker_list_falls_back_to_the_verified_defaults()
    {
        Assert.Equal(TorrentSettings.DefaultTrackerList, TorrentSettings.NormalizeTrackerList("   "));
        Assert.Equal(
            TorrentSettings.DefaultTrackers.Length,
            TorrentSettings.TrackerUrls(TorrentSettings.DefaultTrackerList).Count);
    }

    [Fact]
    public void Only_udp_http_and_https_trackers_are_kept()
    {
        string text = string.Join('\n',
            "udp://a.example:1/announce",
            "http://b.example/announce",
            "https://c.example/announce",
            "wss://d.example/announce",
            "magnet:?xt=urn:btih:" + HexHash);

        var urls = TorrentSettings.TrackerUrls(text);

        Assert.Equal(3, urls.Count);
        Assert.DoesNotContain(urls, u => u.StartsWith("wss"));
        Assert.DoesNotContain(urls, u => u.StartsWith("magnet"));
    }

    /* ---------------------------------------------------------- arguments */

    [Fact]
    public void Helper_arguments_are_passed_as_separate_elements()
    {
        string source = @"C:\Media\A show, ""quoted"" & odd.torrent";
        var args = TorrentHelperArguments.Build(
            source, @"C:\Temp\dl", @"C:\Temp\cache", @"C:\Temp\trackers.txt", @"C:\Temp\helper.log",
            launcherPid: 1234, forwardWindowMiB: 32, uploadLimitKiB: 0,
            maxConnections: 45, maxHalfOpen: 8, selectFile: 2);

        Assert.Equal(source, args[Array.IndexOf(args.ToArray(), "--source") + 1]);
        Assert.Contains("--download-dir", args);
        Assert.Contains("--cache-dir", args);
        Assert.Contains("--tracker-file", args);
        Assert.Contains("--log-file", args);
        Assert.Equal("2", args[Array.IndexOf(args.ToArray(), "--select-file") + 1]);
        Assert.Equal("0", args[Array.IndexOf(args.ToArray(), "--tail-wait-sec") + 1]);
        Assert.Equal("2", args[Array.IndexOf(args.ToArray(), "--contract") + 1]);
    }

    [Fact]
    public void The_tail_wait_reaches_the_helper_clamped()
    {
        var args = TorrentHelperArguments.Build(
            "magnet:?xt=urn:btih:" + HexHash, @"C:\dl", @"C:\cache", "", "",
            launcherPid: 1, forwardWindowMiB: 32, uploadLimitKiB: 0,
            maxConnections: 45, maxHalfOpen: 8, tailWaitSeconds: 12, selectFile: 0);

        Assert.Equal("12", args[Array.IndexOf(args.ToArray(), "--tail-wait-sec") + 1]);

        var clamped = TorrentHelperArguments.Build(
            "magnet:?xt=urn:btih:" + HexHash, @"C:\dl", @"C:\cache", "", "",
            launcherPid: 1, forwardWindowMiB: 32, uploadLimitKiB: 0,
            maxConnections: 45, maxHalfOpen: 8, tailWaitSeconds: 9999);

        Assert.Equal("30", clamped[Array.IndexOf(clamped.ToArray(), "--tail-wait-sec") + 1]);
    }

    [Fact]
    public void Helper_arguments_are_clamped_before_they_reach_the_process()
    {
        var args = TorrentHelperArguments.Build(
            "magnet:?xt=urn:btih:" + HexHash, @"C:\dl", @"C:\cache", "", "",
            launcherPid: 1, forwardWindowMiB: 2, uploadLimitKiB: 999,
            maxConnections: 1000, maxHalfOpen: 999);

        Assert.Equal("32", args[Array.IndexOf(args.ToArray(), "--window-mib") + 1]);
        Assert.Equal("128", args[Array.IndexOf(args.ToArray(), "--upload-limit-kib") + 1]);
        Assert.Equal("100", args[Array.IndexOf(args.ToArray(), "--max-conns") + 1]);
        Assert.Equal("20", args[Array.IndexOf(args.ToArray(), "--max-half-open") + 1]);
        Assert.DoesNotContain("--select-file", args);
        Assert.DoesNotContain("--tracker-file", args);
    }

    [Fact]
    public void A_source_and_folders_are_required()
    {
        Assert.Throws<ArgumentException>(() => TorrentHelperArguments.Build(
            " ", @"C:\dl", @"C:\cache", "", "", 1, 32, 0, 45, 8));
        Assert.Throws<ArgumentException>(() => TorrentHelperArguments.Build(
            "magnet:?xt=urn:btih:" + HexHash, "", @"C:\cache", "", "", 1, 32, 0, 45, 8));
        Assert.Throws<ArgumentOutOfRangeException>(() => TorrentHelperArguments.Build(
            "magnet:?xt=urn:btih:" + HexHash, @"C:\dl", @"C:\cache", "", "", 0, 32, 0, 45, 8));
    }

    [Fact]
    public void Mpv_arguments_keep_the_session_options_out_of_mpv_conf()
    {
        var args = TorrentHelperArguments.BuildMpvArguments(bufferMiB: 64, cachePauseWaitSeconds: 7);

        Assert.Equal(new[]
        {
            "--cache=yes",
            "--cache-pause=yes",
            "--cache-pause-initial=yes",
            "--cache-pause-wait=7",
            "--demuxer-max-bytes=64MiB",
            "--demuxer-max-back-bytes=16MiB",
            "--network-timeout=60"
        }, args);
        Assert.DoesNotContain(args, a => a.Contains("mpv.conf", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_back_buffer_keeps_a_sane_share_of_small_forward_buffers()
    {
        var args = TorrentHelperArguments.BuildMpvArguments(bufferMiB: 16, cachePauseWaitSeconds: 99);

        Assert.Contains("--cache-pause-wait=10", args);
        Assert.Contains("--demuxer-max-bytes=16MiB", args);
        Assert.Contains("--demuxer-max-back-bytes=8MiB", args);
    }

    /* ----------------------------------------------------------- protocol */

    [Fact]
    public void A_state_line_carries_peers_speed_and_progress()
    {
        var e = TorrentHelperEvent.Parse(
            """{"type":"state","phase":"playing","peers":12,"downloadSpeed":2048,"uploadSpeed":0,"progress":0.25,"received":1048576,"length":4194304}""");

        Assert.NotNull(e);
        Assert.Equal("state", e!.Type);
        Assert.Equal("playing", e.Phase);
        Assert.Equal(12, e.Peers);
        Assert.Equal(2048, e.DownloadSpeed);
        Assert.Equal(0.25, e.Progress);
        Assert.Equal(1048576, e.Received);
    }

    [Fact]
    public void A_metadata_line_lists_files_in_order()
    {
        var e = TorrentHelperEvent.Parse(
            """{"type":"metadata","name":"Show","files":[{"name":"a.mkv","length":100},{"name":"b.srt","length":50,"index":7}]}""");

        Assert.NotNull(e);
        Assert.Equal(2, e!.Files.Count);
        Assert.Equal(0, e.Files[0].Index);
        Assert.Equal("a.mkv", e.Files[0].DisplayName);
        Assert.Equal(7, e.Files[1].Index);
        Assert.Equal("100 B", e.Files[0].DisplaySize);
    }

    [Fact]
    public void A_ready_line_carries_the_local_url_and_display_name()
    {
        var e = TorrentHelperEvent.Parse(
            """{"type":"ready","url":"http://127.0.0.1:5555/token/x.mkv","fileName":"x.mkv","displayName":"Movie","size":42,"ready":true}""");

        Assert.NotNull(e);
        Assert.True(e!.Ready);
        Assert.Equal("http://127.0.0.1:5555/token/x.mkv", e.Url);
        Assert.Equal("Movie", e.DisplayName);
        Assert.Equal(42, e.Size);
    }

    [Fact]
    public void An_error_line_exposes_the_code_and_whether_it_is_fatal()
    {
        var e = TorrentHelperEvent.Parse("""{"type":"error","code":"metadata_timeout","message":"no peers","fatal":true}""");

        Assert.NotNull(e);
        Assert.Equal("metadata_timeout", e!.Code);
        Assert.Equal("no peers", e.Message);
        Assert.True(e.Fatal);
    }

    [Fact]
    public void Unknown_events_and_broken_lines_do_not_throw()
    {
        Assert.Equal("future", TorrentHelperEvent.Parse("""{"type":"future"}""")!.Type);
        Assert.Null(TorrentHelperEvent.Parse("not json"));
        Assert.Null(TorrentHelperEvent.Parse("[]"));
        Assert.Null(TorrentHelperEvent.Parse("   "));
    }

    [Fact]
    public void File_sizes_are_rendered_with_binary_units()
    {
        Assert.Equal("512 B", new TorrentFileEntry(0, "a", "a", 512).DisplaySize);
        Assert.Equal("1.5 KiB", new TorrentFileEntry(0, "a", "a", 1536).DisplaySize);
        Assert.Equal("1.0 MiB", new TorrentFileEntry(0, "a", "a", 1048576).DisplaySize);
    }

    /* --------------------------------------------------------------- hash */

    [Fact]
    public void Sha256_is_lower_case_hex_of_the_file_contents()
    {
        string path = Path.Combine(Path.GetTempPath(), "mpvlauncher-sha-" + Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllText(path, "abc");
        try
        {
            Assert.Equal(
                "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
                TorrentHelperInstallService.ComputeSha256(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_missing_or_empty_expected_hash_never_verifies()
    {
        Assert.False(TorrentHelperInstallService.VerifyInstalledHash(""));
        Assert.False(TorrentHelperInstallService.VerifyInstalledHash(new string('0', 64)));
    }

    /* ------------------------------------------------------------- source */

    [Fact]
    public void The_gate_is_evaluated_before_a_helper_session_can_start()
    {
        string source = File.ReadAllText(FindRepoFile(Path.Combine("MpvLauncher.Gui", "MainWindow.xaml.cs")));

        int gate = source.IndexOf("TorrentLaunchPolicy.Check", StringComparison.Ordinal);
        int start = source.IndexOf("_torrentSession.StartAsync", StringComparison.Ordinal);

        Assert.True(gate >= 0, "the launcher must call TorrentLaunchPolicy.Check");
        Assert.True(start > gate, "the gate must run before the helper is started");
    }

    [Fact]
    public void The_native_host_checks_the_torrent_shape_before_the_scheme_list()
    {
        string source = File.ReadAllText(FindRepoFile(Path.Combine(
            "MpvLauncher.Gui", "Services", "NativeHostService.cs")));

        int torrentCheck = source.IndexOf("IsRejectedFromBrowser", StringComparison.Ordinal);
        int schemeCheck = source.IndexOf("uri.Scheme != Uri.UriSchemeHttp", StringComparison.Ordinal);

        Assert.True(torrentCheck >= 0);
        Assert.True(schemeCheck > torrentCheck);
    }

    /* ------------------------------------------------------------- helpers */

    private static bool NativeHostAccepts(string url)
    {
        var method = typeof(NativeHostService).GetMethod(
            "TryValidateMediaUrl", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        object?[] args = { url, null };
        return (bool)method!.Invoke(null, args)!;
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0, index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    private static string FindRepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 10 && dir != null; i++, dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate)) return candidate;
            string nested = Path.Combine(dir.FullName, "mpv-opn", relative);
            if (File.Exists(nested)) return nested;
        }
        throw new FileNotFoundException($"could not find {relative}");
    }
}
