namespace telnet_cs.Tests
{
    using System;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using FluentAssertions;
    using Xunit;
    using telnet_cs.Client;
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
        [InlineData(3, "\u0003A")]   // SGA option number: data, not a command.
        [InlineData(5, "\u0005A")]   // STATUS option number: data.
        [InlineData(7, "\u0007A")]   // BEL: no command meaning after IAC.
        [InlineData(200, "ÈA")]      // Unassigned value: data (never-drop-bytes).
        public async Task IllegalTwoByteIac_IsDeliveredAsDataWithoutReply(int verb, string expected)
        {
            // telnetlib3 parity: IAC followed by a byte with no defined
            // command meaning falls through as in-band data (never a reply).
            var (output, writes) = await ReadScriptedAsync(255, verb, 65);
            output.Should().Be(expected);
            writes.Should().BeEmpty();
        }

        [Fact]
        public async Task IllegalTwoByteTm_IsConsumedSilentlyWithoutReply()
        {
            // Byte 6 (TM) is not a defined RFC 854 command, but telnetlib3
            // registers a NOP callback for it — so unlike other undefined
            // bytes it never becomes data.
            var (output, writes) = await ReadScriptedAsync(255, 6, 65);
            output.Should().Be("A");
            writes.Should().BeEmpty();
        }

        [Fact]
        public async Task StraySeOutsideSb_IsDeliveredAsData()
        {
            // Decided: a bare IAC SE with no open SB block delivers 0xF0 as
            // data (telnetlib3 parity, never-drop-bytes) — never a reply.
            var (output, writes) = await ReadScriptedAsync(255, 240, 65);
            output.Should().Be("ðA");
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
        public async Task EmptySb_WithNoOptionByte_IsDiscardedWithoutReply()
        {
            // Port of test_sb_empty_subnegotiation: IAC SB IAC SE (no option
            // byte at all) is discarded without a reply. The empty frame is
            // consumed as a unit, so no orphaned bytes leak as data.
            // (A framed IAC SE pair would still deliver ð; see
            // StraySeOutsideSb_IsDeliveredAsData.)
            var (output, writes) = await ReadScriptedAsync(255, 250, 255, 240, 111, 107);
            output.Should().Be("ok");
            writes.Should().BeEmpty();
        }

        [Fact]
        public async Task SbOverflow_DiscardsPayloadAndResynchronises()
        {
            // Over-long subnegotiation input keeps being consumed (so the
            // stream resynchronises at IAC SE) but the payload is ignored —
            // including escaped IAC IAC pairs past the cap. The cap matches the
            // reference 1 MiB bound.
            var reads = new int[] { 255, 250, 24 }
                .Concat(Enumerable.Repeat(65, (1 << 20) + 10))
                .Concat(new[] { 255, 255 })
                .Concat(Enumerable.Repeat(66, 10))
                .Concat(new[] { 255, 240, 67, 68 })
                .ToArray();
            var (output, writes) = await ReadScriptedAsync(reads);
            output.Should().Be("CD");
            writes.Should().BeEmpty();
        }

        [Fact]
        public async Task WillTtypeTwice_SendsDontEachTime()
        {
            // A client refuses WILL TTYPE: every refused WILL earns its
            // own DONT — duplicate refusals are idempotent, and a peer
            // that re-sends a request expects a reply per copy.
            var (output, writes) = await ReadScriptedAsync(255, 251, 24, 255, 251, 24);
            output.Should().BeEmpty();
            writes.Should().Equal(255, 254, 24, 255, 254, 24);
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
            // Each refused DO earns its own WONT: duplicate refusals are
            // idempotent on the wire, and a re-sent request still gets a
            // reply.
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
            // Raw reads preserve: CR arrives first, NUL arrives next as data.
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
        public async Task ReadAfterPeerCloseMidSession_ReturnsEmpty()
        {
            // Peer-close after data (not pre-closed): the first read drains
            // the queued text, the post-close read is empty.
            using var stream = new ScriptedStream();
            stream.Enqueue(72, 105);
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1);
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().Be("Hi");
            stream.Close();
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().BeEmpty();
        }

        [Fact]
        public async Task SendGa_AfterClose_WritesNothing()
        {
            // Port of test_send_iac_skipped_when_closing_or_closed: once the
            // stream is closed the GA send is skipped (no ByteWrites).
            using (GlobalStateGuard.SkipProactive(true))
            {
                using var stream = new ScriptedStream();
                using var sut = new Client(stream, TimeSpan.FromMilliseconds(50), default);
                stream.Close();
                await sut.SendGaAsync();
                stream.ByteWrites.Should().BeEmpty();
            }
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
            // Reference readuntil parity: never returns a partial — a missed
            // deadline throws TimeoutException (never ""), EOF or not.
            using var stream = new ScriptedStream("partial-no-terminator");
            using var client = new telnet_cs.Client.Client(stream, new CancellationToken());
            Func<Task> act = () => client.TerminatedReadAsync(":", TimeSpan.FromMilliseconds(300));
            await act.Should().ThrowAsync<TimeoutException>();
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
