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
            // Source of truth: direction matters on MCCP failure.
            // docs/mud-protocols/mccp.md Compression Errors (MCCP3): server-side zlib
            // errors send IAC WONT MCCP3 (server revokes its WILL; client disables and
            // continues plaintext). Client-side MCCP2 errors send IAC DONT MCCP2.
            // RFC 1143 polarity: WONT revokes our WILL (us-side), DONT revokes our DO
            // (him-side). The reference itself sends nothing on corrupt (it clears the
            // decompressor and resumes plaintext), but it never sends DONT 87 for an
            // MCCP3 failure; the spec WONT is the interop-safe wire behavior (silent
            // resume would leave the compressor sending).
            // Setup polarity matters: the server must be WILL-side (us YES) for WONT
            // to be correct. Peer DO 87 earns server WILL 87 (us YES), then empty
            // SB 87 arms decompression (IsEnabledByUs). A peer WILL setup would make
            // the server DO-side (him YES), where DONT would be the RFC-correct
            // revoke, so this uses DO to pin the spec WONT path.
            // Our code latches the arming option but always flushes DONT, so an MCCP3
            // failure emits FF FE 57 (DONT 87).
            // Proof: DO 87 + empty SB 87 + corrupt deflate must contain FF FC 57 and
            // never FF FE 57. Observing DONT proves the verb is latched instead of
            // sided (MCCP3 -> WONT, MCCP2 -> DONT). This test is correct as fixed.
            var options = new TelnetServerOptions { EnableMccp = true };
            using var stream = new ScriptedStream(
                [255, 253, 87, 255, 250, 87, 255, 240, 0x78, 0x9C, 0xFF, 0xFF, 0xFF, 0xFF]);
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
            // The start marker carries no payload on the wire, but a padded
            // SB still arms: the option byte selects the stream and extras
            // are dropped. MCCP3 flows client-to-server, so this is pinned
            // server-side (a server ignores MCCP2 markers entirely).
            // Proof: WILL 87 plus padded SB 87 00 plus zlib("HI") reads "HI";
            // plaintext would prove the arm was skipped.
            var options = new TelnetServerOptions { EnableMccp = true };
            using var stream = new ScriptedStream();
            stream.Enqueue([255, 251, 87, 255, 250, 87, 0, 255, 240]);
            stream.Enqueue(ZlibCompress("HI").Select(b => (int)b).ToArray());
            using var session = new ServerSession(stream, options, CancellationToken.None);
            (await session.ReadAsync(TimeSpan.FromSeconds(2))).Should().Be("HI");
        }

        [Fact]
        public async Task NonEmptyMccpSb_MccpDisabled_StaysPlaintext()
        {
            // The padding-carrying SB shares the empty form's gates: with
            // compression not opted in, WILL 86 is declined and the SB arms
            // nothing, so the following bytes stay plain data. The default
            // now accepts MCCP, so opt out explicitly for this shape.
            using var stream = new ScriptedStream();
            stream.Enqueue([255, 251, 86, 255, 250, 86, 0, 255, 240]);
            stream.Enqueue(ZlibCompress("HI").Select(b => (int)b).ToArray());
            using var session = new ServerSession(stream, new TelnetServerOptions { EnableMccp = false }, CancellationToken.None);
            (await session.ReadAsync(TimeSpan.FromMilliseconds(500))).Should().NotBe("HI");
            byte[] writes = stream.ByteWrites.SelectMany(w => w).ToArray();
            ContainsFrame(writes, new byte[] { 255, 254, 86 }).Should().BeTrue();
        }

        [Fact]
        public async Task NonEmptyMccpSb_TlsActive_StaysPlaintext()
        {
            // Never inflate over TLS (CRIME/BREACH): the padded SB is
            // ignored like the empty form, and the bytes stay plain data.
            var options = new TelnetServerOptions { EnableMccp = true };
            using var stream = new ScriptedStream();
            stream.Enqueue([255, 251, 86, 255, 250, 86, 0, 255, 240]);
            stream.Enqueue(ZlibCompress("HI").Select(b => (int)b).ToArray());
            using var session = new ServerSession(stream, options, CancellationToken.None);
            session.IsTls = true;
            (await session.ReadAsync(TimeSpan.FromMilliseconds(500))).Should().NotBe("HI");
            byte[] writes = stream.ByteWrites.SelectMany(w => w).ToArray();
            ContainsFrame(writes, new byte[] { 255, 254, 86 }).Should().BeTrue();
        }

        [Fact]
        public async Task NonEmptyMccpSb_WithoutAgreement_StaysPlaintext()
        {
            // No prior WILL/DO: the padded SB arms nothing even with
            // compression opted in, so the following bytes stay plain data.
            var options = new TelnetServerOptions { EnableMccp = true };
            using var stream = new ScriptedStream();
            stream.Enqueue([255, 250, 86, 0, 255, 240]);
            stream.Enqueue(ZlibCompress("HI").Select(b => (int)b).ToArray());
            using var session = new ServerSession(stream, options, CancellationToken.None);
            (await session.ReadAsync(TimeSpan.FromMilliseconds(500))).Should().NotBe("HI");
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
