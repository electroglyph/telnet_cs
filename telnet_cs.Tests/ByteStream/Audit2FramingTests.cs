namespace telnet_cs.Tests
{
    using System;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using FluentAssertions;
    using Xunit;
    using telnet_cs.IO;

    /// <summary>
    /// Round-2 audit (§1) pins: each test asserts the telnetlib3 framing
    /// behavior, so every test here fails against the current code.
    /// </summary>
    public class Audit2FramingTests
    {
        private static async Task<(string Output, byte[] Writes)> ReadScriptedAsync(params int[] reads)
        {
            using var stream = new ScriptedStream(reads);
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1);
            var output = await sut.ReadAsync(TimeSpan.FromMilliseconds(50));
            return (output, stream.ByteWrites.SelectMany(w => w).ToArray());
        }

        [Fact]
        public async Task SplitEmptySbProbe_DiscardsWithoutPhantomData()
        {
            // audit2 §1 F2-SB-SPLIT.
            // Reference: stream_writer.py:767 (cmd persists while in
            // iac_mbs), :769-776 (IAC toggle + SB-escape path), :795-828
            // (SB kept across feed_byte calls; :808-816 discards
            // IAC SB IAC SE with an empty buffer), :1019-1021 (is_oob).
            // Repro (client writer, feed_byte one-by-one): feed
            // FF FA FF -> inband=b'' sent=b'' iac=True cmd=SB sb=b'';
            // then feed F0 41 -> inband=b'A' sent=b''. Total inband=b'A',
            // sent=b'' — the lone SE never becomes data.
            using var stream = new ScriptedStream(255, 250, 255);
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1);
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().BeEmpty();
            stream.Enqueue(240, 65);
            var second = await sut.ReadAsync(TimeSpan.FromMilliseconds(50));
            second.Should().Be("A");
            stream.ByteWrites.SelectMany(w => w).ToArray().Should().BeEmpty();
        }

        [Fact]
        public async Task InterruptedSb_DispatchesInnerNegotiationWithoutDataLeak()
        {
            // audit2 §1 F2-SB-INTERRUPT.
            // Reference: stream_writer.py:795-807 (warns "interrupted by
            // IAC", clears _sb_buffer), :847-888 (dispatches the inner
            // verb; :852-857 DO path + pending clear), :2096-2097
            // (DO SGA -> WILL).
            // Repro (both roles): feed FF FA 1F 41 42 FF FD 03 43
            // (IAC SB NAWS "AB" IAC DO SGA "C") -> inband=b'C' (0x43),
            // sent=FF FB 03. No "AB"/0x03 leak; inner DO answered WILL SGA.
            var (output, writes) = await ReadScriptedAsync(255, 250, 31, 65, 66, 255, 253, 3, 67);
            output.Should().Be("C");
            writes.Should().Equal(255, 251, 3);
        }

        [Fact]
        public async Task InterruptProcess_DoesNotTruncatePendingRead()
        {
            // audit2 §1 1.23.
            // Reference: stream_writer.py:1609-1611 (handle_ip only
            // log.debug — no send, no state change).
            // Repro (both roles): feed FF F4 41 42 (IAC IP "AB") ->
            // inband=b'AB' sent=b''. IP is consumed and logged; the
            // in-flight read keeps delivering.
            var (output, writes) = await ReadScriptedAsync(255, 244, 65, 66);
            output.Should().Be("AB");
            writes.Should().BeEmpty();
        }

        [Fact]
        public async Task IacInOptionPosition_SixByteForm_AnswersWont()
        {
            // audit2 §1 1.27.
            // Reference: stream_writer.py:847-850 (3rd byte -> cmd,opt),
            // :852-854 (handle_do dispatch), :2118-2125 (unknown option ->
            // WONT + rejected_do).
            // Repro (both roles, feed_byte trace): FF->iac, FD->cmd=DO,
            // FF->iac/cmd=DO, FF->iac cleared/cmd=DO, FB->dispatch
            // DO opt=0xFB -> sent=FF FC FB, FF->iac parked. Final
            // inband=b'' sent=FF FC FB (IAC WONT 0xFB).
            var (output, writes) = await ReadScriptedAsync(255, 253, 255, 255, 251, 255);
            output.Should().BeEmpty();
            writes.Should().Equal(255, 252, 251);
        }
    }
}
