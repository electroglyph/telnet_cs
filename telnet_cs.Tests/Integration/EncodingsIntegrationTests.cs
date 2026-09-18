// Phase 4 encoding pins over a live pair: the session and client
// configured with the same codec round-trip text both ways (client to
// session needs BINARY for high bytes, so every pair agrees BINARY via
// the opening preset first). Codec tables, split-sequence holders, and
// the ESC-hold input layer stay scripted — only the live transport is
// new here.
namespace telnet_cs.Tests
{
    using System;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using FluentAssertions;
    using Xunit;
    using telnet_cs.Client;
    using telnet_cs.Encodings;
    using telnet_cs.Protocol;
    using telnet_cs.Server;
    using telnet_cs.Transport;

    public class EncodingsIntegrationTests
    {
        private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

        private static async Task<(Client Client, ServerSession Session, IDisposable Guard)> CreateCodecPairAsync(Encoding? codec)
        {
            var guard = GlobalStateGuard.SkipProactive(true);
            var (clientStream, serverStream) = InMemoryPipe.Create();
            var session = new ServerSession(
                serverStream, new TelnetServerOptions { TextEncoding = codec }, CancellationToken.None);
            var client = await Client.CreateAsync(clientStream, TimeSpan.FromSeconds(30), CancellationToken.None);
            client.Settings.TextEncoding = codec;
            return (client, session, guard);
        }

        private static async Task AgreeBinaryAsync(Client client, ServerSession session)
        {
            await session.SendOpeningPresetAsync(CancellationToken.None);
            await LiveExchange.PumpUntilAsync(
                client, session,
                () => session.Negotiation.IsEnabledByUs((int)Options.TransmitBinary)
                    && client.Negotiation.IsEnabledByUs((int)Options.TransmitBinary),
                Budget);
            session.Negotiation.IsEnabledByUs((int)Options.TransmitBinary).Should().BeTrue();
            client.Negotiation.IsEnabledByUs((int)Options.TransmitBinary).Should().BeTrue();
        }

        private static async Task RoundTripsBothWaysAsync(Client client, ServerSession session, string text)
        {
            await session.WriteAsync(text, CancellationToken.None);
            (await client.ReadAsync(TimeSpan.FromSeconds(5))).Should().Be(text);
            await client.WriteAsync(text, CancellationToken.None);
            (await session.ReadAsync(TimeSpan.FromSeconds(5))).Should().Be(text);
        }

        [Fact]
        public async Task Utf8_Multibyte_RoundTripsBothWays()
        {
            var (client, session, guard) = await CreateCodecPairAsync(Encoding.UTF8);
            using (client)
            using (session)
            using (guard)
            {
                await AgreeBinaryAsync(client, session);
                await RoundTripsBothWaysAsync(client, session, "aÿb夢");
            }
        }

        [Fact]
        public async Task Latin1_NullEncoding_RoundTripsBothWays()
        {
            // Legacy mode: noCharset auto-request (it would override the
            // null decoding with the accepted UTF-8), so the bare 0xFF
            // survives as ÿ both ways.
            var guard = GlobalStateGuard.SkipProactive(true);
            var (clientStream, serverStream) = InMemoryPipe.Create();
            var session = new ServerSession(
                serverStream,
                new TelnetServerOptions { TextEncoding = null, RequestCharacterSet = false },
                CancellationToken.None);
            var client = await Client.CreateAsync(clientStream, TimeSpan.FromSeconds(30), CancellationToken.None);
            client.Settings.TextEncoding = null;
            using (client)
            using (session)
            using (guard)
            {
                await AgreeBinaryAsync(client, session);
                // Down: the session string-write maps ÿ to one Latin-1
                // byte (doubled on the wire) and the client decodes it back.
                await session.WriteAsync("AÿB", CancellationToken.None);
                (await client.ReadAsync(TimeSpan.FromSeconds(5))).Should().Be("AÿB");
                // Up: a client string-write cannot carry ÿ here (strict
                // ASCII off-BINARY throws; on-BINARY encodes UTF-8), so the
                // raw byte form covers the reverse leg.
                await client.WriteAsync([65, 255, 66], CancellationToken.None);
                (await session.ReadAsync(TimeSpan.FromSeconds(5))).Should().Be("AÿB");
            }
        }

        [Theory]
        [InlineData("atascii")]
        [InlineData("petscii")]
        [InlineData("atarist")]
        public async Task RetroCharmaps_Ascii_RoundTripsBothWays(string name)
        {
            Encoding codec = name switch
            {
                "atascii" => new AtasciiEncoding(),
                "petscii" => new PetsciiEncoding(),
                "atarist" => new AtaristEncoding(),
                _ => throw new ArgumentOutOfRangeException(nameof(name)),
            };
            var (client, session, guard) = await CreateCodecPairAsync(codec);
            using (client)
            using (session)
            using (guard)
            {
                await AgreeBinaryAsync(client, session);
                await RoundTripsBothWaysAsync(client, session, "HELLO vandal 123");
            }
        }

        [Fact]
        public async Task Big5Bbs_CjkPhrase_RoundTripsBothWays()
        {
            var (client, session, guard) = await CreateCodecPairAsync(new Big5BbsEncoding());
            using (client)
            using (session)
            using (guard)
            {
                await AgreeBinaryAsync(client, session);
                await RoundTripsBothWaysAsync(client, session, "夢想台灣");
            }
        }
    }
}
