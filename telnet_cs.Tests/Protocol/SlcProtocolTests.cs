namespace telnet_cs.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using FluentAssertions;
    using Xunit;
    using telnet_cs.IO;
    using telnet_cs.Protocol;
    using telnet_cs.Server;

    /// <summary>
    /// LINEMODE/SLC behavior pins: each test asserts the telnetlib3 behavior.
    /// </summary>
    public class SlcProtocolTests
    {
        private static async Task<(string Output, ScriptedStream Stream)> ReadConfiguredAsync(Action<ByteStreamHandler> configure, params int[] reads)
        {
            var stream = new ScriptedStream(reads);
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1);
            configure(sut);
            var output = await sut.ReadAsync(TimeSpan.FromMilliseconds(50));
            return (output, stream);
        }

        [Fact]
        public void AckedDifferentLevelTriplet_IsDroppedWithoutReply()
        {
            // ACKed triplet at a different level is dropped without reply.
            // Reference: stream_writer.py:3050-3054 (same-level+ACK ->
            // return; any-ACK -> return — dropped, never stored/replied);
            // levels via slc.py:54,113-116 (mask & LEVELBITS).
            // Repro (server writer): _slc_process(0x03, SLC(0x81, 0x03))
            // (his=CANTCHANGE+ACK vs my=VARIABLE) -> _slc_buffer=[],
            // slctab[3] unchanged (mask 62, val 03).
            var state = new LinemodeState();
            state.ApplySlcAsServer(3, (byte)(1 | 128), 3).Should().BeNull();
            state.GetEntry(3).Should().Be(new SlcEntry(2, 3, 96));
        }

        [Fact]
        public void AckedSameLevelTriplet_ClientDoesNotAdopt()
        {
            // ACKed same-level triplet is not adopted (no client/server split).
            // Reference: stream_writer.py:3050-3051 (same-level+ACK ->
            // return); no client/server split in 3003-3055.
            // Repro (client writer): _slc_process(0x03, SLC(0x82, 0x04))
            // -> _slc_buffer=[], slctab[3].val stays 03.
            var state = new LinemodeState();
            state.ApplySlc(3, (byte)(2 | 128), 4).Should().BeNull();
            state.GetEntry(3).Value.Should().Be(3);
        }

        [Fact]
        public void ValuedCantChange_AdoptsPeerValueWithAck()
        {
            // Valued CANTCHANGE row adopts the peer value with ACK.
            // Reference: stream_writer.py:3092-3099 (valued row: val !=
            // theNULL -> set_value+set_mask, set ACK, _slc_add).
            // Repro: slctab[3]=(01,05), _slc_process(03,(02,07)) ->
            // buffer 03 82 07 (value 07 + ACK), stored val 07 mask 02.
            var state = new LinemodeState();
            state.SetEntry(3, 1, 5);
            var reply = state.ApplySlcAsServer(3, 2, 7);
            reply.Should().NotBeNull();
            reply.GetValueOrDefault().Value.Should().Be(7);
            (reply.GetValueOrDefault().Modifier & 128).Should().NotBe(0);
        }

        [Fact]
        public void OutOfRangeFunction_AnsweredNosupportWithDisableValue()
        {
            // Out-of-range function answered NOSUPPORT with the disable value.
            // _slc_add(func, SLC_nosupport())); slc.py:55 (NSLC=30),
            // slc.py:185-190,200 (SLC_nosupport=(NOSUPPORT, 0xFF)).
            // Repro: _slc_process(0x1F,(00,00)) -> buffer 1F 00 FF
            // (modifier 00, value 255 = _POSIX_VDISABLE).
            var state = new LinemodeState();
            var reply = state.ApplySlcAsServer(31, 0, 0);
            reply.Should().NotBeNull();
            reply.GetValueOrDefault().Modifier.Should().Be(0);
            reply.GetValueOrDefault().Value.Should().Be(255);
        }

        [Fact]
        public void NosupportReceipt_AnsweredWithDisableValue()
        {
            // NOSUPPORT receipt answered with the disable value.
            // -> SLC_nosupport()+ACK, _slc_add).
            // Repro: slctab[9]=(00,00), _slc_process(09,(00,7F)) ->
            // buffer 09 80 FF — 0xFF, peer 0x7F not echoed.
            var state = new LinemodeState();
            state.SetEntry(9, 0, 0);
            var reply = state.ApplySlcAsServer(9, 0, 0x7F);
            reply.Should().NotBeNull();
            reply.GetValueOrDefault().Value.Should().Be(255);
        }

        [Fact]
        public void DefaultImport_OmitsNosupportRows()
        {
            // Default import omits NOSUPPORT rows.
            // Reference: stream_writer.py:2979-2981 (if
            // slctab.get(...).nosupport: continue); same _slc_send serves
            // the func-0 DEFAULT-import path (:3022-3030).
            // Repro: _slc_send() on the default tab -> funcs [1..16], 16
            // triplets; 17 (FORW1)/18 (FORW2)/19-30 absent.
            var export = new LinemodeState().ExportTriplets(forImport: true);
            export.Should().NotBeNull();
            var bytes = export ?? [];
            var rendered = new List<byte>();
            for (int i = 0; i + 2 < bytes.Length; i += 3)
            {
                if (bytes[i] >= 17 && bytes[i + 1] == 3 && bytes[i + 2] == 0)
                {
                    rendered.Add(bytes[i]);
                }
            }

            rendered.Should().BeEmpty();
        }

        [Fact]
        public void FlushOnlyChange_IsIgnoredWithoutReply()
        {
            // FLUSH-only change is ignored without reply (value-only triplets
            // return early; FLUSH bits ignored).
            // Reference: stream_writer.py:3047-3049 (mylevel/level + value
            // only -> return early; FLUSH bits ignored).
            // Repro: _slc_process(03, mask 02|20=0x22, val 03) ->
            // _slc_buffer=[], entry still (62,03) — ignored, not
            // stored/ACKed.
            var state = new LinemodeState();
            state.ApplySlcAsServer(3, (byte)(2 | 32), 3).Should().BeNull();
            state.GetEntry(3).Should().Be(new SlcEntry(2, 3, 96));
        }

        [Fact]
        public void LoneForw2_IsAccepted()
        {
            // Lone FORW2 is accepted (no rule gates FORW2 on FORW1).
            // Reference: no FORW1-gates-FORW2 rule anywhere (grep FORW
            // only slc.py:206-207,244-272); func 18 default is nosupport
            // (00,FF), and _slc_change:3094 adopts since FF != theNULL.
            // Repro: _slc_process(0x12=18,(02,0x41='A')) -> buffer
            // 12 82 41 (ACK, value 0x41).
            var state = new LinemodeState();
            var reply = state.ApplySlcAsServer(18, 2, 65);
            reply.Should().NotBeNull();
            (reply.GetValueOrDefault().Modifier & 128).Should().NotBe(0);
            reply.GetValueOrDefault().Value.Should().Be(65);
        }

        [Fact]
        public void ReservedModifierBits_PreservedInReply()
        {
            // Reserved modifier bits are preserved in the reply.
            // Reference: stream_writer.py:3094-3098 (set_mask(full mask),
            // set_flag(ACK), _slc_add); slc.py:157-163 (full mask kept).
            // Repro: _slc_process(03,(02|10=0x12,05)) -> buffer 03 92 05
            // (0x92=02|10|80, 0x10 preserved).
            var state = new LinemodeState();
            var reply = state.ApplySlcAsServer(3, (byte)(2 | 0x10), 5);
            reply.Should().NotBeNull();
            (reply.GetValueOrDefault().Modifier & 0x10).Should().NotBe(0);
        }

        [Fact]
        public async Task ServerWillLinemode_SendsModeProposal()
        {
            // Server WILL LINEMODE sends a mode proposal (server half).
            // Reference: stream_writer.py:2229-2237 (iac(DO) + pending SB
            // + send_linemode(default_linemode)); :1470-1492 frames
            // FF FA 22 01 <mask> FF F0.
            // Repro (server writer): feed FF FB 22 -> sent=FF FD 22
            // FF FA 22 01 10 FF F0 — contains FF FD 22 and FF FA 22 01.
            var (output, stream) = await ReadConfiguredAsync(static s => { s.IsServerRole = true; }, 255, 251, 34);
            output.Should().BeEmpty();
            var outbound = stream.ByteWrites.SelectMany(w => w).ToArray();
            ContainsSubsequence(outbound, new byte[] { 255, 250, 34, 1 }).Should().BeTrue();
        }

        [Fact]
        public async Task NonAckedMode_DoesNotPublishSlc()
        {
            // Non-ACKed MODE does not publish SLC (publish half).
            // Reference: stream_writer.py:2851-2882 (non-ACK branch:
            // send_linemode(ACK) + early return) vs :2884-2924 (ACK branch
            // only: server and not _slc_sent -> _slc_start/_slc_send/
            // _slc_end).
            // Repro (server writer, after WILL LINEMODE): feed
            // FF FA 22 01 01 FF F0 -> sent=FF FA 22 01 05 FF F0 (MODE+ACK),
            // no FF FA 22 03 (LMODE_SLC=3), _slc_sent=False. (Needs a
            // session: the publish path requires ApplyLinemodeAsServer.)
            using var stream = new ScriptedStream(255, 251, 34, 255, 250, 34, 1, 1, 255, 240);
            using var session = new ServerSession(stream, new TelnetServerOptions(), CancellationToken.None);
            (await session.ReadAsync(TimeSpan.FromMilliseconds(300))).Should().BeEmpty();
            var outbound = stream.ByteWrites.SelectMany(w => w).ToArray();
            ContainsSubsequence(outbound, new byte[] { 255, 250, 34, 1 }).Should().BeTrue("the MODE+ACK reply proves the MODE was processed");
            stream.ByteWrites.Should().NotContain(w => w.Length >= 4 && w[0] == 255 && w[1] == 250 && w[2] == 34 && w[3] == 3);
        }

        private static bool ContainsSubsequence(byte[] haystack, byte[] needle)
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
                    return true;
                }
            }

            return false;
        }
    }
}
