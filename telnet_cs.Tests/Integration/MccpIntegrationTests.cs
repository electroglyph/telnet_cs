// Phase 4 MCCP2/MCCP3 pins over a live pair: a real Client against a real
// ServerSession, both ends tapped. The server offers via OfferMccp2/3; the
// client passively accepts (EnableMccp default on) and starts its MCCP3
// compressor itself. Corrupt-stream and cap pins stay scripted — they need
// byte-exact hostile payloads no public API can emit.
namespace telnet_cs.Tests
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using FluentAssertions;
    using Xunit;
    using telnet_cs.Client;
    using telnet_cs.Protocol;
    using telnet_cs.Server;
    using telnet_cs.Transport;

    public class MccpIntegrationTests
    {
        private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

        private const byte Mccp2 = 86;
        private const byte Mccp3 = 87;

        private static async Task<(Client Client, ServerSession Session, WireTap ClientTap, WireTap SessionTap, IDisposable Guard)> CreateTappedPairAsync(
            TelnetServerOptions? serverOptions = null,
            Action<TelnetClientOptions>? configureClient = null)
        {
            var guard = GlobalStateGuard.SkipProactive(true);
            var (clientStream, serverStream) = InMemoryPipe.Create();
            var clientTap = new WireTap(clientStream);
            var sessionTap = new WireTap(serverStream);
            var session = new ServerSession(
                sessionTap, serverOptions ?? new TelnetServerOptions(), CancellationToken.None);
            var client = await Client.CreateAsync(clientTap, TimeSpan.FromSeconds(30), CancellationToken.None);
            configureClient?.Invoke(client.Settings);
            return (client, session, clientTap, sessionTap, guard);
        }

        private static async Task AgreeMccpAsync(Client client, ServerSession session, int option)
        {
            // The session offers (WILL); the client answers (DO). The option
            // lands on the session's US side and the client's PEER side.
            await session.SendOpeningPresetAsync(CancellationToken.None);
            await LiveExchange.PumpUntilAsync(
                client, session,
                () => session.Negotiation.IsEnabledByUs(option)
                    && client.Negotiation.IsEnabledByPeer(option),
                Budget);
            session.Negotiation.IsEnabledByUs(option).Should().BeTrue();
            client.Negotiation.IsEnabledByPeer(option).Should().BeTrue();
        }

        private static byte[] StartMarker(int option) =>
            [Wire.Iac, Wire.Sb, (byte)option, Wire.Iac, Wire.Se];

        [Fact]
        public async Task Mccp2_ServerOffer_CompressesOutboundRoundTrip()
        {
            // The session sends the empty SB START marker, then every later
            // write arrives compressed: the client inflates it back to the
            // original text, and the wire image after the marker is far
            // smaller than the raw payload.
            var options = new TelnetServerOptions { OfferMccp2 = true };
            var (client, session, _, sessionTap, guard) = await CreateTappedPairAsync(options);
            using (client)
            using (session)
            using (guard)
            {
                await AgreeMccpAsync(client, session, (int)Options.Mccp2);

                await LiveExchange.PumpUntilAsync(
                    client, session,
                    () => Wire.ContainsFrame(sessionTap.WrittenBytes, StartMarker((int)Options.Mccp2)),
                    Budget);
                Wire.ContainsFrame(sessionTap.WrittenBytes, StartMarker((int)Options.Mccp2)).Should().BeTrue();

                var markerIndex = Array.IndexOf(
                    sessionTap.WrittenBytes, StartMarker((int)Options.Mccp2)[0]);
                markerIndex.Should().BeGreaterThanOrEqualTo(0);

                var payload = new string('a', 200) + new string('b', 200);
                await session.WriteAsync(payload, CancellationToken.None);
                (await client.ReadAsync(TimeSpan.FromSeconds(5))).Should().Be(payload);

                var afterMarker = sessionTap.WrittenBytes.Length - markerIndex;
                afterMarker.Should().BeLessThan(payload.Length);
            }
        }

        [Fact]
        public async Task Mccp3_ServerOffer_ClientCompressesInboundRoundTrip()
        {
            // MCCP3 compresses the other way: the client sends its own empty
            // SB START marker, then its writes arrive compressed and the
            // session inflates them back.
            var options = new TelnetServerOptions { OfferMccp3 = true };
            var (client, session, clientTap, _, guard) = await CreateTappedPairAsync(options);
            using (client)
            using (session)
            using (guard)
            {
                await AgreeMccpAsync(client, session, (int)Options.Mccp3);

                await LiveExchange.PumpUntilAsync(
                    client, session,
                    () => Wire.ContainsFrame(clientTap.WrittenBytes, StartMarker((int)Options.Mccp3)),
                    Budget);
                Wire.ContainsFrame(clientTap.WrittenBytes, StartMarker((int)Options.Mccp3)).Should().BeTrue();

                var payload = "mccp3-compressed-" + new string('z', 300);
                await client.WriteAsync(payload, CancellationToken.None);
                (await session.ReadAsync(TimeSpan.FromSeconds(10))).Should().Be(payload);
            }
        }

        [Fact]
        public async Task Mccp2_ClientOptOut_StaysPlaintext()
        {
            // EnableMccp = false: the client WONTs the offer, no START
            // marker ever goes out, and text flows uncompressed both ways.
            var options = new TelnetServerOptions { OfferMccp2 = true };
            var (client, session, _, sessionTap, guard) = await CreateTappedPairAsync(
                options, c => c.EnableMccp = false);
            using (client)
            using (session)
            using (guard)
            {
                await session.SendOpeningPresetAsync(CancellationToken.None);
                await LiveExchange.PumpForAsync(client, session, TimeSpan.FromSeconds(2));

                session.Negotiation.IsEnabledByUs((int)Options.Mccp2).Should().BeFalse();
                Wire.ContainsFrame(sessionTap.WrittenBytes, StartMarker((int)Options.Mccp2)).Should().BeFalse();

                await session.WriteAsync("plain-down", CancellationToken.None);
                (await client.ReadAsync(TimeSpan.FromSeconds(5))).Should().Be("plain-down");

                await client.WriteAsync("plain-up", CancellationToken.None);
                (await session.ReadAsync(TimeSpan.FromSeconds(5))).Should().Be("plain-up");
            }
        }

        [Fact]
        public async Task Mccp2AndMccp3_BothDirections_CompressSimultaneously()
        {
            // Both offers agreed: each direction compresses independently in
            // the same session.
            var options = new TelnetServerOptions { OfferMccp2 = true, OfferMccp3 = true };
            var (client, session, _, _, guard) = await CreateTappedPairAsync(options);
            using (client)
            using (session)
            using (guard)
            {
                await AgreeMccpAsync(client, session, (int)Options.Mccp2);
                await AgreeMccpAsync(client, session, (int)Options.Mccp3);

                await session.WriteAsync("down-compressed", CancellationToken.None);
                (await client.ReadAsync(TimeSpan.FromSeconds(5))).Should().Be("down-compressed");

                await client.WriteAsync("up-compressed", CancellationToken.None);
                (await session.ReadAsync(TimeSpan.FromSeconds(10))).Should().Be("up-compressed");
            }
        }
    }
}
