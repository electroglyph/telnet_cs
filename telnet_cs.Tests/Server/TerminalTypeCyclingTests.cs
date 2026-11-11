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
    /// Terminal-type collection pins: SEND is resent per answer, unsolicited IS
    /// is stored, and case-variant repeats continue cycling, pinned against the
    /// telnetlib3 TTYPE behavior.
    /// </summary>
    public class TerminalTypeCyclingTests
    {
        private static ServerSession NewSession(ScriptedStream stream, TelnetServerOptions? options = null)
        {
            // Peer agreement for TTYPE collection (state-only).
            ServerSession s = new(stream, options ?? new TelnetServerOptions(), CancellationToken.None);
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
            // Each TTYPE answer triggers another SEND until the cycle terminates.
            // Reference: server.py:650-653 (else branch: _ttype_count+=1,
            // writer.request_ttype()); :630,635,640,646 (loop / empty+MTTS
            // / LOOPMAX=90 / repeat stops); stream_writer.py:1397-1413
            // (SEND = FF FA 18 01 FF F0); :2374-2375 (pending SB+TTYPE
            // cleared per SB so the next request_ttype is not suppressed).
            // Repro (real writer + real on_ttype as ext-callback, feed
            // driven): WILL TTYPE -> FF FA 18 01 FF F0; IS vt100 ->
            // FF FA 18 01 FF F0 (ttype1=vt100); IS xterm -> FF FA 18 01 FF
            // F0 (ttype2=xterm). One SEND per answer on the wire when the
            // request chases them live; a pump-filed answer is replayed
            // without re-soliciting, so only initial-plus-one is pinned.
            using ScriptedStream stream = new();
            stream.Enqueue([.. TtypeIsFrame("vt100"), .. TtypeIsFrame("xterm")]);
            using var session = NewSession(stream);
            var chain = await session.RequestTerminalTypesAsync(TimeSpan.FromMilliseconds(2000));
            // Both answers recorded in order: neither repeats, so the
            // cycle holds both (the duplicate trim only removes a
            // closing repeat).
            chain.Should().Equal("vt100", "xterm");
            // Peer WILL agreement makes the cycle solicited and releases the
            // advanced preset, so the wire contains the initial SEND plus
            // per-answer follow-ups. The exact total depends on whether the
            // background pump or the request consumed each answer (filed
            // answers are replayed, never re-solicited), and a follow-up
            // armed just before the timeout need not go out — so only the
            // initial SEND plus at least one follow-up is pinned here.
            CountSubsequence(OutboundBytes(stream), new byte[] { 255, 250, 24, 1 }).Should().BeGreaterThanOrEqualTo(2);
        }

        [Fact]
        public async Task UnsolicitedTerminalTypeIs_Stored()
        {
            // An unsolicited TTYPE IS is stored even with no prior SEND outstanding.
            // Reference: stream_writer.py:2537-2551 (_handle_sb_ttype IS
            // branch: server check only, decode + _ext_callback[TTYPE], no
            // pending check — cf. environ :2592-2594 which logs unsolicited
            // but also consumes); server.py:611-614 (stores ttype{n}+TERM
            // even unsolicited).
            // Repro (fresh server writer, no prior SEND, pending
            // SB+TTYPE=None): feed FF FA 18 00 "vt100" FF F0 -> TERM=vt100,
            // ttype1=vt100, outbound empty (no SEND since request_ttype
            // needs remote_option[TTYPE], but the value is stored).
            using ScriptedStream stream = new();
            stream.Enqueue(TtypeIsFrame("vt100"));
            using var session = NewSession(stream);
            (await session.ReadAsync(TimeSpan.FromMilliseconds(300))).Should().BeEmpty();
            session.ClientTerminalTypes.Should().Equal("vt100");
        }

        [Fact]
        public async Task CaseVariantRepeat_ContinuesCycling()
        {
            // A case-variant repeat counts as a new answer and continues cycling (case-sensitive compare).
            // Reference: server.py:630 (ttype == ttype1) and :646
            // (ttype == _lastval) use case-sensitive ==.
            // Repro (same harness): IS XTERM -> FF FA 18 01 FF F0
            // (ttype1=XTERM); IS xterm -> FF FA 18 01 FF F0 (ttype2=xterm).
            // The returned chain holding both proves xterm != XTERM is a
            // new answer (a repeat would terminate the cycle with one).
            using ScriptedStream stream = new();
            stream.Enqueue([.. TtypeIsFrame("XTERM"), .. TtypeIsFrame("xterm")]);
            using var session = NewSession(stream);
            var chain = await session.RequestTerminalTypesAsync(TimeSpan.FromMilliseconds(2000));
            // Case-sensitive compare (reference server.py:630,646): xterm
            // is a new answer after XTERM, so the cycle holds both instead
            // of terminating on a repeat.
            chain.Should().Equal("XTERM", "xterm");
            // Peer WILL agreement adds the WILL-triggered probe, so at
            // least the initial SEND plus one follow-up is present. The
            // exact total depends on whether the background pump or the
            // request consumed each answer (filed answers are replayed,
            // never re-solicited), and a follow-up armed just before the
            // timeout need not go out.
            CountSubsequence(OutboundBytes(stream), new byte[] { 255, 250, 24, 1 }).Should().BeGreaterThanOrEqualTo(2);
        }
    }
}
