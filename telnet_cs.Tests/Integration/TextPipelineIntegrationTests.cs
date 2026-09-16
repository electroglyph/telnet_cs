// Phase 4 text-pipeline pins over a live pair: a real Client against a
// real ServerSession, both ends tapped so the wire image (IAC doubling,
// CRLF, GA/AYT markers) is asserted alongside the text the peer reads.
// Terminated-read truncation/stash shapes mirror the scripted pins; only
// the transport (live pair instead of ScriptedStream) is new.
namespace telnet_cs.Tests
{
    using System;
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Threading.Tasks;
    using FluentAssertions;
    using Xunit;
    using telnet_cs.Client;
    using telnet_cs.Protocol;
    using telnet_cs.Server;
    using telnet_cs.Transport;

    public class TextPipelineIntegrationTests
    {
        private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

        private static (Client Client, ServerSession Session, WireTap ClientTap, WireTap SessionTap, IDisposable Guard) CreateTappedPair(
            TelnetServerOptions? serverOptions = null,
            Action<TelnetClientOptions>? configureClient = null)
        {
            var guard = GlobalStateGuard.SkipProactive(true);
            var (clientStream, serverStream) = InMemoryPipe.Create();
            var clientTap = new WireTap(clientStream);
            var sessionTap = new WireTap(serverStream);
            var session = new ServerSession(
                sessionTap, serverOptions ?? new TelnetServerOptions(), CancellationToken.None);
            var client = new Client(clientTap, CancellationToken.None);
            configureClient?.Invoke(client.Settings);
            return (client, session, clientTap, sessionTap, guard);
        }

        private static async Task AgreeSgaAsync(Client client, ServerSession session)
        {
            // The advanced preset carries WILL SGA; the client answers DO.
            // Pump until both sides see it (the DO reply is still in flight
            // when the session side alone flips).
            await session.SendOpeningPresetAsync(CancellationToken.None);
            await LiveExchange.PumpUntilAsync(
                client, session,
                () => session.Negotiation.IsEnabledByUs((int)Options.SuppressGoAhead)
                    && client.Negotiation.IsEnabledByPeer((int)Options.SuppressGoAhead),
                Budget);
        }

        [Fact]
        public async Task ReadAsync_QuietWire_ReturnsEmpty()
        {
            var (client, session, _, _, guard) = CreateTappedPair();
            using (client)
            using (session)
            using (guard)
            {
                (await client.ReadAsync(TimeSpan.FromMilliseconds(300))).Should().BeEmpty();
                (await session.ReadAsync(TimeSpan.FromMilliseconds(300))).Should().BeEmpty();
            }
        }

        [Fact]
        public async Task TerminatedRead_String_TruncatesAndStashesRemainder()
        {
            var (client, session, _, _, guard) = CreateTappedPair();
            using (client)
            using (session)
            using (guard)
            {
                await session.WriteAsync("hello\nworld", CancellationToken.None);
                (await client.TerminatedReadAsync("\n", TimeSpan.FromSeconds(5))).Should().Be("hello\n");
                (await client.ReadAsync(TimeSpan.FromSeconds(5))).Should().Be("world");
            }
        }

        [Fact]
        public async Task TerminatedRead_Regex_CutsAtMatchEnd()
        {
            var (client, session, _, _, guard) = CreateTappedPair();
            using (client)
            using (session)
            using (guard)
            {
                await session.WriteAsync("score 42!", CancellationToken.None);
                (await client.TerminatedReadAsync(new Regex(@"\d+"), TimeSpan.FromSeconds(5))).Should().Be("score 42");
                (await client.ReadAsync(TimeSpan.FromSeconds(5))).Should().Be("!");
            }
        }

        [Fact]
        public async Task TerminatedRead_Multi_FirstTerminatorWins()
        {
            var (client, session, _, _, guard) = CreateTappedPair();
            using (client)
            using (session)
            using (guard)
            {
                await session.WriteAsync("a;b!", CancellationToken.None);
                (await client.TerminatedReadAsync([";", "!"], TimeSpan.FromSeconds(5))).Should().Be("a;");
                (await client.ReadAsync(TimeSpan.FromSeconds(5))).Should().Be("b!");
            }
        }

