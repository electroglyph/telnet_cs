namespace telnet_cs.Tests
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using FluentAssertions;
    using Xunit;
    using telnet_cs.Client;
    using telnet_cs.IO;

    public class BinaryModeTests
    {
        private static async Task<(string Output, ScriptedStream Stream)> ReadHandlerOnceAsync(
          Action<ByteStreamHandler> configure, params int[] reads)
        {
            var stream = new ScriptedStream(reads);
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1);
            configure(sut);
            var output = await sut.ReadAsync(TimeSpan.FromMilliseconds(50));
            return (output, stream);
        }

        private static async Task<string> ReadClientOnceAsync(Client client)
        {
            return await client.ReadAsync(TimeSpan.FromMilliseconds(100));
        }

        [Theory]
        [InlineData(100, "\u0064")]
        [InlineData(150, "\u0096")]
        [InlineData(200, "È")]
        public async Task UndefinedCommand_IsDeliveredAsDataWithoutReply(int command, string expected)
        {
            // telnetlib3 parity: IAC followed by a byte with no defined
            // command meaning falls through as in-band data (never a reply).
            var (output, stream) = await ReadHandlerOnceAsync(static _ => { }, 255, command);
            output.Should().Be(expected);
            stream.ByteWrites.Should().BeEmpty();
        }

        [Fact]
        public async Task UndefinedCommand_DoesNotDisturbFollowingData()
        {
            var (output, _) = await ReadHandlerOnceAsync(static _ => { }, 255, 200, 72);
            output.Should().Be("ÈH");
        }

        [Fact]
        public async Task HighBytes_PassThroughAfterBinaryAgreement()
        {
            // Peer's WILL TransmitBinary is answered DO; the following high
            // bytes (including a doubled IAC) arrive as Latin-1 chars. Only
            // the inbound direction (peer's WILL) opens inbound 8-bit data.
            var (output, stream) = await ReadHandlerOnceAsync(
              static _ => { }, 255, 251, 0, 128, 200, 254, 255, 255);
            output.Should().Be("\u0080\u00c8\u00fe\u00ff");
            stream.ByteWrites.Should().ContainSingle().Which.Should().Equal(new byte[] { 255, 253, 0 });
        }

        [Fact]
        public async Task HighBytes_WithoutAgreement_AreDropped()
        {
            // The bytes path is 8-bit-clean: bare high bytes arrive as Latin-1
            // even without BINARY. The doubled IAC still decodes as well.
            var (output, stream) = await ReadHandlerOnceAsync(static _ => { }, 200, 255, 255);
            output.Should().Be("\u00c8\u00ff");
            stream.ByteWrites.Should().BeEmpty();
        }

        [Fact]
        public async Task HighBytes_WithoutAgreement_DecodedWhenEncodingExplicit()
        {
            // An explicitly configured TextEncoding opts into 8-bit decoding.
            var (output, _) = await ReadHandlerOnceAsync(
              static h => h.TextEncoding = System.Text.Encoding.Latin1, 200, 255, 255);
            output.Should().Be("\u00c8\u00ff");
        }

        [Fact]
        public async Task BinaryAgreement_ClientLevel_DataFlowsAfterDoBinary()
        {
            using (GlobalStateGuard.SkipProactive(true))
            {
                using var stream = new ScriptedStream();
                using var client = new Client(stream, new CancellationToken());
                stream.Enqueue(255, 253, 0, 65);
                (await ReadClientOnceAsync(client)).Should().Be("A");
                stream.ByteWrites.Should().ContainSingle().Which.Should().Equal(new byte[] { 255, 251, 0 });
            }
        }

        [Fact]
        public void Converter_HighCharsMapToSameBytes()
        {
            ByteStringConverter.ConvertStringToByteArray("\u00c8\u00ff")
              .Should().Equal(new byte[] { 200, 255, 255 });
        }

        [Fact]
        public void Converter_Latin1RoundTrips128To255()
        {
            var chars = new char[128];
            for (var i = 0; i < chars.Length; i++)
            {
                chars[i] = (char)(128 + i);
            }

            var text = new string(chars);
            var expected = new byte[129];
            for (var i = 0; i < 127; i++)
            {
                expected[i] = (byte)(128 + i);
            }

            expected[127] = 255;
            expected[128] = 255;
            ByteStringConverter.ConvertStringToByteArray(text).Should().Equal(expected);
            ByteStringConverter.ToString(expected[0..127]).Should().Be(text[0..127]);
        }
    }
}
