// MCCP inflation tests (conflict 9): zlib/gzip/raw-deflate autodection,
// split delivery, Z_FINISH + trailing plaintext, corrupt-path DONT, TLS
// refusal, session persistence across per-read handlers, direction gates
// (MCCP2 is server-to-client, MCCP3 client-to-server), strict server-side
// MCCP3, strict start-once-never-stop outbound compression, and the server
// MCCP3 offer.
namespace telnet_cs.Tests
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.IO.Compression;
    using System.Linq;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using FluentAssertions;
    using Xunit;
    using telnet_cs.IO;
    using telnet_cs.Client;
    using telnet_cs.Server;

    public class MccpTests
    {
        private const int Iac = 255;
        private const int Sb = 250;
        private const int Se = 240;
        private const int Will = 251;
        private const int Wont = 252;
        private const int Do = 253;
        private const int Dont = 254;
        private const int Mccp2 = 86;
        private const int Mccp3 = 87;

        private static async Task<(string Output, List<byte[]> Writes, ByteStreamHandler Handler)> ReadOnceAsync(
          Action<ByteStreamHandler> configure, params int[] reads)
        {
            using var stream = new ScriptedStream(reads);
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1);
            configure(sut);
            var output = await sut.ReadAsync(TimeSpan.FromMilliseconds(50));
            return (output, [.. stream.ByteWrites], sut);
        }

        private static byte[] ZlibCompress(string text)
        {
            return ZlibCompress(Encoding.ASCII.GetBytes(text));
        }

        private static byte[] ZlibCompress(byte[] payload)
        {
            using var ms = new MemoryStream();
            using (var compressor = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            {
                compressor.Write(payload, 0, payload.Length);
            }

            return ms.ToArray();
        }

        private static byte[] RawCompress(byte[] payload)
        {
            using var ms = new MemoryStream();
            using (var compressor = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            {
                compressor.Write(payload, 0, payload.Length);
            }

            return ms.ToArray();
        }

        private static byte[] GzipCompress(string text)
        {
            using var ms = new MemoryStream();
            using (var compressor = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            {
                var bytes = Encoding.ASCII.GetBytes(text);
                compressor.Write(bytes, 0, bytes.Length);
            }

            return ms.ToArray();
        }

        private static int[] SbFrame(int option, byte[] payload)
        {
            // IAC-double literal 255s inside the frame, per RFC 854.
            var frame = new List<int> { Iac, Sb, option };
            foreach (var b in payload)
            {
                frame.Add(b);
                if (b == Iac)
                {
                    frame.Add(Iac);
                }
            }

            frame.Add(Iac);
            frame.Add(Se);
            return [.. frame];
        }

        private static int[] Mccp2Stream(byte[] compressed)
        {
            return [Iac, Will, Mccp2, Iac, Sb, Mccp2, Iac, Se, .. compressed.Select(b => (int)b)];
        }

        private static int[] Mccp3Stream(byte[] compressed)
        {
            return [Iac, Will, Mccp3, Iac, Sb, Mccp3, Iac, Se, .. compressed.Select(b => (int)b)];
        }

        [Fact]
        public async Task CompressedHello_InflatesToPlaintext()
        {
            var fired = 0;
            var (output, writes, sut) = await ReadOnceAsync(
              h =>
              {
                  h.EnableMccp = true;
                  h.Mccp2StartReceived += () => fired++;
              },
              Mccp2Stream(ZlibCompress("Hello, MCCP!")));
            output.Should().Be("Hello, MCCP!");
            writes.SelectMany(w => w).Should().Equal(Iac, Do, Mccp2);
            fired.Should().Be(1);
            // A proven Z_FINISH ends agreement in the same read.
            sut.Mccp2Active.Should().BeFalse();
        }

        [Fact]
        public async Task SplitAcrossTwoReads_Inflates()
        {
            var compressed = ZlibCompress("Hello, split world!");
            var first = Mccp2Stream(compressed[..(compressed.Length / 2)]);
            var second = compressed[(compressed.Length / 2)..].Select(b => (int)b).ToArray();

            using var stream = new ScriptedStream(first);
            using var cts = new CancellationTokenSource();
            string output1;
            MccpDecompressor? carried;
            bool carriedActive;
            using (var sut = new ByteStreamHandler(stream, cts, 1))
            {
                sut.EnableMccp = true;
                output1 = await sut.ReadAsync(TimeSpan.FromMilliseconds(50));
                carried = sut.MccpStream;
                carriedActive = sut.Mccp2Active;
            }

            output1.Should().NotBe("Hello, split world!");
            carriedActive.Should().BeTrue();

            stream.Enqueue(second);
            using (var sut = new ByteStreamHandler(stream, cts, 1))
            {
                sut.EnableMccp = true;
                // Session carry, exactly what FeedSession/FeedHandler persist.
                sut.Mccp2Active = carriedActive;
                sut.MccpStream = carried;
                var output2 = await sut.ReadAsync(TimeSpan.FromMilliseconds(50));
                (output1 + output2).Should().Be("Hello, split world!");
                sut.Mccp2Active.Should().BeFalse();
            }
        }

        [Fact]
        public async Task ZFinish_TrailingPlaintextResumes()
        {
            var wire = Mccp2Stream(ZlibCompress("Compressed."))
              .Concat("PLAIN".Select(c => (int)c)).ToArray();
            var (output, writes, sut) = await ReadOnceAsync(h => h.EnableMccp = true, wire);
            output.Should().Be("Compressed.PLAIN");
            writes.SelectMany(w => w).Should().Equal(Iac, Do, Mccp2);
            sut.Mccp2Active.Should().BeFalse();
        }

        [Fact]
        public async Task CorruptStream_AnswersDont_AndFeedsReaderNothing()
        {
            // 0xFF is a reserved deflate block type: the live inflate throws.
            var (output, writes, sut) = await ReadOnceAsync(
              h => h.EnableMccp = true, Iac, Will, Mccp2, Iac, Sb, Mccp2, Iac, Se, 0xFF, 0xFF);
            output.Should().BeEmpty();
            writes.SelectMany(w => w).Should().Equal(Iac, Do, Mccp2, Iac, Dont, Mccp2);
            sut.Mccp2Active.Should().BeFalse();
            sut.MccpStream.Should().BeNull();
        }

        [Fact]
        public async Task InflatedIacEscape_Survives()
        {
            var (output, _, _) = await ReadOnceAsync(
              h => h.EnableMccp = true, Mccp2Stream(ZlibCompress([(byte)'A', 0xFF, 0xFF, (byte)'B'])));
            output.Should().Be("AÿB");
        }

        [Fact]
        public async Task TlsActive_RefusesMccp()
        {
            var (output, writes, sut) = await ReadOnceAsync(
              h =>
              {
                  h.EnableMccp = true;
                  h.IsTlsActive = true;
              },
              Iac, Will, Mccp2);
            output.Should().BeEmpty();
            writes.SelectMany(w => w).Should().Equal(Iac, Dont, Mccp2);
            sut.Mccp2Active.Should().BeFalse();
        }

        [Fact]
        public async Task TlsActive_RefusesDoMccp()
        {
            var (output, writes, _) = await ReadOnceAsync(
              h =>
              {
                  h.EnableMccp = true;
                  h.IsTlsActive = true;
              },
              Iac, Do, Mccp2);
            output.Should().BeEmpty();
            writes.SelectMany(w => w).Should().Equal(Iac, Wont, Mccp2);
        }

        [Fact]
        public async Task GzipStream_Inflates()
        {
            var (output, _, _) = await ReadOnceAsync(
              h => h.EnableMccp = true, Mccp2Stream(GzipCompress("Hello, gzip!")));
            output.Should().Be("Hello, gzip!");
        }

        [Fact]
        public async Task RawDeflateStream_Inflates_WithoutEnd()
        {
            // Hand-built stored block: BFINAL=1, BTYPE=00, LEN=2, "Hi". A final
            // stored block terminates the stream, so agreement ends in the
            // same read and trailing plaintext would resume after it.
            var (output, _, sut) = await ReadOnceAsync(
              h => h.EnableMccp = true,
              Mccp2Stream([0x01, 0x02, 0x00, 0xFD, 0xFF, (byte)'H', (byte)'i']));
            output.Should().Be("Hi");
            sut.Mccp2Active.Should().BeFalse();
        }

        [Fact]
        public async Task RawFixedHuffmanStream_ResumesPlaintext()
        {
            // Hand-built fixed-Huffman final block (BFINAL=1, BTYPE=01)
            // spelling "HI", followed by plaintext "YO": the block walker
            // proves the end at the exact byte, so the same read resumes
            // plaintext and agreement ends.
            var wire = Mccp2Stream([0xF3, 0xF0, 0x04, 0x00]).Concat("YO".Select(c => (int)c)).ToArray();
            var (output, _, sut) = await ReadOnceAsync(h => h.EnableMccp = true, wire);
            output.Should().Be("HIYO");
            sut.Mccp2Active.Should().BeFalse();
        }

        [Fact]
        public async Task RawDynamicHuffmanStream_ResumesPlaintext()
        {
            // Raw deflate with a dynamic-Huffman final block (BTYPE=10, as
            // emitted by zlib level 9 for repetitive text): no footer
            // exists, so only the block walker can prove the end and resume
            // the trailing plaintext in the same read.
            const string segment = "Pack my box with five dozen liquor jugs! 0123456789 ABCDEFGHIJKLMNOPQRSTUVWXYZ abcdefghijklmnopqrstuvwxyz. ";
            var expected = string.Concat(Enumerable.Repeat(segment, 40));
            var compressed = Convert.FromHexString(
              "edcd4516c2301405d0ad7c36c0c1650814b760456695b44d2d54525b3d6b60feee062e33ac80a29a4c595129728" +
              "f1c5170b265c3630a45a2644abe72b31675babdfe60381a4fa6349b2fb4e56abdd9eef687e3e9cc2ed7dbfda13f5" +
              "fef0f19a66573c7f5841f84512cbf499ae5aa28abba691343850a152a54a850a142850a152a54a850a1faa7fa01");
            var wire = Mccp2Stream(compressed).Concat("YO".Select(c => (int)c)).ToArray();
            var (output, _, sut) = await ReadOnceAsync(h => h.EnableMccp = true, wire);
            output.Should().Be(expected + "YO");
            sut.Mccp2Active.Should().BeFalse();
        }

        [Fact]
        public async Task RawFixedHuffmanStream_SplitDelivery_ResumesPlaintext()
        {
            // The first two bytes decode "H" but leave the block open, so
            // agreement stays; the rest (plus trailing "YO") completes the
            // block on a carried stream and resumes plaintext.
            var first = Mccp2Stream([0xF3, 0xF0]);
            var second = new byte[] { 0x04, 0x00, (byte)'Y', (byte)'O' }.Select(b => (int)b).ToArray();

            using var stream = new ScriptedStream(first);
            using var cts = new CancellationTokenSource();
            string output1;
            MccpDecompressor? carried;
            bool carriedActive;
            using (var sut = new ByteStreamHandler(stream, cts, 1))
            {
                sut.EnableMccp = true;
                output1 = await sut.ReadAsync(TimeSpan.FromMilliseconds(50));
                carried = sut.MccpStream;
                carriedActive = sut.Mccp2Active;
            }

            output1.Should().Be("H");
            carriedActive.Should().BeTrue();

            stream.Enqueue(second);
            using (var sut = new ByteStreamHandler(stream, cts, 1))
            {
                sut.EnableMccp = true;
                // Session carry, exactly what FeedSession/FeedHandler persist.
                sut.Mccp2Active = carriedActive;
                sut.MccpStream = carried;
                var output2 = await sut.ReadAsync(TimeSpan.FromMilliseconds(50));
                (output1 + output2).Should().Be("HIYO");
                sut.Mccp2Active.Should().BeFalse();
            }
        }

        [Fact]
        public async Task RawStreamStartingWithZlibMagic_RetriesRawInsteadOfFailing()
        {
            // A raw deflate stream may start with 0x78 and mis-sniff as zlib
            // (zlib is tried first, then raw). Hand-built wire:
            // non-final stored block with pad bits 11110 (byte 0x78, LEN=2,
            // "Hi") + final stored block ("!"). The final block ends the
            // stream, so agreement ends in the same read. Inflates with no DONT.
            var (output, writes, sut) = await ReadOnceAsync(
              h => h.EnableMccp = true,
              Mccp2Stream([0x78, 0x02, 0x00, 0xFD, 0xFF, (byte)'H', (byte)'i', 0x01, 0x01, 0x00, 0xFE, 0xFF, (byte)'!']));
            output.Should().Be("Hi!");
            // Only the WILL→DO agreement reply: no DONT (the stream never
            // failed) and no other negotiation.
            writes.SelectMany(w => w).Should().Equal(Iac, Do, Mccp2);
            sut.Mccp2Active.Should().BeFalse();
        }

        [Fact]
        public async Task Mccp3_ClientRole_StartsOutboundWithoutInflating()
        {
            // MCCP3 flows client-to-server: a client never inflates inbound
            // on the peer's marker. It answers WILL with DO plus its own
            // empty SB start (outbound compression starts there) and reads
            // the following bytes as plaintext.
            var receivedFired = 0;
            var sentFired = 0;
            var wire = new[] { Iac, Will, Mccp3, Iac, Sb, Mccp3, Iac, Se }
              .Concat(ZlibCompress("Downstream.").Select(b => (int)b)).ToArray();
            var (output, writes, sut) = await ReadOnceAsync(
              h =>
              {
                  h.EnableMccp = true;
                  h.Mccp3StartReceived += () => receivedFired++;
                  h.Mccp3StartSent += () => sentFired++;
              },
              wire);
            output.Should().NotBe("Downstream.");
            writes.SelectMany(w => w).Should().Equal(Iac, Do, Mccp3, Iac, Sb, Mccp3, Iac, Se);
            receivedFired.Should().Be(0);
            sentFired.Should().Be(1);
            sut.Mccp3Active.Should().BeFalse();
        }

        [Fact]
        public async Task Mccp3_ServerRole_ArmsInflatesAndFiresHook()
        {
            // The server side of the same exchange: DO agrees (no SB goes
            // out — the peer's marker starts the stream), and the bytes
            // after it inflate.
            var fired = 0;
            var wire = new[] { Iac, Will, Mccp3, Iac, Sb, Mccp3, Iac, Se }
              .Concat(ZlibCompress("Downstream.").Select(b => (int)b)).ToArray();
            var (output, writes, sut) = await ReadOnceAsync(
              h =>
              {
                  h.EnableMccp = true;
                  h.IsServerRole = true;
                  h.Mccp3StartReceived += () => fired++;
              },
              wire);
            output.Should().Be("Downstream.");
            writes.SelectMany(w => w).Should().Equal(Iac, Do, Mccp3);
            fired.Should().Be(1);
            sut.Mccp3Active.Should().BeFalse();
        }

        [Fact]
        public async Task SecondSbAfterEnd_StartsFreshStream()
        {
            var fired = 0;
            var wire = Mccp2Stream(ZlibCompress("One"))
              .Concat([Iac, Sb, Mccp2, Iac, Se])
              .Concat(ZlibCompress("Two").Select(b => (int)b)).ToArray();
            var (output, _, _) = await ReadOnceAsync(
              h =>
              {
                  h.EnableMccp = true;
                  h.Mccp2StartReceived += () => fired++;
              },
              wire);
            output.Should().Be("OneTwo");
            fired.Should().Be(2);
        }

        [Fact]
        public async Task SessionPersistsMccpAcrossReads()
        {
            // MCCP3 flows client-to-server, so the server persists that
            // direction across per-read handlers (MCCP2 on a server never
            // inflates).
            var compressed = ZlibCompress("Hello, session!");
            var first = Mccp3Stream(compressed[..(compressed.Length / 2)]);
            using var stream = new ScriptedStream(first);
            using var session = new ServerSession(
              stream, new TelnetServerOptions { EnableMccp = true }, CancellationToken.None);
            var output1 = await session.ReadAsync(TimeSpan.FromMilliseconds(50));
            output1.Should().NotBe("Hello, session!");

            stream.Enqueue(compressed[(compressed.Length / 2)..].Select(b => (int)b).ToArray());
            var output2 = await session.ReadAsync(TimeSpan.FromMilliseconds(50));
            (output1 + output2).Should().Be("Hello, session!");
        }

        private static byte[] OutboundBytes(ScriptedStream stream)
        {
            return stream.ByteWrites.SelectMany(static b => b).ToArray();
        }

        private static int CountFrame(byte[] haystack, byte[] needle)
        {
            int count = 0;
            for (int i = 0; i + needle.Length <= haystack.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < needle.Length; j++)
                {
                    if (haystack[i + j] != needle[j])
                    {
                        match = false;
                        break;
                    }
                }

                if (match)
                {
                    count++;
                }
            }

            return count;
        }

        private static int IndexOfFrame(byte[] haystack, byte[] needle)
        {
            for (int i = 0; i + needle.Length <= haystack.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < needle.Length; j++)
                {
                    if (haystack[i + j] != needle[j])
                    {
                        match = false;
                        break;
                    }
                }

                if (match)
                {
                    return i;
                }
            }

            return -1;
        }

        private static string ZlibInflate(byte[] payload)
        {
            using var ms = new MemoryStream(payload);
            using var inflater = new ZLibStream(ms, CompressionMode.Decompress);
            using var reader = new StreamReader(inflater, Encoding.ASCII);
            return reader.ReadToEnd();
        }

        [Fact]
        public async Task OfferMccp2_AdvancedPreset_SendsWillMccp2()
        {
            // Explicit opt-in offer: once the peer advances negotiation
            // (WILL TTYPE, option 24, here), the advanced preset carries
            // IAC WILL MCCP2 exactly once.
            using var stream = new ScriptedStream();
            using var session = new ServerSession(
              stream, new TelnetServerOptions { OfferMccp2 = true }, CancellationToken.None);
            await session.SendOpeningPresetAsync();
            stream.Enqueue(Iac, Will, 24);
            (await session.ReadAsync(TimeSpan.FromMilliseconds(500))).Should().BeEmpty();
            CountFrame(OutboundBytes(stream), [Iac, Will, Mccp2]).Should().Be(1);
        }

        [Fact]
        public async Task OfferMccp2_Accepted_CompressesOutbound()
        {
            // The peer accepts the offer (DO MCCP2): the session emits the
            // empty SB start marker raw, then compresses everything after
            // it, so the trailing bytes inflate back to the sent text.
            using var stream = new ScriptedStream();
            using var session = new ServerSession(
              stream, new TelnetServerOptions { OfferMccp2 = true }, CancellationToken.None);
            await session.SendOpeningPresetAsync();
            stream.Enqueue(Iac, Will, 24);
            (await session.ReadAsync(TimeSpan.FromMilliseconds(500))).Should().BeEmpty();
            stream.Enqueue(Iac, Do, Mccp2);
            (await session.ReadAsync(TimeSpan.FromMilliseconds(500))).Should().BeEmpty();
            await session.WriteAsync("hi");
            var wire = OutboundBytes(stream);
            byte[] start = [Iac, Sb, Mccp2, Iac, Se];
            CountFrame(wire, start).Should().Be(1);
            int at = IndexOfFrame(wire, start);
            at.Should().BeGreaterThanOrEqualTo(0);
            var compressed = wire[(at + start.Length)..];
            compressed.Should().NotBeEmpty();
            ZlibInflate(compressed).Should().Be("hi");
        }

        [Fact]
        public async Task OfferMccp2_Tls_SendsNoWillMccp2()
        {
            // Compression is never offered over TLS: the advanced preset
            // still negotiates everything else, but stays silent on MCCP2.
            using var stream = new ScriptedStream();
            using var session = new ServerSession(
              stream, new TelnetServerOptions { OfferMccp2 = true }, CancellationToken.None);
            session.IsTls = true;
            await session.SendOpeningPresetAsync();
            stream.Enqueue(Iac, Will, 24);
            (await session.ReadAsync(TimeSpan.FromMilliseconds(500))).Should().BeEmpty();
            var wire = OutboundBytes(stream);
            wire.Should().NotBeEmpty();
            CountFrame(wire, [Iac, Will, Mccp2]).Should().Be(0);
        }

        [Fact]
        public async Task ClientMccp3_Agreed_CompressesOutbound()
        {
            // The client answers WILL MCCP3 with DO plus the empty SB start,
            // then compresses everything it sends: the bytes after the start
            // marker inflate back to the written payload.
            using var stream = new ScriptedStream();
            using var client = new Client(stream, new CancellationToken());
            stream.Enqueue(Iac, Will, Mccp3);
            (await client.ReadAsync(TimeSpan.FromMilliseconds(500))).Should().BeEmpty();
            await client.WriteAsync([(byte)'h', (byte)'i']);
            var wire = OutboundBytes(stream);
            byte[] start = [Iac, Sb, Mccp3, Iac, Se];
            CountFrame(wire, [Iac, Do, Mccp3]).Should().Be(1);
            CountFrame(wire, start).Should().Be(1);
            int at = IndexOfFrame(wire, start);
            at.Should().BeGreaterThanOrEqualTo(0);
            var compressed = wire[(at + start.Length)..];
            compressed.Should().NotBeEmpty();
            ZlibInflate(compressed).Should().Be("hi");
        }

        [Fact]
        public async Task ClientMccp3_PeerWont_KeepsCompressing()
        {
            // Strict: a peer WONT only records state (the reference never
            // stops its outbound compressor), so later writes stay
            // compressed in the same stream.
            using var stream = new ScriptedStream();
            using var client = new Client(stream, new CancellationToken());
            stream.Enqueue(Iac, Will, Mccp3);
            (await client.ReadAsync(TimeSpan.FromMilliseconds(500))).Should().BeEmpty();
            stream.Enqueue(Iac, Wont, Mccp3);
            (await client.ReadAsync(TimeSpan.FromMilliseconds(500))).Should().BeEmpty();
            await client.WriteAsync([(byte)'h', (byte)'i']);
            var wire = OutboundBytes(stream);
            CountFrame(wire, [Iac, Do, Mccp3]).Should().Be(1);
            byte[] start = [Iac, Sb, Mccp3, Iac, Se];
            CountFrame(wire, start).Should().Be(1);
            int at = IndexOfFrame(wire, start);
            at.Should().BeGreaterThanOrEqualTo(0);
            ZlibInflate(wire[(at + start.Length)..]).Should().Be("hi");
        }

        [Fact]
        public async Task ClientMccp3_Disabled_DeclinesAndWritesRaw()
        {
            // Compression off: the client answers WILL MCCP3 with DONT and
            // never compresses.
            using var stream = new ScriptedStream();
            using var client = new Client(stream, new CancellationToken());
            client.Settings.EnableMccp = false;
            stream.Enqueue(Iac, Will, Mccp3);
            (await client.ReadAsync(TimeSpan.FromMilliseconds(500))).Should().BeEmpty();
            await client.WriteAsync([(byte)'h', (byte)'i']);
            var wire = OutboundBytes(stream);
            CountFrame(wire, [Iac, Dont, Mccp3]).Should().Be(1);
            wire[^2..].Should().Equal((byte)'h', (byte)'i');
        }

        [Fact]
        public async Task ServerRole_PlaintextAfterMccp2Sb_ReadsRaw()
        {
            // MCCP2 compresses server-to-client: a server never inflates it,
            // so the bytes after an agreed SB MCCP2 still parse as telnet.
            var (output, writes, sut) = await ReadOnceAsync(
              h =>
              {
                  h.EnableMccp = true;
                  h.IsServerRole = true;
              },
              [Iac, Will, Mccp2, Iac, Sb, Mccp2, Iac, Se, (int)'h', (int)'i']);
            output.Should().Be("hi");
            writes.SelectMany(w => w).Should().Equal(Iac, Do, Mccp2);
            sut.Mccp2Active.Should().BeFalse();
        }

        [Fact]
        public async Task ClientRole_PlaintextAfterMccp3Sb_ReadsRaw()
        {
            // MCCP3 compresses client-to-server: a client never inflates it.
            // Its own outbound still starts (DO plus the empty SB), while the
            // inbound bytes after the peer's marker parse as telnet.
            var received = 0;
            var (output, writes, sut) = await ReadOnceAsync(
              h =>
              {
                  h.EnableMccp = true;
                  h.Mccp3StartReceived += () => received++;
              },
              [Iac, Will, Mccp3, Iac, Sb, Mccp3, Iac, Se, (int)'h', (int)'i']);
            output.Should().Be("hi");
            var wire = writes.SelectMany(w => w).ToArray();
            CountFrame(wire, [Iac, Do, Mccp3]).Should().Be(1);
            CountFrame(wire, [Iac, Sb, Mccp3, Iac, Se]).Should().Be(1);
            received.Should().Be(0);
            sut.Mccp3Active.Should().BeFalse();
        }

        [Fact]
        public async Task MccpSb_WithoutAgreement_IsIgnored()
        {
            // No negotiation happened, so the start marker arms nothing and
            // the following bytes parse as telnet.
            var (output, writes, sut) = await ReadOnceAsync(
              h => h.EnableMccp = true,
              [Iac, Sb, Mccp2, Iac, Se, (int)'h', (int)'i']);
            output.Should().Be("hi");
            writes.Should().BeEmpty();
            sut.Mccp2Active.Should().BeFalse();
        }

        [Fact]
        public async Task TlsMccpSb_IsIgnored()
        {
            // Compression is refused over TLS, so the marker that slips past
            // the DONT still arms nothing.
            var (output, writes, sut) = await ReadOnceAsync(
              h =>
              {
                  h.EnableMccp = true;
                  h.IsTlsActive = true;
              },
              [Iac, Will, Mccp2, Iac, Sb, Mccp2, Iac, Se, (int)'h', (int)'i']);
            output.Should().Be("hi");
            writes.SelectMany(w => w).Should().Equal(Iac, Dont, Mccp2);
            sut.Mccp2Active.Should().BeFalse();
        }

        [Fact]
        public async Task ServerRole_Mccp3GzipSb_RefusedWithDont()
        {
            // The server side of MCCP3 accepts zlib only: a gzip stream fails
            // instead of falling back, and the peer asked first (WILL), so
            // the refusal is DONT.
            var wire = new[] { Iac, Will, Mccp3, Iac, Sb, Mccp3, Iac, Se }
              .Concat(GzipCompress("nope").Select(b => (int)b)).ToArray();
            var (output, writes, sut) = await ReadOnceAsync(
              h =>
              {
                  h.EnableMccp = true;
                  h.IsServerRole = true;
              },
              wire);
            output.Should().BeEmpty();
            var outbound = writes.SelectMany(w => w).ToArray();
            CountFrame(outbound, [Iac, Do, Mccp3]).Should().Be(1);
            CountFrame(outbound, [Iac, Dont, Mccp3]).Should().Be(1);
            sut.Mccp3Active.Should().BeFalse();
        }

        [Fact]
        public async Task ServerRole_Mccp3RawSb_RefusedWithDont()
        {
            // Raw deflate is fine client-side but the server side of MCCP3
            // takes zlib only, with no raw retry.
            var wire = new[] { Iac, Will, Mccp3, Iac, Sb, Mccp3, Iac, Se }
              .Concat(RawCompress(Encoding.ASCII.GetBytes("nope")).Select(b => (int)b)).ToArray();
            var (output, writes, sut) = await ReadOnceAsync(
              h =>
              {
                  h.EnableMccp = true;
                  h.IsServerRole = true;
              },
              wire);
            output.Should().BeEmpty();
            var outbound = writes.SelectMany(w => w).ToArray();
            CountFrame(outbound, [Iac, Do, Mccp3]).Should().Be(1);
            CountFrame(outbound, [Iac, Dont, Mccp3]).Should().Be(1);
            sut.Mccp3Active.Should().BeFalse();
        }

        [Fact]
        public async Task ServerRole_Mccp3PeerInitiated_CorruptSendsDont()
        {
            // Garbage after a peer-initiated start is refused with DONT: the
            // peer offered (WILL), so WONT would be ignored by its reader.
            var wire = new[] { Iac, Will, Mccp3, Iac, Sb, Mccp3, Iac, Se }
              .Concat(Encoding.ASCII.GetBytes("definitely-not-zlib!!").Select(b => (int)b)).ToArray();
            var (output, writes, sut) = await ReadOnceAsync(
              h =>
              {
                  h.EnableMccp = true;
                  h.IsServerRole = true;
              },
              wire);
            output.Should().BeEmpty();
            var outbound = writes.SelectMany(w => w).ToArray();
            CountFrame(outbound, [Iac, Do, Mccp3]).Should().Be(1);
            CountFrame(outbound, [Iac, Dont, Mccp3]).Should().Be(1);
            CountFrame(outbound, [Iac, Wont, Mccp3]).Should().Be(0);
            sut.Mccp3Active.Should().BeFalse();
        }

        [Fact]
        public async Task OfferMccp3_AdvancedPreset_SendsWillMccp3AfterMccp2()
        {
            // Explicit opt-in offer: once the peer advances negotiation, the
            // preset carries IAC WILL MCCP3 exactly once, after WILL MCCP2.
            using var stream = new ScriptedStream();
            using var session = new ServerSession(
              stream,
              new TelnetServerOptions { OfferMccp2 = true, OfferMccp3 = true },
              CancellationToken.None);
            await session.SendOpeningPresetAsync();
            stream.Enqueue(Iac, Will, 24);
            (await session.ReadAsync(TimeSpan.FromMilliseconds(500))).Should().BeEmpty();
            var wire = OutboundBytes(stream);
            CountFrame(wire, [Iac, Will, Mccp3]).Should().Be(1);
            IndexOfFrame(wire, [Iac, Will, Mccp2]).Should().BeLessThan(
              IndexOfFrame(wire, [Iac, Will, Mccp3]));
        }

        [Fact]
        public async Task OfferMccp3_Tls_SendsNoWillMccp3()
        {
            // Compression is never offered over TLS, MCCP3 included.
            using var stream = new ScriptedStream();
            using var session = new ServerSession(
              stream, new TelnetServerOptions { OfferMccp3 = true }, CancellationToken.None);
            session.IsTls = true;
            await session.SendOpeningPresetAsync();
            stream.Enqueue(Iac, Will, 24);
            (await session.ReadAsync(TimeSpan.FromMilliseconds(500))).Should().BeEmpty();
            var wire = OutboundBytes(stream);
            wire.Should().NotBeEmpty();
            CountFrame(wire, [Iac, Will, Mccp3]).Should().Be(0);
        }

        [Fact]
        public async Task OfferMccp3_Accepted_InflatesInbound()
        {
            // The peer accepts our WILL MCCP3 (DO), then sends its own start
            // marker and compressed bytes, which the server inflates. The
            // server itself sends no SB marker for MCCP3.
            using var stream = new ScriptedStream();
            using var session = new ServerSession(
              stream, new TelnetServerOptions { OfferMccp3 = true }, CancellationToken.None);
            await session.SendOpeningPresetAsync();
            stream.Enqueue(Iac, Will, 24);
            (await session.ReadAsync(TimeSpan.FromMilliseconds(500))).Should().BeEmpty();
            CountFrame(OutboundBytes(stream), [Iac, Will, Mccp3]).Should().Be(1);
            stream.Enqueue(Iac, Do, Mccp3);
            (await session.ReadAsync(TimeSpan.FromMilliseconds(500))).Should().BeEmpty();
            stream.Enqueue(Mccp3Stream(ZlibCompress("up"))[3..]);
            (await session.ReadAsync(TimeSpan.FromMilliseconds(500))).Should().Be("up");
            var wire = OutboundBytes(stream);
            CountFrame(wire, [Iac, Sb, Mccp3, Iac, Se]).Should().Be(0);
        }

        [Fact]
        public async Task OfferMccp3_Accepted_CorruptSendsWont()
        {
            // The peer accepted our offer (DO, not WILL), so corrupt bytes
            // after its marker are refused with WONT: a DONT would only end
            // our own outbound, which MCCP3 has none of server-side.
            using var stream = new ScriptedStream();
            using var session = new ServerSession(
              stream, new TelnetServerOptions { OfferMccp3 = true }, CancellationToken.None);
            await session.SendOpeningPresetAsync();
            stream.Enqueue(Iac, Will, 24);
            (await session.ReadAsync(TimeSpan.FromMilliseconds(500))).Should().BeEmpty();
            stream.Enqueue(Iac, Do, Mccp3);
            (await session.ReadAsync(TimeSpan.FromMilliseconds(500))).Should().BeEmpty();
            stream.Enqueue([Iac, Sb, Mccp3, Iac, Se, .. Encoding.ASCII.GetBytes("junk!!").Select(b => (int)b)]);
            (await session.ReadAsync(TimeSpan.FromMilliseconds(500))).Should().BeEmpty();
            var wire = OutboundBytes(stream);
            CountFrame(wire, [Iac, Wont, Mccp3]).Should().Be(1);
            CountFrame(wire, [Iac, Dont, Mccp3]).Should().Be(0);
        }

        [Fact]
        public async Task ServerMccp3_ReagreeAfterCorrupt_RestartsClean()
        {
            // A corrupt stream ends in DONT but records the refusal, so a
            // fresh WILL re-arms and a valid stream inflates afterwards.
            using var stream = new ScriptedStream();
            using var session = new ServerSession(
              stream, new TelnetServerOptions { EnableMccp = true }, CancellationToken.None);
            stream.Enqueue([Iac, Will, Mccp3, Iac, Sb, Mccp3, Iac, Se,
                .. Encoding.ASCII.GetBytes("junk!!").Select(b => (int)b)]);
            (await session.ReadAsync(TimeSpan.FromMilliseconds(500))).Should().BeEmpty();
            stream.Enqueue(Mccp3Stream(ZlibCompress("again")));
            (await session.ReadAsync(TimeSpan.FromMilliseconds(500))).Should().Be("again");
            var wire = OutboundBytes(stream);
            CountFrame(wire, [Iac, Do, Mccp3]).Should().Be(2);
            CountFrame(wire, [Iac, Dont, Mccp3]).Should().Be(1);
        }

        [Fact]
        public async Task OfferMccp2_PeerWont_KeepsCompressing()
        {
            // Strict: a peer WONT only records state (the reference never
            // stops its outbound compressor), so writes before and after
            // land in the same compressed stream.
            using var stream = new ScriptedStream();
            using var session = new ServerSession(
              stream, new TelnetServerOptions { OfferMccp2 = true }, CancellationToken.None);
            await session.SendOpeningPresetAsync();
            stream.Enqueue(Iac, Will, 24);
            (await session.ReadAsync(TimeSpan.FromMilliseconds(500))).Should().BeEmpty();
            stream.Enqueue(Iac, Do, Mccp2);
            (await session.ReadAsync(TimeSpan.FromMilliseconds(500))).Should().BeEmpty();
            await session.WriteAsync("hi");
            stream.Enqueue(Iac, Wont, Mccp2);
            (await session.ReadAsync(TimeSpan.FromMilliseconds(500))).Should().BeEmpty();
            await session.WriteAsync("AB");
            var wire = OutboundBytes(stream);
            byte[] start = [Iac, Sb, Mccp2, Iac, Se];
            CountFrame(wire, start).Should().Be(1);
            int at = IndexOfFrame(wire, start);
            at.Should().BeGreaterThanOrEqualTo(0);
            CountFrame(wire, [Iac, Dont, Mccp2]).Should().Be(0);
            ZlibInflate(wire[(at + start.Length)..]).Should().Be("hiAB");
        }

        [Fact]
        public async Task OfferMccp2_ReagreeAfterWont_KeepsSingleStream()
        {
            // The re-DO re-affirms the exchange, but the compressor never
            // stopped: no second start marker goes out, and the write after
            // re-agreement inflates from the same stream.
            using var stream = new ScriptedStream();
            using var session = new ServerSession(
              stream, new TelnetServerOptions { OfferMccp2 = true }, CancellationToken.None);
            await session.SendOpeningPresetAsync();
            stream.Enqueue(Iac, Will, 24);
            (await session.ReadAsync(TimeSpan.FromMilliseconds(500))).Should().BeEmpty();
            stream.Enqueue(Iac, Do, Mccp2);
            (await session.ReadAsync(TimeSpan.FromMilliseconds(500))).Should().BeEmpty();
            stream.Enqueue(Iac, Wont, Mccp2);
            (await session.ReadAsync(TimeSpan.FromMilliseconds(500))).Should().BeEmpty();
            stream.Enqueue(Iac, Do, Mccp2);
            (await session.ReadAsync(TimeSpan.FromMilliseconds(500))).Should().BeEmpty();
            await session.WriteAsync("yo");
            var wire = OutboundBytes(stream);
            byte[] start = [Iac, Sb, Mccp2, Iac, Se];
            CountFrame(wire, start).Should().Be(1);
            int at = IndexOfFrame(wire, start);
            at.Should().BeGreaterThanOrEqualTo(0);
            ZlibInflate(wire[(at + start.Length)..]).Should().Be("yo");
        }

        [Fact]
        public async Task Mccp2Inflate_SplitAtEveryPosition()
        {
            // A compressed stream split across reads at any byte offset
            // still reassembles byte-exact: the inflater emits early bytes
            // as soon as they decode, so the two reads concatenate to the
            // payload with nothing lost, duplicated, or reordered.
            var compressed = ZlibCompress("split");
            var full = Mccp2Stream(compressed);
            for (int split = 1; split < full.Length; split++)
            {
                using var stream = new ScriptedStream(full[..split]);
                using var client = new Client(stream, new CancellationToken());
                var first = await client.ReadAsync(TimeSpan.FromMilliseconds(500));
                stream.Enqueue(full[split..]);
                var second = await client.ReadAsync(TimeSpan.FromMilliseconds(500));
                (first + second).Should().Be("split");
            }
        }

        [Fact]
        public async Task Mccp2Inflate_IacBytesInCompressedStreamAreData()
        {
            // A stored raw-deflate block carrying IAC-looking bytes still
            // inflates as data: once compression starts, 255 bytes on the
            // wire are payload, not framing. The inflated bytes re-enter
            // telnet decoding, so the sender's escaped 0xFF pair yields one
            // 0xFF here; trailing plaintext resumes after Z_FINISH.
            byte[] block = [0x01, 0x05, 0x00, 0xFA, 0xFF, (byte)'X', 0xFF, 0xFF, 0xF0, (byte)'Y'];
            var wire = new[] { Iac, Will, Mccp2, Iac, Sb, Mccp2, Iac, Se }
              .Concat(block.Select(b => (int)b))
              .Concat([(int)'O', (int)'K']).ToArray();
            var (output, _, sut) = await ReadOnceAsync(h => h.EnableMccp = true, wire);
            output.Should().Be("X\xff\xf0YOK");
            sut.Mccp2Active.Should().BeFalse();
        }

        [Fact]
        public async Task Outbound_RoundTrip_FuzzPayloads()
        {
            // Everything the client sends after the MCCP3 start marker
            // inflates byte-exact, including IAC-heavy binary payloads: the
            // stack escapes 0xFF before compression (as the reference
            // compresses the encoded stream), so expectations escape too.
            byte[][] payloads =
            [
                Encoding.ASCII.GetBytes("hello"),
                Encoding.ASCII.GetBytes(new string('A', 1000)),
                Enumerable.Repeat((byte)0xFF, 64).ToArray(),
                Enumerable.Range(0, 256).Select(i => (byte)i).ToArray(),
            ];
            foreach (var payload in payloads)
            {
                using var stream = new ScriptedStream();
                using var client = new Client(stream, new CancellationToken());
                stream.Enqueue(Iac, Will, Mccp3);
                (await client.ReadAsync(TimeSpan.FromMilliseconds(500))).Should().BeEmpty();
                await client.WriteAsync(payload);
                var wire = OutboundBytes(stream);
                byte[] start = [Iac, Sb, Mccp3, Iac, Se];
                int at = IndexOfFrame(wire, start);
                at.Should().BeGreaterThanOrEqualTo(0);
                ZlibInflateBytes(wire[(at + start.Length)..]).Should().Equal(IacEscape(payload));
            }
        }

        private static byte[] IacEscape(byte[] payload)
        {
            using var ms = new MemoryStream(payload.Length + 1);
            foreach (var b in payload)
            {
                ms.WriteByte(b);
                if (b == Iac)
                {
                    ms.WriteByte(Iac);
                }
            }

            return ms.ToArray();
        }

        private static byte[] ZlibInflateBytes(byte[] payload)
        {
            using var ms = new MemoryStream(payload);
            using var inflater = new ZLibStream(ms, CompressionMode.Decompress);
            using var result = new MemoryStream();
            inflater.CopyTo(result);
            return result.ToArray();
        }
    }
}
