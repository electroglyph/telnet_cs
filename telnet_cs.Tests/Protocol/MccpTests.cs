// MCCP inflation tests (conflict 9): zlib/gzip/raw-deflate autodection,
// split delivery, Z_FINISH + trailing plaintext, corrupt-path DONT, TLS
// refusal, and session persistence across per-read handlers.
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
        public async Task Mccp3_ArmsInflatesAndFiresHook()
        {
            var fired = 0;
            var wire = new[] { Iac, Will, Mccp3, Iac, Sb, Mccp3, Iac, Se }
              .Concat(ZlibCompress("Downstream.").Select(b => (int)b)).ToArray();
            var (output, writes, sut) = await ReadOnceAsync(
              h =>
              {
                  h.EnableMccp = true;
                  h.Mccp3StartReceived += () => fired++;
              },
              wire);
            output.Should().Be("Downstream.");
            // WILL MCCP3 agreement also emits the empty start SB (reference:
            // the client sends SB MCCP3 to start compression).
            writes.SelectMany(w => w).Should().Equal(Iac, Do, Mccp3, Iac, Sb, Mccp3, Iac, Se);
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
            var compressed = ZlibCompress("Hello, session!");
            var first = Mccp2Stream(compressed[..(compressed.Length / 2)]);
            using var stream = new ScriptedStream(first);
            using var session = new ServerSession(
              stream, new TelnetServerOptions { EnableMccp = true }, CancellationToken.None);
            var output1 = await session.ReadAsync(TimeSpan.FromMilliseconds(50));
            output1.Should().NotBe("Hello, session!");

            stream.Enqueue(compressed[(compressed.Length / 2)..].Select(b => (int)b).ToArray());
            var output2 = await session.ReadAsync(TimeSpan.FromMilliseconds(50));
            (output1 + output2).Should().Be("Hello, session!");
        }
    }
}
