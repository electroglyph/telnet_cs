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
    using telnet_cs.Protocol;
    using telnet_cs.Server;
    using telnet_cs.Transport;

    /// <summary>
    /// Audit §4 proper-behavior tests. F-M1/M4/M7 FAIL against current behavior.
    /// </summary>
    public class AuditProperMudTests
    {
        private static bool ContainsFrame(byte[] writes, byte[] frame)
        {
            for (int i = 0; i + frame.Length <= writes.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < frame.Length; j++)
                {
                    if (writes[i + j] != frame[j])
                    {
                        match = false;
                        break;
                    }
                }

                if (match)
                {
                    return true;
                }
            }

            return false;
        }

        private static byte[] ZlibCompress(string text)
        {
            using var ms = new MemoryStream();
            using (var zlib = new ZLibStream(ms, CompressionLevel.NoCompression, leaveOpen: true))
            {
                zlib.Write(Encoding.ASCII.GetBytes(text));
            }

            return ms.ToArray();
        }

        [Fact]
        public async Task Mccp3Corrupt_AnsweredWithWont()
        {
            // F-M1: a server-side MCCP3 decompression error earns IAC WONT 87
            // (mccp.md direction rule), never IAC DONT 87.
            var options = new TelnetServerOptions { EnableMccp = true };
            using var stream = new ScriptedStream(
                [255, 251, 87, 255, 250, 87, 255, 240, 0x78, 0x9C, 0xFF, 0xFF, 0xFF, 0xFF]);
            using var session = new ServerSession(stream, options, CancellationToken.None);
            await session.ReadAsync(TimeSpan.FromMilliseconds(500));
            await session.ReadAsync(TimeSpan.FromMilliseconds(500));
            byte[] writes = stream.ByteWrites.SelectMany(w => w).ToArray();
            ContainsFrame(writes, new byte[] { 255, 252, 87 }).Should().BeTrue();
            ContainsFrame(writes, new byte[] { 255, 254, 87 }).Should().BeFalse();
        }

        [Fact]
        public async Task NonEmptyMccpSb_StillArmsCompression()
        {
            // F-M7: extra bytes after the MCCP option byte are dropped but
            // compression still activates (reference _handle_sb_mccp2).
            var options = new TelnetServerOptions { EnableMccp = true };
            using var stream = new ScriptedStream();
            stream.Enqueue([255, 251, 86, 255, 250, 86, 0, 255, 240]);
            stream.Enqueue(ZlibCompress("HI").Select(b => (int)b).ToArray());
            using var session = new ServerSession(stream, options, CancellationToken.None);
            (await session.ReadAsync(TimeSpan.FromSeconds(2))).Should().Be("HI");
        }

        [Fact]
        public void MsdpDecode_StopsKeyOnlyAtVarVal()
        {
            // F-M4: like the reference _read_key, only VAR/VAL terminate a key;
            // garbage marker bytes inside a key are preserved, not parsed.
            var decoded = MudProtocol.MsdpDecode(new byte[] { 1, (byte)'A', 3, (byte)'B', 2, (byte)'x' });
            decoded.Should().ContainSingle().Which.Should().Be(KeyValuePair.Create<string, object?>("A\x03" + "B", "x"));
        }
    }
}
