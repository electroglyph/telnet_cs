namespace telnet_cs.Tests
{
    using System;
    using System.Diagnostics;
    using System.Linq;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using FluentAssertions;
    using Xunit;
    using telnet_cs.IO;
    using telnet_cs.Protocol;
    using telnet_cs.Server;
    using telnet_cs.Transport;

    /// <summary>
    /// Audit §3 proper-behavior tests. F-O1–O3/O5–O9/O11/O15 FAIL against
    /// current behavior; the F-O16a/d/e pins PASS (that code is already
    /// correct, some of it more correct than the reference).
    /// </summary>
    public class AuditProperOptionsTests
    {
        private static async Task<(string Output, byte[] Writes)> ReadScriptedAsync(params int[] reads)
        {
            using var stream = new ScriptedStream(reads);
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1);
            var output = await sut.ReadAsync(TimeSpan.FromMilliseconds(50));
            return (output, stream.ByteWrites.SelectMany(w => w).ToArray());
        }

        private static int[] TtypeIsFrame(string term)
        {
            return [255, 250, 24, 0, .. term.Select(c => (int)c), 255, 240];
        }

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

        [Fact]
        public async Task StatusIs_TerminatedWithIacSe()
        {
            // Decided: RFC 859 §5 text says bare SE but its worked example
            // shows IAC SE, and strict IAC-SE parsers swallow everything after
            // a bare SE into the SB buffer — so send IAC SE for interop while
            // still accepting both forms inbound.
            var (_, writes) = await ReadScriptedAsync(255, 253, 5, 255, 250, 5, 1, 255, 240);
            writes.Should().Equal(255, 251, 5, 255, 250, 5, 0, 251, 5, 255, 240);
        }

        [Fact]
        public void TtableVerbs_MatchRfc2066()
        {
            // Source of truth: RFC 2066 section 6 numbers the CHARSET verbs
            // REQUEST=1 ACCEPTED=2 REJECTED=3 TTABLE-IS=4 TTABLE-REJECTED=5
            // TTABLE-ACK=6 TTABLE-NAK=7. ~/telnetlib3/telnetlib3/telopt.py matches
            // exactly (range(1,8) unpacked in that order).
            // Our code: telnet_cs/Protocol/CharsetProtocol.cs:29-38 defines
            // TTableIs=4, TTableAck=5, TTableNak=6, TTableRejected=7, rotating 5/6/7.
            // Proof: asserting the constants plus BuildTTableRejected()==5 fails on the
            // rotated values. Misclassification follows: inbound 5 (REJECTED) reads as
            // ACK so pending never clears, and outbound decline emits 07 not 05. This
            // test is correct; un-rotating the three verbs is the fix.
            CharsetProtocol.TTableIs.Should().Be((byte)4);
            CharsetProtocol.TTableRejected.Should().Be((byte)5);
            CharsetProtocol.TTableAck.Should().Be((byte)6);
            CharsetProtocol.TTableNak.Should().Be((byte)7);
            CharsetProtocol.BuildTTableRejected().Should().Equal((byte)5);
        }

        [Fact]
        public async Task TtableIs_DeclinedWithRejectedVerb()
        {
            // Source of truth: RFC 2066 requires TTABLE-REJECTED (05) in response to
            // TTABLE-IS when the table is not accepted; the minimal set
            // REQUEST/ACCEPTED/REJECTED/TTABLE-REJECTED MUST be supported. The
            // reference never ACKs tables (stream_writer.py raises for TTABLE), so the
            // RFC is the wire authority here.
            // Our code: ByteStreamHandler.cs ReplyCharsetAnswerAsync emits
            // BuildTTableRejected() which is [7] while the verbs stay rotated, so wire
            // FF FA 2A 04 ... FF F0 earns FF FA 2A 07 FF F0.
            // Proof: inbound TTABLE-IS must earn exactly FF FA 2A 05 FF F0; observing
            // 07 at index 3 proves the rotation. This test is correct.
            var (_, writes) = await ReadScriptedAsync(255, 250, 42, 4, 65, 255, 240);
            writes.Should().Equal(255, 250, 42, 5, 255, 240);
        }

        [Fact]
        public async Task LinemodeMode_AcksFullMask()
        {
            // Source of truth: the reference echoes any suggested MODE mask verbatim
            // plus ACK. ~/telnetlib3/telnetlib3/stream_writer.py answers MODE with
            // mask | LMODE_MODE_ACK (0x04), no subsetting; slc.py defines ACK=4,
            // SOFT_TAB=8, LIT_ECHO=16. RFC 1184 permits subsetting but the ported
            // behavior is verbatim echo.
            // Our code: Protocol/LinemodeProtocol.cs:42 SupportedModeBits = Edit |
            // TrapSignal (3), and LinemodeState.cs masks requested & Supported before
            // OR-ing ACK, so client mask 0x08 yields 0x04 instead of 0x0C, silently
            // clearing SOFT_TAB/LIT_ECHO the peer requested.
            // Proof: agreed LINEMODE plus inbound FF FA 22 01 08 FF F0 must contain
            // FF FA 22 01 0C; observing 04 proves the subset. This test is correct.
            var (_, writes) = await ReadScriptedAsync(255, 253, 34, 255, 250, 34, 1, 8, 255, 240);
            ContainsFrame(writes, new byte[] { 255, 250, 34, 1, 12 }).Should().BeTrue();
        }

        [Fact]
        public async Task LinemodeMode_WithoutNegotiation_Silent()
        {
            // Source of truth: MODE with LINEMODE never negotiated is ignored.
            // stream_writer.py warns and returns when neither local nor remote LINEMODE
            // is enabled, sending nothing.
            // Our code: ByteStreamHandler.cs ReplyModeAsync has no IsEnabledByUs/Peer
            // gate, so stray FF FA 22 01 01 FF F0 earns FF FA 22 01 05 FF F0.
            // Proof: with no prior WILL/DO LINEMODE, the same bytes must yield empty
            // writes; any reply proves the missing gate. This test is correct.
            var (_, writes) = await ReadScriptedAsync(255, 250, 34, 1, 1, 255, 240);
            writes.Should().BeEmpty();
        }

        [Fact]
        public async Task ForwardMaskDo_AcceptedSilently()
        {
            // Source of truth: a well-formed DO FORWARDMASK mask is stored silently.
            // RFC 1184 section 5: only the DO-sender may propose DO FORWARDMASK mask;
            // the client answers WILL/WONT, DONT must be empty and DO non-empty.
            // stream_writer.py stores _forwardmask and marks local SB+FORWARDMASK
            // without emitting WONT for a well-formed client DO; malformed empty-DO /
            // payload-DONT are warned on, not silently accepted.
            // Our code: ByteStreamHandler.cs always WONTs a well-formed DO plus mask,
            // keeps no bookkeeping, and silently accepts the malformed shapes.
            // Proof: agreed LINEMODE plus FF FA 22 FD 02 01 FF F0 must never contain
            // FF FA 22 FC 02; observing it proves the refusal. This test is correct.
            var (_, writes) = await ReadScriptedAsync(255, 253, 34, 255, 250, 34, 253, 2, 1, 255, 240);
            ContainsFrame(writes, new byte[] { 255, 250, 34, 252, 2 }).Should().BeFalse();
        }

        [Fact]
        public async Task EnvironSend_VolunteersRefSetWithUtf8Spelling()
        {
            // Source of truth: the reference volunteers COLORTERM, never DISPLAY, and
            // spells LANG per str(encoding). ~/telnetlib3/telnetlib3/client.py
            // DEFAULT_SEND_ENVIRON is (TERM, LANG, COLUMNS, LINES, COLORTERM); DISPLAY
            // is deliberately unavailable (security comment) and USER only when
            // allow-listed. LANG is DEFAULT_LOCALE + "." + str(encoding), with default
            // encoding "utf8" giving "en_US.utf8" (no hyphen).
            // Our code: Protocol/EnvironmentProtocol.cs volunteers USER+DISPLAY
            // (+TERM/LANG/COLUMNS/LINES), never COLORTERM/HOME/SHELL, and
            // ByteStreamHandler.cs builds "en_US." + TextEncoding.WebName, giving
            // "en_US.utf-8" for UTF8.
            // Proof: SEND-all must contain COLORTERM and "en_US.utf8" and must not
            // contain "utf-8"; any other spelling proves the divergence. This test is
            // correct.
            using var stream = new ScriptedStream(255, 253, 39, 255, 250, 39, 1, 255, 240);
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1) { TextEncoding = Encoding.UTF8 };
            await sut.ReadAsync(TimeSpan.FromMilliseconds(200));
            string reply = Encoding.ASCII.GetString(stream.ByteWrites.SelectMany(w => w).ToArray());
            reply.Should().Contain("COLORTERM");
            reply.Should().Contain("en_US.utf8");
            reply.Should().NotContain("utf-8");
        }

        [Fact]
        public async Task XdisplaySend_Unconfigured_EmptyIs()
        {
            // Source of truth: an unconfigured SEND earns an empty IS, never silence.
            // RFC 1096 section 4 defines SEND/IS with only WILL allowed to send IS; it
            // does not forbid an empty display string. stream_writer.py always answers
            // with IAC SB XDISPLOC IS <str>, and client.py defaults xdisploc to "",
            // yielding an empty IS.
            // Our code: ByteStreamHandler.cs breaks silently when XDisplayLocation is
            // null, so the server TryConsumeXDisplay times out instead of recording "".
            // Proof: inbound FF FA 23 01 FF F0 must earn exactly FF FA 23 00 FF F0;
            // silence proves the missing empty-IS path. This test is correct.
            var (_, writes) = await ReadScriptedAsync(255, 250, 35, 1, 255, 240);
            writes.Should().Equal(255, 250, 35, 0, 255, 240);
        }

        [Fact]
        public async Task TtypeEmptyAnswer_StopsCycle()
        {
            // Source of truth: an empty non-first TTYPE answer ends the phase.
            // ~/telnetlib3/telnetlib3/server.py stops cycling when ttype is empty or
            // past TTYPE_LOOPMAX and proceeds to the environ phase; only the framing
            // (SEND=1/IS=0) and MaxLength=40 truncation are strict on both sides.
            // Our code: ServerSession.Collectors.cs returns early when the chain is
            // non-empty without clearing the TTYPE expectation, so PollForResponse
            // stalls to the full timeout.
            // Proof: [IS "xterm", IS ""] with a 3 s budget must return ["xterm"] in
            // well under 2 s; taking the full timeout proves the stop is missing. This
            // test is correct.
            using var stream = new ScriptedStream([.. TtypeIsFrame("xterm"), .. TtypeIsFrame(string.Empty)]);
            using var session = new ServerSession(stream, new TelnetServerOptions(), CancellationToken.None);
            var sw = Stopwatch.StartNew();
            var result = await session.RequestTerminalTypesAsync(TimeSpan.FromSeconds(3));
            sw.Stop();
            result.Should().Equal("xterm");
            sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
        }

        [Fact]
        public async Task TtypeAnsiThenEmpty_ArmsEnvironWithoutTimeout()
        {
            // Source of truth: after a deferring ANSI first answer, an empty second
            // answer still releases the environ phase immediately.
            // ~/telnetlib3/telnetlib3/server.py defers environ on ttype1 == ANSI but
            // calls _negotiate_environ (IAC DO NEW_ENVIRON) on ttype2, even when empty;
            // the empty path also hits the cycle-stop branch. MUD set and MS
            // ANSI/VT100 guards are identical on both stacks and stay pinned elsewhere.
            // Our code only arms environ for non-ANSI answers and does nothing for an
            // empty second answer, so DO NEW_ENVIRON waits for the final collection
            // timeout in FlushDeferredNegotiationAsync.
            // Proof: [IS "ANSI", IS ""] with RequestNewEnvironment must show
            // FF FD 27 within 800 ms of a 3 s poll; absence proves the arming gap.
            // This test is correct, as is the known-then-empty sibling.
            var options = new TelnetServerOptions { RequestNewEnvironment = true };
            using var stream = new ScriptedStream([.. TtypeIsFrame("ANSI"), .. TtypeIsFrame(string.Empty)]);
            using var session = new ServerSession(stream, options, CancellationToken.None);
            var task = session.RequestTerminalTypesAsync(TimeSpan.FromSeconds(3));
            await Task.Delay(800);
            byte[] mid = stream.ByteWrites.SelectMany(w => w).ToArray();
            ContainsFrame(mid, new byte[] { 255, 253, 39 }).Should().BeTrue("empty second answer releases DO NEW_ENVIRON without waiting for the final timeout");
            (await task).Should().Equal("ANSI");
        }

        [Fact]
        public async Task TtypeKnownThenEmpty_ArmsEnvironWithoutTimeout()
        {
            // F-O15: like the ANSI case, a known first answer followed by an
            // empty second answer releases DO NEW_ENVIRON immediately.
            var options = new TelnetServerOptions { RequestNewEnvironment = true };
            using var stream = new ScriptedStream([.. TtypeIsFrame("xterm"), .. TtypeIsFrame(string.Empty)]);
            using var session = new ServerSession(stream, options, CancellationToken.None);
            var task = session.RequestTerminalTypesAsync(TimeSpan.FromSeconds(3));
            await Task.Delay(800);
            byte[] mid = stream.ByteWrites.SelectMany(w => w).ToArray();
            ContainsFrame(mid, new byte[] { 255, 253, 39 }).Should().BeTrue("empty second answer releases DO NEW_ENVIRON without waiting for the final timeout");
            (await task).Should().Equal("xterm");
        }

        [Fact]
        public async Task TspeedExtraSegments_ParsedLeniently()
        {
            // Source of truth: the reference parses leniently. stream_writer.py splits
            // at the first two commas and int()-parses (which trims whitespace),
            // ignoring any tail, so "1200,1200,9600" yields (1200,1200) and
            // " 1200,1200" is accepted. RFC 1079 section 4 is strict, but the ported
            // feature follows the reference leniency; leading-zero normalization
            // already agrees on both.
            // Our code: Protocol/TerminalSpeedProtocol.cs requires exactly two
            // ASCII-digit parts, so "1,2,3" validates to null.
            // Proof: SB TSPEED IS "1,2,3" must resolve to "1,2" via
            // RequestTerminalSpeedAsync; null proves the strictness gap. This test is
            // correct.
            using var stream = new ScriptedStream(255, 250, 32, 0, 49, 44, 50, 44, 51, 255, 240);
            using var session = new ServerSession(stream, new TelnetServerOptions(), CancellationToken.None);
            (await session.RequestTerminalSpeedAsync(TimeSpan.FromSeconds(2))).Should().Be("1,2");
        }

        [Fact]
        public async Task GoAhead_SuppressedWhileSga()
        {
            // Source of truth: GA must not be sent while local SGA is in effect.
            // RFC 858 section 5: with SGA in effect the sender need not transmit GAs;
            // IAC GA should be treated as NOP and should not normally be sent. The
            // reference send_ga returns False when local SGA is enabled.
            // Receive gates (GA/EOR) and the EOR send gate are already correct; only
            // ServerSession.Negotiation.cs SendCommand(GoAhead) lacks the SGA gate and
            // always writes FF F9.
            // Proof: after opening preset (WILL SGA) plus peer DO SGA, SendCommand(GA)
            // must emit no FF F9; observing it proves the missing send gate. This test
            // is correct.
            using var stream = new ScriptedStream();
            using var session = new ServerSession(stream, new TelnetServerOptions(), CancellationToken.None);
            await session.SendOpeningPresetAsync(CancellationToken.None);
            stream.Enqueue(255, 253, 3);
            await session.ReadAsync(TimeSpan.FromMilliseconds(500));
            await session.SendCommand(Protocol.Commands.GoAhead, CancellationToken.None);
            byte[] writes = stream.ByteWrites.SelectMany(w => w).ToArray();
            ContainsFrame(writes, new byte[] { 255, 249 }).Should().BeFalse();
        }

        [Fact]
        public void EncodingFromLang_TrailingDot_Empty()
        {
            // Source of truth: "en_US." carries an empty codeset.
            // ~/telnetlib3/telnetlib3/accessories.py splits LANG on "." once and on "@"
            // for modifiers, returning "" for "en_US." (only a missing "." yields None).
            // Downstream force-binary predicates already agree; only direct callers see
            // the null-vs-empty difference.
            // Our code: Protocol/TelnetAccessories.cs returns null when the codeset
            // part is empty.
            // Proof: EncodingFromLang("en_US.") must be string.Empty; null proves the
            // gap. This test is correct.
            TelnetAccessories.EncodingFromLang("en_US.").Should().Be(string.Empty);
        }

        [Fact]
        public async Task NawsVerbFirst_Accepted()
        {
            // F-O16a pin (already correct): our own 5-byte verb-first shape is
            // accepted alongside the strict RFC 1073 shape.
            using var stream = new ScriptedStream();
            stream.Enqueue([255, 250, 31, 0, 0, 80, 0, 24, 255, 240]);
            using var session = new ServerSession(stream, new TelnetServerOptions(), CancellationToken.None);
            await session.ReadAsync(TimeSpan.FromMilliseconds(500));
            session.ClientWindowSize.Should().Be(((ushort)80, (ushort)24));
        }

        [Fact]
        public void CharsetSeparator_FallsBackFromSpace()
        {
            // F-O16d pin (already correct, RFC 2066): spaceless names keep the
            // space separator; names with spaces pick ';'/','/'/'.
            CharsetProtocol.BuildRequest(["US-ASCII", "LATIN-1"])[1].Should().Be((byte)' ');
            CharsetProtocol.BuildRequest(["A B", "C"])[1].Should().NotBe((byte)' ');
        }

        [Fact]
        public void EnvironEscapes_RoundTripValueByte()
        {
            // F-O16e pin (already correct, RFC 1408 §4.3): a VALUE byte inside
            // a value is escaped and survives the round trip.
            var entries = EnvironmentProtocol.ParseEntries(new byte[] { 0, 0, (byte)'A', 1, (byte)'x', 2, 1, (byte)'y' });
            entries.Should().ContainSingle().Which.Should().Be((false, "A", "x\u0001y"));
        }
    }
}
