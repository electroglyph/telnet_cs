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
            // Source of truth: direction matters on MCCP failure. docs/mud-protocols/
            // mccp.md requires server-side decompression errors to send IAC WONT MCCP3
            // while client-side errors send IAC DONT MCCP2. The reference itself sends
            // nothing (it clears the decompressor and resumes plaintext), but it never
            // sends DONT 87 for an MCCP3 failure.
            // Our code: telnet_cs/IO/ByteStreamHandler.cs latches the arming option and
            // always flushes SendDont(mccpShutdownOption), so an MCCP3 failure emits
            // FF FE 57 (DONT 87) — the wrong verb for a server disable.
            // Proof: agree MCCP3, arm empty SB 87, feed corrupt deflate; correct bytes
            // contain FF FC 57 and never FF FE 57. Observing DONT proves the verb is
            // latched instead of sided (MCCP3 -> WONT, MCCP2 -> DONT). This test is
            // correct per the spec direction rule.
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
            // Source of truth: the reference pops the MCCP option byte, drops extras,
            // and still activates. ~/telnetlib3/telnetlib3/stream_writer.py
            // _handle_sb_mccp2/_handle_sb_mccp3 do buf.popleft() then set active without
            // checking length; the MCCP spec only defines the empty handshake but does
            // not contradict this leniency.
            // Our code: telnet_cs/IO/ByteStreamHandler.cs logs and ignores any non-empty
            // MCCP SB, so mid-stream FF FA 56 00 FF F0 never arms and following bytes
            // stay plaintext.
            // Proof: WILL 86 plus padded SB 86 00 plus zlib("HI") must read "HI" when
            // compression armed; plaintext proves the arm was skipped. This test is
            // correct.
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
            // Source of truth: only VAR (1) and VAL (2) terminate an MSDP key.
            // ~/telnetlib3/telnetlib3/mud.py _read_key loops while the next byte is not
            // in (MSDP_VAL, MSDP_VAR); only _read_string stops at TABLE/CLOSE markers.
            // The MSDP spec forbids bytes 3-6 in names/values, so this is reachable
            // only with malformed input, but garbage tolerance differs observably.
            // Our code: telnet_cs/Protocol/MudProtocol.cs ReadKey also stops at
            // TABLE_OPEN/CLOSE and ARRAY_OPEN/CLOSE, so payload 01 41 03 42 02 78
            // parses key "A" then treats 0x03 as TABLE_OPEN instead of preserving
            // "A\x03B".
            // Proof: decoding that payload must yield a single entry {"A\x03B": "x"};
            // a split tree plus trailing leak proves the over-eager stop. This test is
            // correct.
            var decoded = MudProtocol.MsdpDecode(new byte[] { 1, (byte)'A', 3, (byte)'B', 2, (byte)'x' });
            decoded.Should().ContainSingle().Which.Should().Be(KeyValuePair.Create<string, object?>("A\x03" + "B", "x"));
        }
    }
}
