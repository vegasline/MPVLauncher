// Original implementation, MIT licensed.

using MpvLauncher.Gui.Services;
using Xunit;

namespace MpvLauncher.Gui.Tests
{
    /// <summary>
    /// Routing for links that arrive from the browser extension.
    ///
    /// Only the extension knows whether a URL is still usable: it says "resolve"
    /// when the stream has to be fetched through the tab, and sends nothing when
    /// the URL can go straight to mpv. A popup list row that pointed at another
    /// host used to fall through to the relay anyway, and the session could only
    /// end in relay_no_stream.
    /// </summary>
    public class RelayRoutingTests
    {
        [Fact]
        public void ResolvedStreamGoesToTheRelayWhenEnabled()
        {
            Assert.Equal(
                MediaRoute.Relay,
                RelayRouting.ForExtension("https://site.example/w/abc", "resolve", relayEnabled: true));
        }

        [Fact]
        public void ResolvedStreamStaysDirectWhenTheRelayIsOff()
        {
            Assert.Equal(
                MediaRoute.Direct,
                RelayRouting.ForExtension("https://site.example/w/abc", "resolve", relayEnabled: false));
        }

        [Fact]
        public void ExtensionPayloadWithoutAKindNeverRelays()
        {
            // A popup list row: the extension already decided mpv can open it.
            Assert.Equal(
                MediaRoute.Direct,
                RelayRouting.ForExtension("https://accounts.example.com/signin", "", relayEnabled: true));
            Assert.Equal(
                MediaRoute.Direct,
                RelayRouting.ForExtension("https://www.youtube.com/shorts/abc", null, relayEnabled: true));
        }

        [Fact]
        public void MediaFilesAndTorrentsKeepTheirOwnRoute()
        {
            Assert.Equal(
                MediaRoute.Direct,
                RelayRouting.ForExtension("https://cdn.example.com/v/master.m3u8", "", relayEnabled: true));
            Assert.Equal(
                MediaRoute.Torrent,
                RelayRouting.ForExtension("magnet:?xt=urn:btih:abc", "resolve", relayEnabled: true));
        }
    }
}