        [Fact]
        public async Task Write_ByteIac_DoubledOnWireAndDecodedByPeer()
        {
            // A bare 0xFF data byte doubles on the wire (IAC escape) and
            // decodes back to ÿ on a Latin-1 peer — the live form of the
            // InflatedIacEscape handler pin (here without compression).
            // (Under UTF-8 the same wire byte correctly surfaces as U+FFFD;
            // that decode half is pinned scripted.)
            var (client, session, _, sessionTap, guard) = CreateTappedPair(
                configureClient: c => c.TextEncoding = null);
            using (client)
            using (session)
            using (guard)
            {
                await session.WriteAsync([65, 255, 66], CancellationToken.None);
                (await client.ReadAsync(TimeSpan.FromSeconds(5))).Should().Be("AÿB");
                Wire.ContainsFrame(sessionTap.WrittenBytes, 65, 255, 255, 66).Should().BeTrue();
            }
        }

        [Fact]
        public async Task WriteLine_CrlfOnWire()
        {
            var (client, session, _, sessionTap, guard) = CreateTappedPair();
            using (client)
            using (session)
            using (guard)
            {
                await session.WriteLineAsync("hi", CancellationToken.None);
                Wire.ContainsFrame(sessionTap.WrittenBytes, (byte)'h', (byte)'i', (byte)'\r', (byte)'\n').Should().BeTrue();
                (await client.ReadAsync(TimeSpan.FromSeconds(5))).Should().Contain("hi");
            }
        }

        [Fact]
        public async Task SendGa_WithoutSga_SendsBareMarker()
        {
            var (client, session, _, sessionTap, guard) = CreateTappedPair();
            using (client)
            using (session)
            using (guard)
            {
                (await session.SendGaAsync(CancellationToken.None)).Should().BeTrue();
                await LiveExchange.PumpUntilAsync(
                    client, session,
                    () => Wire.ContainsFrame(sessionTap.WrittenBytes, Wire.Iac, 249),
                    Budget);
                // The marker is consumed, never text.
                (await client.ReadAsync(TimeSpan.FromMilliseconds(300))).Should().BeEmpty();
            }
        }

        [Fact]
        public async Task SendGa_WithSga_Suppressed()
        {
            var (client, session, _, sessionTap, guard) = CreateTappedPair();
            using (client)
            using (session)
            using (guard)
            {
                await AgreeSgaAsync(client, session);
                var tapBefore = sessionTap.WrittenBytes.Length;
                (await session.SendGaAsync(CancellationToken.None)).Should().BeFalse();
                // Autonomous negotiation chatter may still grow the tap;
                // the pin is that no GA marker is among the new bytes.
                await LiveExchange.PumpForAsync(client, session, TimeSpan.FromSeconds(1));
                Wire.ContainsFrame(sessionTap.WrittenBytes[tapBefore..], Wire.Iac, 249).Should().BeFalse();
            }
        }

        [Fact]
        public async Task SendCommand_Ayt_ReachesPeerAsBytesAndDrawsNoReply()
        {
            // AYT is silent (no reply), but the IAC AYT bytes cross and the
            // session survives them.
            var (client, session, clientTap, _, guard) = CreateTappedPair();
            using (client)
            using (session)
            using (guard)
            {
                await client.SendCommand(Commands.AreYouThere, CancellationToken.None);
                await LiveExchange.PumpUntilAsync(
                    client, session,
                    () => Wire.ContainsFrame(clientTap.WrittenBytes, Wire.Iac, (byte)(int)Commands.AreYouThere),
                    Budget);
                (await session.ReadAsync(TimeSpan.FromMilliseconds(300))).Should().BeEmpty();

                await session.WriteAsync("alive", CancellationToken.None);
                (await client.ReadAsync(TimeSpan.FromSeconds(5))).Should().Be("alive");
            }
        }
    }
}
