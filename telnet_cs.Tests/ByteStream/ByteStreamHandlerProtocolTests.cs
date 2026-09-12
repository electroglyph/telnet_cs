namespace telnet_cs.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using FakeItEasy;
    using FluentAssertions;
    using Xunit;
    using telnet_cs.IO;
    using telnet_cs.Transport;

    public class ByteStreamHandlerProtocolTests
    {
        private static IByteStream FakeStreamOnce(int[] reads)
        {
            var fake = A.Fake<IByteStream>();
            var first = true;
            A.CallTo(() => fake.Connected).Returns(true);
            A.CallTo(() => fake.Available).ReturnsLazily(() =>
            {
                if (first)
                {
                    first = false;
                    return 1;
                }

                return 0;
            });
            A.CallTo(() => fake.ReadByte()).ReturnsNextFromSequence(reads);
            return fake;
        }

        private static async Task<string> ReadOnceAsync(int[] reads)
        {
            var fake = FakeStreamOnce(reads);
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(fake, cts, 1);
            return await sut.ReadAsync(TimeSpan.FromMilliseconds(50));
        }

        private static async Task<string> ReadScriptedAsync(params int[] reads)
        {
            return (await ReadScriptedWithWritesAsync(reads)).Output;
        }

        private static async Task<(string Output, IReadOnlyList<byte[]> Writes)> ReadScriptedWithWritesAsync(params int[] reads)
        {
            using var stream = new ScriptedStream(reads);
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1);
            var output = await sut.ReadAsync(TimeSpan.FromMilliseconds(50));
            return (output, stream.ByteWrites);
        }

        private static Task<string> ReadScriptedTextAsync(string text) =>
            ReadScriptedAsync(Encoding.Latin1.GetBytes(text).Select(b => (int)b).ToArray());

        [Theory]
        // Port of test_telnet_reader_using_readline_unicode and
        // test_telnet_reader_using_readline_bytes (same 9 vectors, bytes and
        // unicode): strict-NVT line-break handling. telnetlib3 blocks on
        // unterminated tails until EOF; C# returns what arrived when the read
        // ends (CONFLICT on "---\r"/"xxxxxxxxxxx", left failing if it diverges).
        [InlineData("alpha\r\0", "alpha\r")]
        [InlineData("bravo\r\n", "bravo\r\n")]
        [InlineData("charlie\n", "charlie\n")]
        [InlineData("---\r", "---\r")]
        [InlineData("\r\0", "\r")]
        [InlineData("\n", "\n")]
        [InlineData("\r\n", "\r\n")]
        [InlineData("xxxxxxxxxxx", "xxxxxxxxxxx")]
        public async Task ReadlineVectors_MatchStrictNvtTable(string input, string expected)
        {
            (await ReadScriptedTextAsync(input)).Should().Be(expected);
        }

        [Theory]
        [InlineData(1, "\x01")]   // SOH: data, never expanded
        [InlineData(2, "\x02")]   // STX: data, never TAB
        [InlineData(3, "\x03")]   // ETX: data, never "^C"
        [InlineData(4, "\x04")]   // EOT: data, never "^D"
        [InlineData(5, "\x05")]   // ENQ: data, never an ACK side effect
        [InlineData(6, "\x06")]   // ACK: data, never dropped
        [InlineData(7, "\x07")]   // BEL: data, never a beep
        [InlineData(8, "\x08")]   // BS: data, never destructive
        [InlineData(9, "\t")]      // HT passes through (no TAB rewriting without LINEMODE)
        [InlineData(11, "\x0B")]   // VT: data, never NewLine
        [InlineData(12, "\x0C")]   // FF: data, never NewLine
        [InlineData(21, "\x15")]   // NAK: data, never message text
        [InlineData(31, "\x1F")]   // US: data, never ","
        [InlineData(65, "A")]      // default passthrough
        [InlineData(66, "B")]
        public async Task ControlCharsArriveVerbatim(int input, string expected)
        {
            (await ReadOnceAsync(new[] { input })).Should().Be(expected);
        }

        [Fact]
        public async Task MidSbVerb_AbortsFramingWithoutReply()
        {
            // IAC DO inside SB STATUS loses framing: the partial is discarded, the
            // verb never dispatches (no negotiation reply), and the stream resyncs
            // as data — the orphaned option byte 65 surfaces as "A", the trailing
            // stray IAC SE delivers 0xF0, then the trailing text.
            var (output, writes) = await ReadScriptedWithWritesAsync(255, 250, 5, 1, 255, 253, 65, 255, 240, 66, 67);
            output.Should().Be("AðBC");
            writes.Should().BeEmpty();
        }

        [Fact]
        public async Task MidSbTm_AbortsFramingWithoutReply()
        {
            // Port of test_sb_interrupted (TM half): IAC TM inside SB STATUS
            // aborts framing exactly like the DO variant — the partial is
            // discarded, the TM itself is consumed by the abort (never a
            // command, never data), and the stream resyncs as data.
            var (output, writes) = await ReadScriptedWithWritesAsync(255, 250, 5, 1, 255, 6, 65, 255, 240, 66, 67);
            output.Should().Be("AðBC");
            writes.Should().BeEmpty();
        }

        [Fact]
        public async Task NakDeliveredVerbatim()
        {
            // Decided: NAK is data, never message text.
            (await ReadOnceAsync(new[] { 21 }))
              .Should().Be("\x15");
        }

        [Fact]
        public async Task EraseLine_AfterMarker_ErasesWithoutThrow()
        {
            // Consecutive out-of-band commands must not throw out of ReadAsync
            // (both are consumed without touching the buffer).
            var (output, writes) = await ReadScriptedWithWritesAsync(255, 243, 255, 248);
            output.Should().BeEmpty();
            writes.Should().BeEmpty();
        }

        [Fact]
        public async Task Backspace_DeliveredVerbatim()
        {
            // Decided: BS is data, never destructive — the delivery buffer is
            // the application's byte record.
            using var stream = new ScriptedStream(65, 255, 243, 8);
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1) { TextEncoding = Encoding.Latin1 };
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().Be("A\x08");
        }

        [Fact]
        public async Task BellDeliveredVerbatim()
        {
            // Decided: BEL is data, never a console beep.
            var act = async () => await ReadOnceAsync(new[] { 7 });
            (await act()).Should().Be("\x07");
        }

        [Fact]
        public async Task StreamEndReturnsEmpty()
        {
            (await ReadOnceAsync(new[] { -1 })).Should().BeEmpty();
        }

        [Fact]
        public async Task EscapedIacYieldsSingle255Char()
        {
            // P0.1: IAC IAC decodes to one (char)255, not the decimal string "255".
            (await ReadOnceAsync(new[] { 255, 255 })).Should().Be("\u00ff");
        }

        [Fact]
        public async Task EscapedIacEmbeddedInTextDecodesInline()
        {
            var result = await ReadScriptedAsync(65, 255, 255, 66);
            result.Should().Be("A\u00ffB");
        }

        [Fact]
        public async Task ConsecutiveEscapedIacsYieldOneCharEach()
        {
            var result = await ReadScriptedAsync(255, 255, 255, 255);
            result.Should().Be("\u00ff\u00ff");
        }

        [Fact]
        public async Task EscapedIacRecordsSingleRawByteUnderLatin1()
        {
            // Exercises the rawBytes path: the buggy code recorded ASCII("255")
            // (3 bytes -> "255"); the fix records a single 0xFF (-> "ÿ").
            var fake = FakeStreamOnce(new[] { 255, 255 });
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(fake, cts, 1) { TextEncoding = Encoding.Latin1 };
            var result = await sut.ReadAsync(TimeSpan.FromMilliseconds(50));
            result.Should().Be("\u00ff");
        }

        [Fact]
        public async Task EscapedIacFollowedByCommandDecodesCharThenConsumesCommand()
        {
            // IAC IAC -> ÿ, then IAC NOP is consumed silently.
            var result = await ReadScriptedAsync(255, 255, 255, 241);
            result.Should().Be("\u00ff");
        }

        [Fact]
        public async Task TruncatedIacYieldsEmpty()
        {
            (await ReadOnceAsync(new[] { 255, -1 })).Should().BeEmpty();
        }

        [Fact]
        public async Task EnquirySendsNoAck()
        {
            // Decided: ENQ is data and must not emit an unsolicited ACK.
            var fake = FakeStreamOnce(new[] { 5 });
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(fake, cts, 1);
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().Be("\x05");
            A.CallTo(() => fake.WriteByteAsync(A<byte>.Ignored, A<CancellationToken>.Ignored)).MustNotHaveHappened();
        }

        [Theory]
        // verb, option, expected reply verb
        [InlineData(253, 3, 251)]   // DO SGA -> WILL SGA
        [InlineData(251, 3, 253)]   // WILL SGA -> DO SGA
        [InlineData(253, 24, 251)]  // DO TT -> WILL
        [InlineData(251, 24, 253)]  // WILL TT -> DO
        [InlineData(253, 32, 251)]  // DO TS -> WILL
        [InlineData(251, 32, 253)]  // WILL TS -> DO
        [InlineData(253, 1, 252)]   // DO Echo without opt-in -> WONT (see EchoTests)
        [InlineData(251, 1, 253)]   // WILL Echo -> DO; local echo suppressed instead (see EchoTests)
        [InlineData(253, 99, 252)]   // DO unknown -> WONT
        [InlineData(251, 99, 254)]   // WILL unknown -> DONT
        [InlineData(253, 241, 252)]  // DO NOP (command-as-option) -> WONT
        public async Task ReplyToCommandTable(int verb, int option, int expectedReply)
        {
            var fake = FakeStreamOnce(new[] { 255, verb, option });
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(fake, cts, 1);
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().BeEmpty();
            A.CallTo(() => fake.WriteAsync(A<byte[]>.Ignored, 0, 3, A<CancellationToken>.Ignored))
              .WhenArgumentsMatch(o => o[0] is byte[] b && b[0] == 255 && b[1] == (byte)expectedReply && b[2] == (byte)option)
              .MustHaveHappened();
        }

        [Fact]
        public async Task DoWindowSizeRepliesWillPlusBareNawsFollowUp()
        {
            var fake = FakeStreamOnce(new[] { 255, 253, 31 });
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(fake, cts, 1);
            sut.WindowWidth = 80;
            sut.WindowHeight = 24;
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(100))).Should().BeEmpty();
            // First reply: IAC WILL WS
            A.CallTo(() => fake.WriteAsync(A<byte[]>.Ignored, 0, 3, A<CancellationToken>.Ignored))
              .WhenArgumentsMatch(o => o[0] is byte[] b && b[0] == 255 && b[1] == 251 && b[2] == 31)
              .MustHaveHappened();
            // NAWS follow-up (bare RFC 1073 shape, no IS verb): IAC SB WS <width-hi> <width-lo> <height-hi> <height-lo> IAC SE
            var expectedNaws = new byte[] { 255, 250, 31, 0, 80, 0, 24, 255, 240 };
            A.CallTo(() => fake.WriteAsync(A<byte[]>.Ignored, 0, expectedNaws.Length, A<CancellationToken>.Ignored))
              .WhenArgumentsMatch(o => o[0] is byte[] b && b.SequenceEqual(expectedNaws))
              .MustHaveHappenedOnceExactly();
        }

        [Theory]
        [InlineData(254, 1)]  // DONT Echo ignored
        [InlineData(254, 3)]  // DONT SGA ignored
        [InlineData(252, 3)]  // WONT SGA ignored
        [InlineData(252, 1)]  // WONT Echo ignored
        public async Task DontWontAreIgnoredWithoutReply(int verb, int option)
        {
            var fake = FakeStreamOnce(new[] { 255, verb, option });
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(fake, cts, 1);
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().BeEmpty();
            A.CallTo(() => fake.WriteAsync(A<byte[]>.Ignored, A<int>.Ignored, A<int>.Ignored, A<CancellationToken>.Ignored)).MustNotHaveHappened();
            A.CallTo(() => fake.WriteByteAsync(A<byte>.Ignored, A<CancellationToken>.Ignored)).MustNotHaveHappened();
        }

        [Fact]
        public async Task UnknownVerbIsIgnoredWithoutReply()
        {
            var fake = FakeStreamOnce(new[] { 255, 241 }); // IAC NOP
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(fake, cts, 1);
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().BeEmpty();
            A.CallTo(() => fake.WriteAsync(A<byte[]>.Ignored, A<int>.Ignored, A<int>.Ignored, A<CancellationToken>.Ignored)).MustNotHaveHappened();
        }

        [Fact]
        public async Task TruncatedOptionAfterDoIsIgnoredWithoutReply()
        {
            var fake = FakeStreamOnce(new[] { 255, 253, -1 });
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(fake, cts, 1);
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().BeEmpty();
            A.CallTo(() => fake.WriteAsync(A<byte[]>.Ignored, A<int>.Ignored, A<int>.Ignored, A<CancellationToken>.Ignored)).MustNotHaveHappened();
        }

        [Fact]
        public async Task InterruptProcessCancelsPendingRead()
        {
            var fake = FakeStreamOnce(new[] { 255, 244 }); // IAC IP -> CancelPendingReads()
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(fake, cts, 1);
            var sw = Stopwatch.StartNew();
            var result = await sut.ReadAsync(TimeSpan.FromMilliseconds(2000));
            sw.Stop();
            result.Should().BeEmpty();
            sw.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(1000));
        }

        [Fact]
        public async Task SubnegotiationTerminalTypeSendRepliesWithVt100()
        {
            using (GlobalStateGuard.TerminalType("vt100"))
            {
                var fake = FakeStreamOnce(new[] { 255, 250, 24, 1, 255, 240 });
                using var cts = new CancellationTokenSource();
                using var sut = new ByteStreamHandler(fake, cts, 1);
                (await sut.ReadAsync(TimeSpan.FromMilliseconds(100))).Should().BeEmpty();
                var expected = new byte[] { 255, 250, 24, 0 }
                  .Concat(Encoding.ASCII.GetBytes("vt100"))
                  .Concat(new byte[] { 255, 240 }).ToArray();
                A.CallTo(() => fake.WriteAsync(A<byte[]>.Ignored, 0, expected.Length, A<CancellationToken>.Ignored))
                  .WhenArgumentsMatch(o => o[0] is byte[] b && b.SequenceEqual(expected))
                  .MustHaveHappened();
            }
        }

        [Fact]
        public async Task SubnegotiationTerminalSpeedSendRepliesWithSpeed()
        {
            using (GlobalStateGuard.TerminalSpeed("19200,19200"))
            {
                var fake = FakeStreamOnce(new[] { 255, 250, 32, 1, 255, 240 });
                using var cts = new CancellationTokenSource();
                using var sut = new ByteStreamHandler(fake, cts, 1);
                (await sut.ReadAsync(TimeSpan.FromMilliseconds(100))).Should().BeEmpty();
                var expected = new byte[] { 255, 250, 32, 0 }
                  .Concat(Encoding.ASCII.GetBytes("19200,19200"))
                  .Concat(new byte[] { 255, 240 }).ToArray();
                A.CallTo(() => fake.WriteAsync(A<byte[]>.Ignored, 0, expected.Length, A<CancellationToken>.Ignored))
                  .WhenArgumentsMatch(o => o[0] is byte[] b && b.SequenceEqual(expected))
                  .MustHaveHappened();
            }
        }

        [Fact]
        public async Task SubnegotiationUnknownOptionSendsNoReply()
        {
            var fake = FakeStreamOnce(new[] { 255, 250, 99, 1, 255, 240 });
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(fake, cts, 1);
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().BeEmpty();
            A.CallTo(() => fake.WriteAsync(A<byte[]>.Ignored, A<int>.Ignored, A<int>.Ignored, A<CancellationToken>.Ignored)).MustNotHaveHappened();
        }

        [Fact]
        public async Task SubnegotiationMalformedSendsWont()
        {
            // SEND missing (0 instead of 1) -> fallback IAC WONT opt
            var fake = FakeStreamOnce(new[] { 255, 250, 24, 0, 255, 240 });
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(fake, cts, 1);
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().BeEmpty();
            A.CallTo(() => fake.WriteAsync(A<byte[]>.Ignored, 0, 3, A<CancellationToken>.Ignored))
              .WhenArgumentsMatch(o => o[0] is byte[] b && b[0] == 255 && b[1] == 252 && b[2] == 24)
              .MustHaveHappened();
        }

        [Fact]
        public async Task HandlerDisposeDoesNotDisposeExternalStream()
        {
            // PROPER (test.md §3.5): the handler must not dispose a stream it does
            // not own. Currently fails: Dispose() disposes the stream, which forces
            // Client to leak the handler (CA2000) as a workaround.
            var fake = A.Fake<IByteStream>();
            A.CallTo(() => fake.Connected).Returns(true);
            using var cts = new CancellationTokenSource();
            var sut = new ByteStreamHandler(fake, cts, 1);
            sut.Dispose();
            A.CallTo(() => fake.Dispose()).MustNotHaveHappened();
            await Task.CompletedTask;
        }

        [Fact]
        public async Task ReadSetsReceiveTimeout()
        {
            var fake = FakeStreamOnce(new[] { 65 });
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(fake, cts, 1);
            await sut.ReadAsync(TimeSpan.FromMilliseconds(50));
            A.CallToSet(() => fake.ReceiveTimeout).To(50).MustHaveHappened();
        }
    }
}
