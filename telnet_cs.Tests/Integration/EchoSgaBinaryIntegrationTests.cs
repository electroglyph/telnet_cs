// Phase 4 ECHO / SGA / BINARY pins over a live pair: the client agrees to
// the server's WILL ECHO by default (no opt-in; the bounce guard is the
// only gate on that direction) and agreement alone never replays bytes,
// an inbound DO ECHO is gated by OfferEcho, SGA and BINARY agree through
// the advanced preset, and BINARY gates non-ASCII writes on the client path.
namespace telnet_cs.Tests
{
    using System;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using FluentAssertions;
    using Xunit;
    using telnet_cs.Protocol;
    using telnet_cs.Server;

    public class EchoSgaBinaryIntegrationTests
    {
        [Fact]
        public async Task Echo_ServerWillEcho_AgreesByDefaultWithoutReplay()
        {
            // AgreeEcho answers a peer WILL with DO whenever our own side
            // is off (bounce guard only) — AllowRemoteEcho is not consulted
            // on this direction, so the default client agrees. Agreement is
            // negotiation state only: bytes the session receives are never
            // replayed to the peer (remote echo is the app's own write-back).
            var pair = await LiveExchange.CreatePairAsync();
            using (pair.Client)
            using (pair.Session)
            using (pair.Guard)
            {
                await pair.Session.SendOpeningPresetAsync(CancellationToken.None);
                await LiveExchange.PumpUntilAsync(
                    pair.Client,
                    pair.Session,
                    () => pair.Session.Negotiation.IsEnabledByUs((int)Options.Echo),
                    TimeSpan.FromSeconds(10));
                pair.Session.Negotiation.IsEnabledByUs((int)Options.Echo).Should().BeTrue();

                await LiveExchange.SettleAsync(pair.Client, pair.Session, TimeSpan.FromSeconds(10));
                pair.Session.ClientCharset.Should().Be("UTF-8");

                await pair.Client.WriteAsync("hi", CancellationToken.None);
                (await pair.Session.ReadAsync(TimeSpan.FromSeconds(5))).Should().Be("hi");
                (await pair.Client.ReadAsync(TimeSpan.FromMilliseconds(300))).Should().BeEmpty();
            }
        }

        [Fact]
        public async Task Echo_ClientDoEcho_SessionAgreesWhenOffered()
        {
            // The other direction IS gated: an inbound DO ECHO earns WILL
            // while OfferEcho is on (the default).
            var pair = await LiveExchange.CreatePairAsync();
            using (pair.Client)
            using (pair.Session)
            using (pair.Guard)
            {
                await pair.Client.RequestEnableAsync(Options.Echo, CancellationToken.None);
                // Pump until BOTH sides see it: the session agrees during
                // its own read (writing WILL), but that WILL is still in
                // flight to the client — exiting on the session side alone
                // flakes under load when the client's next read lags it.
                await LiveExchange.PumpUntilAsync(
                    pair.Client,
                    pair.Session,
                    () => pair.Session.Negotiation.IsEnabledByUs((int)Options.Echo)
                        && pair.Client.Negotiation.IsEnabledByPeer((int)Options.Echo),
                    TimeSpan.FromSeconds(10));

                // DO asks the SESSION to perform echo, so agreement lands
                // on the session's us-side (our WILL), not the him-side.
                pair.Session.Negotiation.IsEnabledByUs((int)Options.Echo).Should().BeTrue();
                pair.Client.Negotiation.IsEnabledByPeer((int)Options.Echo).Should().BeTrue();
            }
        }

        [Fact]
        public async Task Echo_ClientDoEcho_SessionRefusesWhenOfferEchoOff()
        {
            var options = new TelnetServerOptions { OfferEcho = false };
            var pair = await LiveExchange.CreatePairAsync(options);
            using (pair.Client)
            using (pair.Session)
            using (pair.Guard)
            {
                await pair.Client.RequestEnableAsync(Options.Echo, CancellationToken.None);
                await LiveExchange.PumpUntilAsync(
                    pair.Client,
                    pair.Session,
                    () => pair.Session.Negotiation.IsEnabledByUs((int)Options.Echo)
                        || pair.Session.Negotiation.WasRefusedByUs((int)Options.Echo),
                    TimeSpan.FromSeconds(10));

                pair.Session.Negotiation.IsEnabledByUs((int)Options.Echo).Should().BeFalse();
                pair.Session.Negotiation.WasRefusedByUs((int)Options.Echo).Should().BeTrue();
            }
        }

        [Fact]
        public async Task Sga_AdvancedPreset_AgreesBothSides()
        {
            var pair = await LiveExchange.CreatePairAsync();
            using (pair.Client)
            using (pair.Session)
            using (pair.Guard)
            {
                await pair.Session.SendOpeningPresetAsync(CancellationToken.None);
                await LiveExchange.PumpUntilAsync(
                    pair.Client,
                    pair.Session,
                    () => pair.Session.Negotiation.IsEnabledByUs((int)Options.SuppressGoAhead),
                    TimeSpan.FromSeconds(10));

                pair.Session.Negotiation.IsEnabledByUs((int)Options.SuppressGoAhead).Should().BeTrue();
                pair.Client.Negotiation.IsEnabledByPeer((int)Options.SuppressGoAhead).Should().BeTrue();
            }
        }

        [Fact]
        public async Task Binary_Agrees_HighByteRoundTrips()
        {
            var pair = await LiveExchange.CreatePairAsync();
            using (pair.Client)
            using (pair.Session)
            using (pair.Guard)
            {
                await pair.Session.SendOpeningPresetAsync(CancellationToken.None);
                await LiveExchange.PumpUntilAsync(
                    pair.Client,
                    pair.Session,
                    () => pair.Session.Negotiation.IsEnabledByUs((int)Options.TransmitBinary),
                    TimeSpan.FromSeconds(10));
                pair.Session.Negotiation.IsEnabledByUs((int)Options.TransmitBinary).Should().BeTrue();

                await pair.Session.WriteAsync("aÿb", CancellationToken.None);
                (await pair.Client.ReadAsync(TimeSpan.FromSeconds(5))).Should().Be("aÿb");
            }
        }

        [Fact]
        public async Task Binary_NotAgreed_ClientWriteHighByteThrows()
        {
            // No preset, no pump: BINARY is off, so the client encodes
            // strict ASCII and a non-ASCII write throws.
            var pair = await LiveExchange.CreatePairAsync();
            using (pair.Client)
            using (pair.Session)
            using (pair.Guard)
            {
                Func<Task> act = () => pair.Client.WriteAsync("é", CancellationToken.None);
                await act.Should().ThrowAsync<EncoderFallbackException>();
            }
        }
    }
}
