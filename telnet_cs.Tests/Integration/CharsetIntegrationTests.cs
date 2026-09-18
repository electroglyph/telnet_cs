// Phase 4 CHARSET pins over a live pair: default offers accept UTF-8,
// disjoint offers reject to null, an accepted switch re-decodes the read
// path, and the default REQUEST carries the full 16-entry offer list.
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
    using telnet_cs.Transport;

    public class CharsetIntegrationTests
    {
        private static async Task AgreeCharsetAsync(
            telnet_cs.Client.Client client,
            ServerSession session)
        {
            await session.SendOpeningPresetAsync(CancellationToken.None);
            await LiveExchange.PumpUntilAsync(
                client,
                session,
                () => session.Negotiation.IsEnabledByPeer((int)Options.TerminalType),
                TimeSpan.FromSeconds(10));
            session.Negotiation.IsEnabledByPeer((int)Options.TerminalType).Should().BeTrue();
            await LiveExchange.PumpUntilAsync(
                client,
                session,
                () => session.Negotiation.IsEnabledByPeer((int)Options.CharacterSet),
                TimeSpan.FromSeconds(10));
            session.Negotiation.IsEnabledByPeer((int)Options.CharacterSet).Should().BeTrue();
        }

        [Fact]
        public async Task RequestCharset_DefaultOffers_AcceptsUtf8()
        {
            var pair = await LiveExchange.CreatePairAsync();
            using (pair.Client)
            using (pair.Session)
            using (pair.Guard)
            {
                await AgreeCharsetAsync(pair.Client, pair.Session);
                var accepted = await LiveExchange.CollectAsync(
                    pair.Session.RequestCharsetAsync(TimeSpan.FromSeconds(5)),
                    pair.Client,
                    TimeSpan.FromSeconds(10));

                accepted.Should().Be("UTF-8");
                pair.Session.ClientCharset.Should().Be("UTF-8");
            }
        }

        [Fact]
        public async Task RequestCharset_DisjointOffers_RejectsToNull()
        {
            // The client's CharsetOffers only shape its own outbound
            // REQUESTs — inbound offers resolve against the client's live
            // TextEncoding (strict UTF-8 by default). An offer list with no
            // UTF-8-compatible entry answers REJECTED, so the server
            // offers an unresolvable name here.
            var options = new TelnetServerOptions { CharsetOffers = ["X-NOPE"] };
            var pair = await LiveExchange.CreatePairAsync(options);
            using (pair.Client)
            using (pair.Session)
            using (pair.Guard)
            {
                await AgreeCharsetAsync(pair.Client, pair.Session);
                var accepted = await LiveExchange.CollectAsync(
                    pair.Session.RequestCharsetAsync(TimeSpan.FromSeconds(5)),
                    pair.Client,
                    TimeSpan.FromSeconds(10));

                accepted.Should().BeNull();
            }
        }

        [Fact]
        public async Task CharsetAccept_SwitchesReadDecoding()
        {
            // The client resolves inbound offers against its own
            // TextEncoding: Latin-1 accepts the server's LATIN1 offer, then
            // writes "é" as the single raw byte 0xE9, which the session
            // (now LATIN1-decoding) reads back as "é".
            var options = new TelnetServerOptions { CharsetOffers = ["LATIN1"] };
            var pair = await LiveExchange.CreatePairAsync(options, o => o.TextEncoding = Encoding.Latin1);
            using (pair.Client)
            using (pair.Session)
            using (pair.Guard)
            {
                await AgreeCharsetAsync(pair.Client, pair.Session);
                await LiveExchange.PumpUntilAsync(
                    pair.Client,
                    pair.Session,
                    () => pair.Session.Negotiation.IsEnabledByUs((int)Options.TransmitBinary),
                    TimeSpan.FromSeconds(10));
                var accepted = await LiveExchange.CollectAsync(
                    pair.Session.RequestCharsetAsync(TimeSpan.FromSeconds(5)),
                    pair.Client,
                    TimeSpan.FromSeconds(10));
                accepted.Should().Be("LATIN1");

                await pair.Client.WriteAsync("é", CancellationToken.None);
                (await pair.Session.ReadAsync(TimeSpan.FromSeconds(5))).Should().Be("é");
            }
        }

        [Fact]
        public async Task CharsetRequest_DefaultOptions_CarriesSixteenOffers()
        {
            var guard = GlobalStateGuard.SkipProactive(true);
            var (clientStream, serverStream) = InMemoryPipe.Create();
            var tap = new WireTap(serverStream);
            using var session = new ServerSession(tap, new TelnetServerOptions(), CancellationToken.None);
            using var client = await telnet_cs.Client.Client.CreateAsync(clientStream, TimeSpan.FromSeconds(30), CancellationToken.None);
            using (guard)
            {
                await AgreeCharsetAsync(client, session);
                await LiveExchange.CollectAsync(
                    session.RequestCharsetAsync(TimeSpan.FromSeconds(5)),
                    client,
                    TimeSpan.FromSeconds(10));

                var wire = tap.WrittenBytes;
                var start = Array.IndexOf(wire, Wire.Iac);
                while (start >= 0 && start + 2 < wire.Length
                    && !(wire[start + 1] == Wire.Sb && wire[start + 2] == (byte)(int)Options.CharacterSet))
                {
                    start = Array.IndexOf(wire, Wire.Iac, start + 1);
                }

                start.Should().BeGreaterThanOrEqualTo(0);
                var end = Array.IndexOf(wire, Wire.Se, start + 3);
                end.Should().BeGreaterThan(start + 4);
                wire[end - 1].Should().Be(Wire.Iac);
                // IAC SB 42 VERB <space-separated names> IAC SE.
                var names = Encoding.Latin1.GetString(wire[(start + 4)..(end - 1)])
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries);
                names.Should().HaveCount(16);
                names.Should().Contain("UTF-8");
                names.Should().Contain("US-ASCII");
            }
        }
    }
}
