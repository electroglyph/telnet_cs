namespace telnet_cs.Tests
{
    using System;
    using System.IO;
    using System.IO.Compression;
    using System.Linq;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using FluentAssertions;
    using Xunit;
    using telnet_cs.Client;
    using telnet_cs.IO;
    using telnet_cs.Transport;

    /// <summary>
    /// Data-path tests: data bytes must reach the reader verbatim
    /// (telnetlib3 <c>_process_data_chunk</c> forwards every non-IAC
    /// byte; RFC 854 NVT printer defines no expansions).
    /// </summary>
    public class ByteStreamDataPathTests
    {
        private static async Task<(string Output, byte[] Writes, byte[] Singles)> ReadScriptedAsync(params int[] reads)
        {
            using var stream = new ScriptedStream(reads);
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1);
            var output = await sut.ReadAsync(TimeSpan.FromMilliseconds(50));
            return (output, stream.ByteWrites.SelectMany(w => w).ToArray(), stream.SingleByteWrites.ToArray());
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(6)]
        [InlineData(7)]
        [InlineData(8)]
        [InlineData(21)]
        [InlineData(31)]
        public async Task ControlBytes_ArriveVerbatim(int controlByte)
        {
            // No "^C" / "\n \n" / "NAK: ..." expansions, no drops, no
            // destructive backspace handling in the data path.
            var (output, _, _) = await ReadScriptedAsync(controlByte, 65);
            output.Should().Be(((char)controlByte).ToString() + "A");
        }

        [Fact]
        public async Task Ayt_ConsumedWithoutReply()
        {
            // AYT earns no proof-alive bytes — answering would inject
            // peer-visible data outside any framing the caller controls.
            var (output, writes, _) = await ReadScriptedAsync(255, 246);
            output.Should().BeEmpty();
            writes.Should().BeEmpty();
        }

        [Theory]
        [InlineData(247)]
        [InlineData(248)]
        public async Task EraseCommands_LeaveBufferUntouched(int command)
        {
            // EC/EL are consumed without editing — the delivery buffer
            // is the application's byte record, not a terminal line.
            var (output, writes, _) = await ReadScriptedAsync(65, 66, 255, command);
            output.Should().Be("AB");
            writes.Should().BeEmpty();
        }

        [Fact]
        public async Task Enquiry_SendsNoAck()
        {
            // ENQ must not emit an unsolicited ACK wire byte.
            var (output, _, singles) = await ReadScriptedAsync(5);
            output.Should().Be("\u0005");
            singles.Should().BeEmpty();
        }

        [Fact]
        public async Task HighBytes_PreservedWithoutBinary()
        {
            // Source of truth: telnetlib3 never drops data bytes in the framing path.
            // ~/telnetlib3/telnetlib3/_base.py _process_data_chunk scans only for IAC (255)
            // and forwards every other byte to reader.feed_data, including bytes > 127.
            // BINARY (RFC 856) only selects the unicode decoder:
            // ~/telnetlib3/telnetlib3/server.py encoding() returns "US-ASCII" unless
            // BINARY was negotiated, but the raw bytes are already buffered in
            // TelnetReader._buffer either way. RFC 854 NVT printer takes no action on
            // remaining codes; it never deletes them.
            // Our code: telnet_cs/IO/ByteStreamHandler.cs:1036 drops input > 127 when
            // BINARY was not agreed and no TextEncoding/ForceBinaryDecoding is set, and
            // telnet_cs/IO/ByteStringConverter.cs:11-15 claims the null-encoding path is
            // 8-bit-clean, so the drop contradicts our own converter contract.
            // Proof: wire [200, 65] (0xC8 'A') must arrive as two chars "\u00C8A";
            // returning only "A" proves a data-loss drop. This test is correct per the
            // reference bytes path; the fix is to gate only charset decoding, never the
            // byte delivery itself.
            var (output, _, _) = await ReadScriptedAsync(200, 65);
            output.Should().Be("ÈA");
        }

        [Theory]
        [InlineData(243)]
        [InlineData(236)]
        [InlineData(237)]
        [InlineData(238)]
        public async Task OutOfBandSignals_ConsumedNotText(int command)
        {
            // BRK/EOF/SUSP/ABORT are consumed, never "[BRK]" text.
            var (output, _, _) = await ReadScriptedAsync(255, command);
            output.Should().BeEmpty();
        }

        [Fact]
        public async Task CrNul_PreservedInRawRead()
        {
            // Source of truth: telnetlib3 splits raw read vs line read.
            // ~/telnetlib3/telnetlib3/stream_reader.py read() drains TelnetReader._buffer
            // verbatim with no CR handling; only readline() trims CR NUL to CR while
            // preserving CR LF. RFC 854 p.11 defines CR NUL as the wire spelling of a
            // lone CR for the NVT printer, i.e. a presentation mapping, not a transport
            // deletion.
            // Our code: telnet_cs/IO/ByteStreamHandler.cs:1031-1033 appends "\r" and sets
            // sawCrAwaitingNul, then swallows a following NUL even in raw ReadAsync, so
            // raw consumers lose a byte that the reference preserves.
            // Proof: wire [65, 13, 0] ("A" CR NUL) must read back as three chars
            // "A\r\0" in a raw read; collapsing to "A\r" belongs only in the line
            // helper (TerminatedReadAsync), matching readline(). This test is correct.
            var (output, _, _) = await ReadScriptedAsync(65, 13, 0);
            output.Should().Be("A\r\0");
        }

        [Theory]
        [InlineData(11)]
        [InlineData(12)]
        public async Task VerticalTabFormFeed_PreservedVerbatim(int controlByte)
        {
            // No platform-dependent Environment.NewLine mapping.
            var (output, _, _) = await ReadScriptedAsync(controlByte);
            output.Should().Be(((char)controlByte).ToString());
        }

        [Fact]
        public async Task LargeSubnegotiation_Dispatched()
        {
            // Source of truth: telnetlib3 caps subnegotiation at 1 MiB.
            // ~/telnetlib3/telnetlib3/stream_writer.py:112 _MAX_SUBNEGOTIATION = 1 << 20,
            // with consume-to-IAC-SE resync past the cap and dispatch to
            // handle_subnegotiation on IAC SE below the cap.
            // Our code: telnet_cs/IO/ByteStreamHandler.cs:20 caps at 512 bytes with the
            // same resync shape at :1452/1471, so frames of 513..1048576 bytes that the
            // reference dispatches are silently dropped here (large NEW-ENVIRON IS,
            // MSSP, GMCP, MSDP).
            // Proof: SB TTYPE SEND + 600 x 'A' + IAC SE is well under 1 MiB, so the
            // reference answers SB TTYPE IS; asserting ContainsFrame(... [255,250,24,0])
            // fails only while the 512 cap is in place. This test is correct; the fix
            // is to raise the cap to the reference value while keeping resync.
            using var stream = new ScriptedStream([255, 253, 24]);
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1);
            await sut.ReadAsync(TimeSpan.FromMilliseconds(50));
            stream.Enqueue([255, 250, 24, 1, .. Enumerable.Repeat(65, 600), 255, 240]);
            await sut.ReadAsync(TimeSpan.FromSeconds(2));
            byte[] writes = stream.ByteWrites.SelectMany(w => w).ToArray();
            ContainsFrame(writes, new byte[] { 255, 250, 24, 0 }).Should().BeTrue("large TTYPE SEND deserves an IS answer");
        }

        [Fact]
        public async Task ClientSplitIacWill_RepliedOnSecondRead()
        {
            // Source of truth: Telnet framing is stream-oriented, so a command split
            // across TCP segments must reassemble. telnetlib3 keeps per-connection
            // framing state (stream_writer.py iac_received/cmd_received/_sb_buffer) that
            // survives across data_received calls. RFC 854 defines WILL = 251 and
            // DO = 253 (see telnet_cs/Protocol/Commands.cs:39,43); a peer WILL SGA is
            // answered with DO SGA when no request is outstanding (RFC 1143 NO + WILL
            // -> YES + DO). A WILL that merely acks an outstanding DO is silent, so
            // the default proactive DO SGA (Client.Connect.cs:299-302) must be
            // suppressed here; otherwise the split WILL would correctly earn no reply
            // and a count of 2 could never pass even with perfect reassembly.
            // Our code: telnet_cs/IO/ByteStreamHandler.cs:34,40 keeps pendingIac and
            // pendingVerb per handler instance, but telnet_cs/Client/Client.cs builds a
            // new handler per top-level ReadAsync and round-trips only SbResumeState
            // (see StateHydration.cs), so a trailing IAC in one read plus WILL SGA in
            // the next is lost and never answered.
            // Proof: with proactive suppressed, feed [255] in one ReadAsync then
            // [251, 3] (IAC WILL SGA split) in the next; the reference reassembles and
            // answers a single DO SGA. Zero answers proves the split state was lost.
            // This setup previously used the default proactive client and expected 2,
            // which conflated the ack-silence rule with the split bug; it now isolates
            // the split path. This test is correct as fixed.
            using var stream = new ScriptedStream(255);
            using var client = new Client(stream, TimeSpan.FromMilliseconds(50), CancellationToken.None, [], skipProactiveNegotiation: true);
            (await client.ReadAsync(TimeSpan.FromMilliseconds(200))).Should().BeEmpty();
            stream.Enqueue(251, 3);
            await client.ReadAsync(TimeSpan.FromMilliseconds(200));
            byte[] writes = stream.ByteWrites.SelectMany(w => w).ToArray();
            CountOccurrences(writes, new byte[] { 255, 253, 3 }).Should().Be(1, "split WILL SGA reassembled across reads earns one DO SGA");
        }

        [Fact]
        public async Task RawDeflateStream_ResumesPlaintext()
        {
            // Source of truth: telnetlib3 detects deflate-stream end via zlib eof and
            // unused_data, then resumes plaintext. See
            // ~/telnetlib3/telnetlib3/client_base.py _mccp2 path and server_base.py:
            // on decompressor.eof the wrapper ends MCCP and reprocesses unused bytes as
            // plaintext; the next chunk with no decompressor goes straight to the
            // reader. The bundled MCCP spec (docs/mud-protocols/mccp.md) defines zlib
            // framing with resume; raw-deflate here exercises the reference raw
            // fallback (wbits=-MAX) which also sets eof on the final block.
            // Our code: telnet_cs/IO/MccpDecompressor.cs never sets StreamEnded for raw
            // deflate (no footer to confirm), so Mccp2Active stays set and the trailing
            // "YO" enters the inflater instead of the reader.
            // Proof: WILL 86 + empty SB 86 (MCCP2 handshake) + raw-deflate("HI") must
            // read "HI", then plaintext [89, 79] ("YO") must read "YO"; returning
            // garbage/empty for the second read proves end-detection is missing. This
            // test is correct per the reference resume behavior.
            using var stream = new ScriptedStream();
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1) { EnableMccp = true };
            stream.Enqueue(255, 251, 86);
            await sut.ReadAsync(TimeSpan.FromMilliseconds(200));
            stream.Enqueue(255, 250, 86, 255, 240);
            await sut.ReadAsync(TimeSpan.FromMilliseconds(200));
            stream.Enqueue(RawDeflate("HI").Select(b => (int)b).ToArray());
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(500))).Should().Be("HI");
            stream.Enqueue(89, 79);
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(500))).Should().Be("YO");
        }

        private static byte[] RawDeflate(string text)
        {
            using var ms = new MemoryStream();
            using (var deflate = new DeflateStream(ms, CompressionLevel.NoCompression, leaveOpen: true))
            {
                deflate.Write(Encoding.ASCII.GetBytes(text));
            }

            return ms.ToArray();
        }

        private static int CountOccurrences(byte[] writes, byte[] frame)
        {
            int count = 0;
            for (int i = 0; i + frame.Length <= writes.Length; i++)
            {
                if (ContainsAt(writes, i, frame))
                {
                    count++;
                }
            }

            return count;
        }

        private static bool ContainsFrame(byte[] writes, byte[] frame)
        {
            for (int i = 0; i + frame.Length <= writes.Length; i++)
            {
                if (ContainsAt(writes, i, frame))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool ContainsAt(byte[] writes, int offset, byte[] frame)
        {
            for (int j = 0; j < frame.Length; j++)
            {
                if (writes[offset + j] != frame[j])
                {
                    return false;
                }
            }

            return true;
        }
    }
}
