// Pins for the UTF-8 server default: reads/writes need no explicit
// TextEncoding, a duplex pair round-trips multibyte text, and an agreed
// CHARSET still overrides per session.
namespace telnet_cs.Tests
{
    using System;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using FluentAssertions;
    using Xunit;
    using telnet_cs.Protocol;
    using telnet_cs.Server;
    using telnet_cs.Transport;

    public class ServerDefaultEncodingTests
    {
        [Fact]
        public async Task DefaultTextEncoding_DecodesUtf8WithoutConfiguration()
        {
            // No explicit TextEncoding: the C3 A9 pair arrives as one é.
            using var stream = new ScriptedStream(0xC3, 0xA9);
            using var session = new ServerSession(stream, new TelnetServerOptions(), CancellationToken.None);
            (await session.ReadAsync(TimeSpan.FromMilliseconds(500))).Should().Be("é");
        }

        [Fact]
        public async Task DefaultTextEncoding_EncodesUtf8WithoutConfiguration()
        {
            // No explicit TextEncoding: one é goes out as C3 A9 (two wire
            // bytes, counted as such).
            using var stream = new ScriptedStream();
            using var session = new ServerSession(stream, new TelnetServerOptions(), CancellationToken.None);
            await session.WriteAsync("é", CancellationToken.None);
            session.Context.CharsSent.Should().Be(2);
            stream.ByteWrites.Should().ContainSingle().Which.Should().Equal(new byte[] { 195, 169 });
        }

        [Fact]
        public async Task Duplex_DefaultTextEncoding_RoundTripsMultibyte()
        {
            // Two default sessions over an in-memory pair: é crosses as
            // C3 A9 and decodes back, with no loopback.
            var (writerSide, readerSide) = DuplexPipe.Create();
            using var writer = new ServerSession(writerSide, new TelnetServerOptions(), CancellationToken.None);
            using var reader = new ServerSession(readerSide, new TelnetServerOptions(), CancellationToken.None);
            await writer.WriteAsync("é", CancellationToken.None);
            (await reader.ReadAsync(TimeSpan.FromSeconds(5))).Should().Be("é");
            writerSide.WrittenBytes.Should().Equal(new byte[] { 195, 169 });
        }

        [Fact]
        public async Task AgreedCharset_OverridesUtf8DefaultForSessionReads()
        {
            // The peer ACCEPTs latin-1 (offered as LATIN1) against our
            // REQUEST, so this session — and only this session — decodes a
            // later bare E9 as é, where the UTF-8 default would buffer it as
            // an incomplete sequence.
            using var stream = new ScriptedStream();
            using var session = new ServerSession(stream, new TelnetServerOptions(), CancellationToken.None);
            session.Negotiation.ReceivedWill((int)Options.CharacterSet, agree: true);
            stream.Enqueue(255, 250, 42, 2, (byte)'l', (byte)'a', (byte)'t', (byte)'i', (byte)'n', (byte)'-', (byte)'1', 255, 240);
            (await session.RequestCharsetAsync(TimeSpan.FromSeconds(5))).Should().Be("latin-1");
            stream.Enqueue([0xE9]);
            (await session.ReadAsync(TimeSpan.FromMilliseconds(500))).Should().Be("é");
        }
    }
}
