namespace telnet_cs.Tests
{
    using System;
    using System.Linq;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using FluentAssertions;
    using Xunit;
    using telnet_cs.Protocol;
    using telnet_cs.Server;

    /// <summary>
    /// Round-2 audit (§3/§6/§8) TTYPE pins: each test asserts the telnetlib3
    /// cycling behavior, so every test here fails against the current code.
    /// </summary>
    public class Audit2TtypeTests
    {
        private static ServerSession NewSession(ScriptedStream stream, TelnetServerOptions? options = null)
        {
            // Peer agreement for TTYPE collection (state-only).
            var s = new ServerSession(stream, options ?? new TelnetServerOptions(), CancellationToken.None);
            s.Negotiation.ReceivedWill((int)Options.TerminalType, agree: true);
            return s;
        }

        private static int[] TtypeIsFrame(string value)
        {
            return new int[] { 255, 250, 24, 0 }
                .Concat(Encoding.ASCII.GetBytes(value).Select(b => (int)b))
                .Concat(new[] { 255, 240 })
                .ToArray();
        }

        private static byte[] OutboundBytes(ScriptedStream stream)
        {
            return stream.ByteWrites.SelectMany(w => w).ToArray();
        }

        private static int CountSubsequence(byte[] haystack, byte[] needle)
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

        [Fact]
        public async Task TerminalTypeCollection_ResendsSendPerAnswer()
        {
            // audit2 §3/§6 F2-TTYPE (High).
            // Reference: server.py:650-653 (else branch: _ttype_count+=1,
            // writer.request_ttype()); :630,635,640,646 (loop / empty+MTTS
            // / LOOPMAX=90 / repeat stops); stream_writer.py:1397-1413
            // (SEND = FF FA 18 01 FF F0); :2374-2375 (pending SB+TTYPE
            // cleared per SB so the next request_ttype is not suppressed).
            // Repro (real writer + real on_ttype as ext-callback, feed
            // driven): WILL TTYPE -> FF FA 18 01 FF F0; IS vt100 ->
            // FF FA 18 01 FF F0 (ttype1=vt100); IS xterm -> FF FA 18 01 FF
            // F0 (ttype2=xterm). 3 SENDs total, >=2 after answers.
            using var stream = new ScriptedStream();
            stream.Enqueue([.. TtypeIsFrame("vt100"), .. TtypeIsFrame("xterm")]);
            using var session = NewSession(stream);
            await session.RequestTerminalTypesAsync(TimeSpan.FromMilliseconds(300));
            // Peer WILL agreement makes the cycle solicited and releases the
            // advanced preset, so the wire contains the initial SEND plus one
            // per answer plus the WILL-triggered probe and advanced frames.
            CountSubsequence(OutboundBytes(stream), new byte[] { 255, 250, 24, 1 }).Should().BeGreaterThanOrEqualTo(3);
        }

        [Fact]
        public async Task UnsolicitedTerminalTypeIs_Stored()
        {
            // audit2 §8 F2-PENDING-SB.
            // Reference: stream_writer.py:2537-2551 (_handle_sb_ttype IS
            // branch: server check only, decode + _ext_callback[TTYPE], no
            // pending check — cf. environ :2592-2594 which logs unsolicited
            // but also consumes); server.py:611-614 (stores ttype{n}+TERM
            // even unsolicited).
            // Repro (fresh server writer, no prior SEND, pending
            // SB+TTYPE=None): feed FF FA 18 00 "vt100" FF F0 -> TERM=vt100,
            // ttype1=vt100, outbound empty (no SEND since request_ttype
            // needs remote_option[TTYPE], but the value is stored).
            using var stream = new ScriptedStream();
            stream.Enqueue(TtypeIsFrame("vt100"));
            using var session = NewSession(stream);
            (await session.ReadAsync(TimeSpan.FromMilliseconds(300))).Should().BeEmpty();
            session.ClientTerminalTypes.Should().Equal("vt100");
        }

        [Fact]
        public async Task CaseVariantRepeat_ContinuesCycling()
        {
            // audit2 §3 TTYPE-CASE.
            // Reference: server.py:630 (ttype == ttype1) and :646
            // (ttype == _lastval) use case-sensitive ==.
            // Repro (same harness): IS XTERM -> FF FA 18 01 FF F0
            // (ttype1=XTERM); IS xterm -> FF FA 18 01 FF F0 (ttype2=xterm).
            // The second SEND proves xterm != XTERM is a new answer.
            // (Also needs the F2-TTYPE continuation; passes only when both
            // hold.)
            using var stream = new ScriptedStream();
            stream.Enqueue([.. TtypeIsFrame("XTERM"), .. TtypeIsFrame("xterm")]);
            using var session = NewSession(stream);
            await session.RequestTerminalTypesAsync(TimeSpan.FromMilliseconds(300));
            // Peer WILL agreement adds the WILL-triggered probe, so at least
            // the initial SEND plus one per answer is present.
            CountSubsequence(OutboundBytes(stream), new byte[] { 255, 250, 24, 1 }).Should().BeGreaterThanOrEqualTo(3);
        }
    }
}
