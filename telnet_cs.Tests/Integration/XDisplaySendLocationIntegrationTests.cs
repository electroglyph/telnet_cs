// Phase 4 X-DISPLAY / SNDLOC pins over a live pair: the session's
// DO draws the client's WILL, SEND returns the configured display, and
// the SNDLOC location arrives volunteered (no SEND) and is stored even
// before any explicit request.
namespace telnet_cs.Tests
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using FluentAssertions;
    using Xunit;
    using telnet_cs.Protocol;
    using telnet_cs.Server;

    public class XDisplaySendLocationIntegrationTests
    {
        [Fact]
        public async Task RequestXDisplay_ConfiguredLocation_ReturnsIt()
        {
            var pair = LiveExchange.CreatePair(null, o => o.XDisplayLocation = "livehost:0");
            using (pair.Client)
            using (pair.Session)
            using (pair.Guard)
            {
                await pair.Session.SendOpeningPresetAsync(CancellationToken.None);
                await pair.Session.RequestEnableAsync(Options.XDisplay, CancellationToken.None);
                await LiveExchange.PumpUntilAsync(
                    pair.Client,
                    pair.Session,
                    () => pair.Session.Negotiation.IsEnabledByPeer((int)Options.XDisplay),
                    TimeSpan.FromSeconds(10));
                pair.Session.Negotiation.IsEnabledByPeer((int)Options.XDisplay).Should().BeTrue();

                var display = await LiveExchange.CollectAsync(
                    pair.Session.RequestXDisplayAsync(TimeSpan.FromSeconds(5)),
                    pair.Client,
                    TimeSpan.FromSeconds(10));

                display.Should().Be("livehost:0");
                pair.Session.ClientXDisplay.Should().Be("livehost:0");
            }
        }

        [Fact]
        public async Task RequestSendLocation_ConfiguredLocation_ReturnsIt()
        {
            var pair = LiveExchange.CreatePair(null, o => o.SendLocation = "Room 101");
            using (pair.Client)
            using (pair.Session)
            using (pair.Guard)
            {
                await pair.Session.SendOpeningPresetAsync(CancellationToken.None);
                var location = await LiveExchange.CollectAsync(
                    pair.Session.RequestSendLocationAsync(TimeSpan.FromSeconds(5)),
                    pair.Client,
                    TimeSpan.FromSeconds(10));

                location.Should().Be("Room 101");
                pair.Session.ClientLocation.Should().Be("Room 101");
            }
        }

        [Fact]
        public async Task SendLocation_VolunteeredAnswer_StoredWithoutRequest()
        {
            // The peer volunteers its SB after WILL, so the location lands
            // in the session store with no collector outstanding.
            var pair = LiveExchange.CreatePair(null, o => o.SendLocation = "volunteered");
            using (pair.Client)
            using (pair.Session)
            using (pair.Guard)
            {
                await pair.Session.SendOpeningPresetAsync(CancellationToken.None);
                await pair.Session.RequestEnableAsync(Options.SendLocation, CancellationToken.None);
                await LiveExchange.PumpUntilAsync(
                    pair.Client,
                    pair.Session,
                    () => pair.Session.ClientLocation is not null,
                    TimeSpan.FromSeconds(10));

                pair.Session.ClientLocation.Should().Be("volunteered");
            }
        }
    }
}
