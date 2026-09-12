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
            // Hand-built stored block: BFINAL=1, BTYPE=00, LEN=2, "Hi". No
            // footer exists, so agreement must stay armed.
            var (output, _, sut) = await ReadOnceAsync(
              h => h.EnableMccp = true,
              Mccp2Stream([0x01, 0x02, 0x00, 0xFD, 0xFF, (byte)'H', (byte)'i']));
            output.Should().Be("Hi");
            sut.Mccp2Active.Should().BeTrue();
        }

        [Fact]
        public async Task RawStreamStartingWithZlibMagic_RetriesRawInsteadOfFailing()
        {
            // A raw deflate stream may start with 0x78 and mis-sniff as zlib
            // (the reference tries zlib first, then raw). Hand-built wire:
            // non-final stored block with pad bits 11110 (byte 0x78, LEN=2,
            // "Hi") + final stored block ("!"). Inflates with no DONT.
            var (output, writes, sut) = await ReadOnceAsync(
              h => h.EnableMccp = true,
              Mccp2Stream([0x78, 0x02, 0x00, 0xFD, 0xFF, (byte)'H', (byte)'i', 0x01, 0x01, 0x00, 0xFE, 0xFF, (byte)'!']));
            output.Should().Be("Hi!");
            // Only the WILL→DO agreement reply: no DONT (the stream never
            // failed) and no other negotiation.
            writes.SelectMany(w => w).Should().Equal(Iac, Do, Mccp2);
            sut.Mccp2Active.Should().BeTrue();
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
