namespace telnet_cs.Tests
{
    using System;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using FluentAssertions;
    using Xunit;
    using telnet_cs.IO;
    using telnet_cs.Transport;

    public class CoreFramingTests
    {
        private static async Task<(string Output, byte[] Writes)> ReadScriptedAsync(params int[] reads)
        {
            using var stream = new ScriptedStream(reads);
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1);
            var output = await sut.ReadAsync(TimeSpan.FromMilliseconds(50));
            return (output, stream.ByteWrites.SelectMany(w => w).ToArray());
        }

        [Theory]
        [InlineData(3)]   // SGA option number: illegal as a 2-byte command.
        [InlineData(5)]   // STATUS option number.
        [InlineData(7)]   // BEL: no command meaning after IAC.
        [InlineData(200)] // Unassigned value.
        public async Task IllegalTwoByteIac_IsConsumedSilentlyWithoutReply(int verb)
        {
            // RFC 856 §5: IAC followed by a byte that is not a defined TELNET
            // command has the same meaning as IAC NOP — consume it silently
            // (never data, never a reply).
            var (output, writes) = await ReadScriptedAsync(255, verb, 65);
            output.Should().Be("A");
            writes.Should().BeEmpty();
        }

        [Fact]
        public async Task StraySeOutsideSb_IsConsumedSilently()
        {
            var (output, writes) = await ReadScriptedAsync(255, 240, 65);
            output.Should().Be("A");
            writes.Should().BeEmpty();
        }

        [Fact]
        public async Task EmptySb_ForNonEmptyOnlyOption_IsDiscardedWithoutReply()
        {
            var (output, writes) = await ReadScriptedAsync(255, 250, 5, 255, 240, 65);
            output.Should().Be("A");
            writes.Should().BeEmpty();
        }

        [Fact]
        public async Task SbOverflow_DiscardsPayloadAndResynchronises()
        {
            // Over-long subnegotiation input keeps being consumed (so the
            // stream resynchronises at IAC SE) but the payload is ignored —
            // including escaped IAC IAC pairs past the cap.
            var reads = new int[] { 255, 250, 24 }
                .Concat(Enumerable.Repeat(65, 600))
                .Concat(new[] { 255, 255 })
                .Concat(Enumerable.Repeat(66, 10))
                .Concat(new[] { 255, 240, 67, 68 })
                .ToArray();
            var (output, writes) = await ReadScriptedAsync(reads);
            output.Should().Be("CD");
            writes.Should().BeEmpty();
        }

        [Fact]
        public async Task WillTtypeTwice_SendsSingleDo()
        {
            // Agreement dedups (second WILL arrives in YES state): mirrors
            // telnetlib3's DO-ECHO-twice single-WILL pin on the agree path.
            var (output, writes) = await ReadScriptedAsync(255, 251, 24, 255, 251, 24);
            output.Should().BeEmpty();
            writes.Should().Equal(255, 253, 24);
        }

        [Fact]
        public async Task DoSgaTwice_SendsSingleWill()
        {
            var (output, writes) = await ReadScriptedAsync(255, 253, 3, 255, 253, 3);
            output.Should().BeEmpty();
            writes.Should().Equal(255, 251, 3);
        }

        [Fact]
        public async Task DoEchoTwice_RefusedWithWontEachTime()
        {
            // Refusals repeat: the RFC 1143 §7 NO row has no EMPTY/OPPOSITE
            // split, so each DO answered from NO re-sends the refusal. Only
            // agreements dedup.
            var (output, writes) = await ReadScriptedAsync(255, 253, 1, 255, 253, 1);
            output.Should().BeEmpty();
            writes.Should().Equal(255, 252, 1, 255, 252, 1);
        }

        [Fact]
        public async Task DontWhenAlreadyOff_SendsNothing()
        {
            var (output, writes) = await ReadScriptedAsync(255, 254, 1, 255, 254, 1);
            output.Should().BeEmpty();
            writes.Should().BeEmpty();
        }

        [Fact]
        public async Task WontEchoTwice_SendsNothing()
        {
            var (output, writes) = await ReadScriptedAsync(255, 252, 1, 255, 252, 1);
            output.Should().BeEmpty();
            writes.Should().BeEmpty();
        }

        [Fact]
        public async Task DoEchoOnce_RefusedWithSingleWont()
        {
            // Unhandled DO (ECHO without AllowRemoteEcho opt-in) is refused.
            var (output, writes) = await ReadScriptedAsync(255, 253, 1);
            output.Should().BeEmpty();
            writes.Should().Equal(255, 252, 1);
        }

        [Fact]
        public async Task SplitCrNul_LeaksNulAsData()
        {
            // CR NUL collapses only when the NUL is already available at the
            // peek; a split CR…NUL surfaces the NUL as data on the next read.
            using var stream = new ScriptedStream(65, 13);
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1);
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().Be("A\r");
            stream.Enqueue(0);
            var second = await sut.ReadAsync(TimeSpan.FromMilliseconds(50));
            second.Should().Be("\0");
        }

        [Fact]
        public async Task ReadAfterClose_ReturnsEmpty()
        {
            using var stream = new ScriptedStream();
            stream.Close();
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1);
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().BeEmpty();
        }

        [Fact]
        public async Task PlainData_ProducesNoSpontaneousOutput()
        {
            // No subnegotiation reply (e.g. TSPEED IS) is ever volunteered:
            // answers only follow explicit SEND requests.
            var (output, writes) = await ReadScriptedAsync(104, 105);
            output.Should().Be("hi");
            writes.Should().BeEmpty();
        }

        [Fact(Timeout = 5000)]
        public async Task TerminatedRead_Eof_ReturnsPartialWithoutThrow()
        {
            // No IncompleteReadError counterpart: unterminated text passes
            // through untouched after the timeout, EOF or not.
            using var stream = new ScriptedStream("partial-no-terminator");
            using var client = new telnet_cs.Client.Client(stream, new CancellationToken());
            (await client.TerminatedReadAsync(":", TimeSpan.FromMilliseconds(300))).Should().Be("partial-no-terminator");
        }

        [Fact]
        public async Task EorSend_GatedUntilDoReceived()
        {
            using var stream = new ScriptedStream(255, 253, 25);
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1);
            (await sut.SendEorAsync()).Should().BeFalse();
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().BeEmpty();
            (await sut.SendEorAsync()).Should().BeTrue();
            stream.ByteWrites.SelectMany(w => w).ToArray().Should().Equal(255, 251, 25, 255, 239);
        }
    }
}
