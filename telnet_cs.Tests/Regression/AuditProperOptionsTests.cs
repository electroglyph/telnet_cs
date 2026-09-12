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
            // F-O1: REQUEST=1 ACCEPTED=2 REJECTED=3 TTABLE-IS=4
            // TTABLE-REJECTED=5 TTABLE-ACK=6 TTABLE-NAK=7.
            CharsetProtocol.TTableIs.Should().Be((byte)4);
            CharsetProtocol.TTableRejected.Should().Be((byte)5);
            CharsetProtocol.TTableAck.Should().Be((byte)6);
            CharsetProtocol.TTableNak.Should().Be((byte)7);
            CharsetProtocol.BuildTTableRejected().Should().Equal((byte)5);
        }

        [Fact]
        public async Task TtableIs_DeclinedWithRejectedVerb()
        {
            // F-O1 wire: TTABLE-IS earns TTABLE-REJECTED (05), not 07.
            var (_, writes) = await ReadScriptedAsync(255, 250, 42, 4, 65, 255, 240);
            writes.Should().Equal(255, 250, 42, 5, 255, 240);
        }

        [Fact]
        public async Task LinemodeMode_AcksFullMask()
        {
            // F-O2: MODE 0x08 (SOFT_TAB) earns 0x08|ACK, not EDIT|TRAPSIG|ACK.
            var (_, writes) = await ReadScriptedAsync(255, 253, 34, 255, 250, 34, 1, 8, 255, 240);
            ContainsFrame(writes, new byte[] { 255, 250, 34, 1, 12 }).Should().BeTrue();
        }

        [Fact]
        public async Task LinemodeMode_WithoutNegotiation_Silent()
        {
            // F-O3: stray MODE with no LINEMODE agreement earns no reply.
            var (_, writes) = await ReadScriptedAsync(255, 250, 34, 1, 1, 255, 240);
            writes.Should().BeEmpty();
        }

        [Fact]
        public async Task ForwardMaskDo_AcceptedSilently()
        {
            // F-O5: a well-formed DO FORWARDMASK is stored, not WONTed.
            var (_, writes) = await ReadScriptedAsync(255, 253, 34, 255, 250, 34, 253, 2, 1, 255, 240);
            ContainsFrame(writes, new byte[] { 255, 250, 34, 252, 2 }).Should().BeFalse();
        }

        [Fact]
        public async Task EnvironSend_VolunteersRefSetWithUtf8Spelling()
        {
            // F-O6: default SEND-all volunteers COLORTERM (never DISPLAY) and
            // spells LANG per the reference ("en_US.utf8", no hyphen).
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
            // F-O9: SEND with no display configured earns an empty IS.
            var (_, writes) = await ReadScriptedAsync(255, 250, 35, 1, 255, 240);
            writes.Should().Equal(255, 250, 35, 0, 255, 240);
        }

        [Fact]
        public async Task TtypeEmptyAnswer_StopsCycle()
        {
            // F-O7: an empty non-first answer ends the TTYPE phase instead of
            // stalling to the full timeout.
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
            // F-O15: after a deferring ANSI first answer, an empty second
            // answer releases DO NEW_ENVIRON immediately — not via the final
            // collection timeout.
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
            // F-O8: "1,2,3" resolves like the reference: ("1","2").
            using var stream = new ScriptedStream(255, 250, 32, 0, 49, 44, 50, 44, 51, 255, 240);
            using var session = new ServerSession(stream, new TelnetServerOptions(), CancellationToken.None);
            (await session.RequestTerminalSpeedAsync(TimeSpan.FromSeconds(2))).Should().Be("1,2");
        }

        [Fact]
        public async Task GoAhead_SuppressedWhileSga()
        {
            // F-O11: SendCommand(GA) emits nothing once SGA is in effect.
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
            // F-O16c: "en_US." carries an empty codeset, matching the reference.
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
