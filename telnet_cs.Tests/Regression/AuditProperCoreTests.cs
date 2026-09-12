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
    /// Audit §1 proper-behavior tests: data bytes must reach the reader
    /// verbatim (telnetlib3 <c>_process_data_chunk</c> forwards every non-IAC
    /// byte; RFC 854 NVT printer defines no expansions). Each test below
    /// FAILS against current behavior and quotes its finding ID.
    /// </summary>
    public class AuditProperCoreTests
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
            // F-C1: no "^C" / "\n \n" / "NAK: ..." expansions, no drops, no
            // destructive backspace handling in the data path.
            var (output, _, _) = await ReadScriptedAsync(controlByte, 65);
            output.Should().Be(((char)controlByte).ToString() + "A");
        }

        [Fact]
        public async Task Ayt_ConsumedWithoutReply()
        {
            // F-C9: AYT earns no proof-alive bytes — answering would inject
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
            // F-C10: EC/EL are consumed without editing — the delivery buffer
            // is the application's byte record, not a terminal line.
            var (output, writes, _) = await ReadScriptedAsync(65, 66, 255, command);
            output.Should().Be("AB");
            writes.Should().BeEmpty();
        }

        [Fact]
        public async Task Enquiry_SendsNoAck()
        {
            // F-C1: ENQ must not emit an unsolicited ACK wire byte.
            var (output, _, singles) = await ReadScriptedAsync(5);
            output.Should().Be("\u0005");
            singles.Should().BeEmpty();
        }

        [Fact]
        public async Task HighBytes_PreservedWithoutBinary()
        {
            // F-C2: the bytes path never drops; BINARY only gates decoding.
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
            // F-C3: BRK/EOF/SUSP/ABORT are consumed, never "[BRK]" text.
            var (output, _, _) = await ReadScriptedAsync(255, command);
            output.Should().BeEmpty();
        }

        [Fact]
        public async Task CrNul_PreservedInRawRead()
        {
            // F-C8: CR NUL collapsing belongs to line-oriented reads only.
            var (output, _, _) = await ReadScriptedAsync(65, 13, 0);
            output.Should().Be("A\r\0");
        }

        [Theory]
        [InlineData(11)]
        [InlineData(12)]
        public async Task VerticalTabFormFeed_PreservedVerbatim(int controlByte)
        {
            // F-C13: no platform-dependent Environment.NewLine mapping.
            var (output, _, _) = await ReadScriptedAsync(controlByte);
            output.Should().Be(((char)controlByte).ToString());
        }

        [Fact]
        public async Task LargeSubnegotiation_Dispatched()
        {
            // F-C4: a 600-byte SB (well under the 1 MiB reference bound) must
            // be dispatched, not silently dropped.
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
            // F-C7: a split IAC WILL across two top-level reads must still be
            // answered; the pending-IAC state must survive the handler swap.
            using var stream = new ScriptedStream(255);
            using var client = new Client(stream, TimeSpan.FromMilliseconds(50), CancellationToken.None);
            (await client.ReadAsync(TimeSpan.FromMilliseconds(200))).Should().BeEmpty();
            stream.Enqueue(253, 3);
            await client.ReadAsync(TimeSpan.FromMilliseconds(200));
            byte[] writes = stream.ByteWrites.SelectMany(w => w).ToArray();
            CountOccurrences(writes, new byte[] { 255, 253, 3 }).Should().Be(2, "proactive DO SGA plus the DO SGA answer to split WILL SGA");
        }

        [Fact]
        public async Task RawDeflateStream_ResumesPlaintext()
        {
            // F-C14: raw-deflate end must be detected so following plaintext
            // resumes instead of entering the inflater.
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
