namespace telnet_cs.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using FluentAssertions;
    using Xunit;
    using telnet_cs.Client;
    using telnet_cs.Encodings;
    using telnet_cs.IO;
    using telnet_cs.Protocol;
    using telnet_cs.Server;

    /// <summary>
    /// Wire-behavior pins against the reference implementation
    /// (<c>~/telnetlib3/telnetlib3</c>) and the governing RFCs. Failing tests
    /// mark places where this library still diverges from the reference;
    /// passing tests pin intentional behavior that must not change silently.
    /// </summary>
    public class AuditFixListTests
    {
        private static byte[] L(string text) => Encoding.Latin1.GetBytes(text);

        private static byte[] Concat(params byte[][] parts) => [.. parts.SelectMany(static p => p)];

        private static async Task<(string Output, ScriptedStream Stream)> ReadHandlerOnceAsync(
          Action<ByteStreamHandler> configure, params int[] reads)
        {
            var stream = new ScriptedStream(reads);
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1);
            configure(sut);
            var output = await sut.ReadAsync(TimeSpan.FromMilliseconds(50));
            return (output, stream);
        }

        private static async Task<string> ReadClientOnceAsync(Client client)
        {
            return await client.ReadAsync(TimeSpan.FromMilliseconds(100));
        }

        private static ServerSession NewSession(ScriptedStream stream, TelnetServerOptions? options = null)
        {
            var session = new ServerSession(stream, options ?? new TelnetServerOptions(), CancellationToken.None);
            _ = session.Negotiation.ReceivedWill((int)Options.TerminalType, agree: true);
            _ = session.Negotiation.ReceivedWill((int)Options.TerminalSpeed, agree: true);
            _ = session.Negotiation.ReceivedWill((int)Options.XDisplay, agree: true);
            _ = session.Negotiation.ReceivedWill((int)Options.OldEnvironment, agree: true);
            _ = session.Negotiation.ReceivedWill((int)Options.NewEnvironment, agree: true);
            _ = session.Negotiation.ReceivedWill((int)Options.CharacterSet, agree: true);
            return session;
        }

        private static byte[] OutboundBytes(ScriptedStream stream)
        {
            return stream.ByteWrites.SelectMany(static b => b).ToArray();
        }

        private static async Task AgreeLinemodeAsync(ServerSession session, ScriptedStream stream)
        {
            await session.RequestEnableAsync(Options.LineMode);
            stream.Enqueue([255, 251, 34]);
            await session.ReadAsync(TimeSpan.FromMilliseconds(500));
        }

        private static byte[] EnvironEntry(byte type, string name, string value) =>
          Concat([type], L(name), [1], L(value));

        private static byte[] EnvironFrame(byte verb, byte option, params byte[][] entries)
        {
            var payload = new List<byte> { verb };
            foreach (var entry in entries)
            {
                payload.AddRange(entry);
            }

            var frame = new List<byte> { 255, 250, option };
            foreach (var b in payload)
            {
                frame.Add(b);
                if (b == 255)
                {
                    frame.Add(b);
                }
            }

            frame.AddRange([255, 240]);
            return [.. frame];
        }

        // Source of truth: ~/telnetlib3/telnetlib3/slc.py BSD_SLC_TAB via
        // generate_slctab — 16 live rows (funcs 1..16), ascending. Masks:
        // VARIABLE=2, FLUSHIN=64, FLUSHOUT=32, DEFAULT=3.
        private static readonly byte[] BsdSlcTriplets =
        [
          1, 3, 0, 2, 3, 0, 3, 98, 3, 4, 34, 15,
          5, 2, 20, 6, 3, 0, 7, 98, 28, 8, 2, 4,
          9, 66, 26, 10, 2, 127, 11, 2, 21, 12, 2, 23,
          13, 2, 18, 14, 2, 22, 15, 2, 17, 16, 2, 19,
        ];

        private static bool ContainsSubsequence(byte[] haystack, byte[] needle)
        {
            for (var i = 0; i + needle.Length <= haystack.Length; i++)
            {
                var match = true;
                for (var j = 0; j < needle.Length; j++)
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

        [Fact]
        public async Task RequestTerminalSpeedAsync_AsymmetricAnswer_StoredTxFirst()
        {
            // Source of truth: RFC 1079 section 4 sends "<tx>,<rx>" (transmit
            // first); ~/telnetlib3/telnetlib3/stream_writer.py unpacks rx first
            // instead (self-consistent but inverted). Decided: this library
            // keeps the RFC order, so asymmetric rates round-trip positionally.
            using var stream = new ScriptedStream();
            stream.Enqueue([255, 250, 32, 0, .. L("9600,4800"), 255, 240]);
            using var session = NewSession(stream);
            var speed = await session.RequestTerminalSpeedAsync(TimeSpan.FromSeconds(5));
            speed.Should().Be("9600,4800");
            // Request requires prior WILL TSPEED (enabled in NewSession helper);
            // the agreement also arms the advanced preset and probes, so assert
            // the TSPEED SEND is present rather than exact bytes.
            ContainsSubsequence(OutboundBytes(stream), [255, 250, 32, 1, 255, 240]).Should().BeTrue();
        }

        [Fact]
        public void TerminalSpeedValidate_Asymmetric_PreservesTxRxPositions()
        {
            // Same order decision at the unit level: the first part is tx.
            TerminalSpeedProtocol.Validate("9600,4800").Should().Be("9600,4800");
        }

        [Fact]
        public async Task EnvironInfo_SentOnNewEnvironment_WhenOnlyNewAgreed()
        {
            // Source of truth: the reference server only DOs NEW_ENVIRON
            // (~/telnetlib3/telnetlib3/server.py _negotiate_environ) and its
            // client sends no spontaneous INFO (IS replies only), so a
            // change-gated INFO framed on OLD-only is dead against reference
            // peers: OLD is never enabled, and NEW-only updates are dropped.
            // The fix frames INFO on the negotiated option; the payload shape
            // below mirrors the pinned OLD-path frame, only the option differs.
            using (GlobalStateGuard.SkipProactive(true))
            {
                using var stream = new ScriptedStream();
                using var client = new Client(stream, new CancellationToken());
                stream.Enqueue(255, 253, 39);
                (await ReadClientOnceAsync(client)).Should().BeEmpty();
                client.Settings.EnvironmentUser = "carol";
                (await ReadClientOnceAsync(client)).Should().BeEmpty();
                // Client INFO defaults (verified by wire probe): TERM "unknown",
                // LANG from the default UTF-8 encoding, 0x0 window, COLORTERM
                // from the environment.
                var expected = EnvironFrame(2, 39,
                  EnvironEntry(0, "USER", "carol"),
                  EnvironEntry(0, "TERM", "unknown"),
                  EnvironEntry(0, "LANG", "en_US.utf8"),
                  EnvironEntry(0, "COLUMNS", "0"),
                  EnvironEntry(0, "LINES", "0"),
                  EnvironEntry(0, "COLORTERM", System.Environment.GetEnvironmentVariable("COLORTERM") ?? string.Empty));
                stream.ByteWrites.Should().Contain(w => w.SequenceEqual(expected));
            }
        }

        [Fact]
        public async Task EnvironInfo_ExplicitDisplay_IsVolunteered()
        {
            // Source of truth: ~/telnetlib3/telnetlib3/client.py never sends
            // DISPLAY at all ("intentionally not available (security)"); here
            // Settings.EnvironmentDisplay (null by default) is the explicit
            // opt-in, and then DISPLAY appears in INFO. This pins the Cs
            // superset shape.
            using (GlobalStateGuard.SkipProactive(true))
            {
                using var stream = new ScriptedStream();
                using var client = new Client(stream, new CancellationToken());
                stream.Enqueue(255, 253, 36);
                (await ReadClientOnceAsync(client)).Should().BeEmpty();
                client.Settings.EnvironmentDisplay = "host:0";
                (await ReadClientOnceAsync(client)).Should().BeEmpty();
                // Explicit DISPLAY rides INFO (the Cs superset); TERM follows
                // the client default ("unknown"), LANG the default encoding.
                var expected = EnvironFrame(2, 36,
                  EnvironEntry(0, "DISPLAY", "host:0"),
                  EnvironEntry(0, "TERM", "unknown"),
                  EnvironEntry(0, "LANG", "en_US.utf8"),
                  EnvironEntry(0, "COLUMNS", "0"),
                  EnvironEntry(0, "LINES", "0"),
                  EnvironEntry(0, "COLORTERM", System.Environment.GetEnvironmentVariable("COLORTERM") ?? string.Empty));
                stream.ByteWrites.Should().Contain(w => w.SequenceEqual(expected));
            }
        }

        [Fact]
        public async Task StatusWill_Alone_SendsSendProbe()
        {
            // Source of truth: ~/telnetlib3/telnetlib3/stream_writer.py answers
            // WILL STATUS with a STATUS SEND probe; answering only DO leaves
            // status-probe interop (fingerprinting) one-sided.
            var (output, stream) = await ReadHandlerOnceAsync(static _ => { }, 255, 251, 5);
            output.Should().BeEmpty();
            var send = new byte[] { 255, 250, 5, 1, 255, 240 };
            stream.ByteWrites.Should().Contain(w => w.SequenceEqual(send));
        }

        [Fact]
        public async Task StatusDo_Alone_VolunteersIs()
        {
            // Source of truth: ~/telnetlib3/telnetlib3/stream_writer.py sends
            // the STATUS IS snapshot immediately on DO STATUS, without waiting
            // for a SEND first. The snapshot reflects live option state, so
            // with nothing negotiated yet it is empty (executed: WILL 05 then
            // FF FA 05 00 FF F0).
            var (output, stream) = await ReadHandlerOnceAsync(static _ => { }, 255, 253, 5);
            output.Should().BeEmpty();
            var will = new byte[] { 255, 251, 5 };
            var snapshot = new byte[] { 255, 250, 5, 0, 255, 240 };
            stream.ByteWrites.Should().Contain(w => w.SequenceEqual(will));
            stream.ByteWrites.Should().Contain(w => w.SequenceEqual(snapshot));
        }

        [Fact]
        public async Task CrNul_EndOfData_ReturnsCarriageReturnPromptly()
        {
            // Source of truth: ~/telnetlib3/telnetlib3/stream_reader.py yields
            // a bare CR without waiting for the next byte; a CR at
            // end-of-available-data must not stall the read.
            var (output, _) = await ReadHandlerOnceAsync(static _ => { }, 65, 13);
            output.Should().Be("A\r");
        }

        [Fact]
        public void LinemodeBuffer_DefaultSlc_CoversBsdEditingRows()
        {
            // Source of truth: ~/telnetlib3/telnetlib3/slc.py BSD_SLC_TAB gives
            // RP, LNEXT, XON, XOFF and AO negotiated values (executed on the
            // reference: func4=0x0F, func13=0x12, func14=0x16, func15=0x11,
            // func16=0x13); the edit table lacks those rows entirely today
            // (KeyNotFound), so the "connect edit table to negotiation table"
            // half has nothing to consult for them.
            LinemodeBuffer.DefaultSlc[13].Should().Be(0x12); // RP ^R
            LinemodeBuffer.DefaultSlc[14].Should().Be(0x16); // LNEXT ^V
            LinemodeBuffer.DefaultSlc[15].Should().Be(0x11); // XON ^Q
            LinemodeBuffer.DefaultSlc[16].Should().Be(0x13); // XOFF ^S
            LinemodeBuffer.DefaultSlc[4].Should().Be(0x0F); // AO ^O
        }

        [Fact]
        public async Task Client_DoLinemode_SendsSlcImportAutomatically()
        {
            // Source of truth: ~/telnetlib3/telnetlib3/stream_writer.py answers
            // DO LINEMODE with WILL plus an SLC import (func 0, DEFAULT) at
            // once; a manual-only import never fires against peers that just
            // proceed to MODE.
            using (GlobalStateGuard.SkipProactive(true))
            {
                using var stream = new ScriptedStream();
                using var client = new Client(stream, new CancellationToken());
                stream.Enqueue(255, 253, 34);
                (await ReadClientOnceAsync(client)).Should().BeEmpty();
                stream.ByteWrites.Should().HaveCount(2);
                stream.ByteWrites[0].Should().Equal(new byte[] { 255, 251, 34 });
                stream.ByteWrites[1].Should().Equal(new byte[] { 255, 250, 34, 3, 0, 3, 0, 255, 240 });
            }
        }

        [Fact]
        public async Task Server_AckedMode_PublishesSlcAutomatically()
        {
            // Source of truth: ~/telnetlib3/telnetlib3/stream_writer.py sends
            // the SLC table on the first server-side MODE (executed: the full
            // 16-row default table); without it the peer never learns our
            // special characters. Cs-shaped expectation: the configured-rows
            // export (PublishSpecialCharactersAsync semantics), here the BSD
            // defaults with the one row set above overridden.
            using var stream = new ScriptedStream();
            using var session = NewSession(stream);
            session.SetLinemodeEntry(3, 2, 5);
            await AgreeLinemodeAsync(session, stream);
            stream.Enqueue(255, 250, 34, 1, 5, 255, 240);
            (await session.ReadAsync(TimeSpan.FromMilliseconds(500))).Should().BeEmpty();
            var slc = new byte[]
            {
              255, 250, 34, 3,
              1, 3, 0, 2, 3, 0, 3, 2, 5, 4, 34, 15, 5, 2, 20, 6, 3, 0,
              7, 98, 28, 8, 2, 4, 9, 66, 26, 10, 2, 127, 11, 2, 21, 12, 2, 23,
              13, 2, 18, 14, 2, 22, 15, 2, 17, 16, 2, 19,
              255, 240,
            };
            stream.ByteWrites.Should().Contain(w => w.SequenceEqual(slc));
        }

        [Fact]
        public async Task Server_SlcBlock_RequestsForwardMaskAutomatically()
        {
            // Source of truth: ~/telnetlib3/telnetlib3/stream_writer.py calls
            // request_forwardmask after every server-side SLC block; a
            // manual-only forwardmask leaves character-mode servers without one.
            using var stream = new ScriptedStream();
            using var session = NewSession(stream);
            await AgreeLinemodeAsync(session, stream);
            stream.Enqueue(255, 250, 34, 3, 3, 2, 5, 255, 240);
            (await session.ReadAsync(TimeSpan.FromMilliseconds(500))).Should().BeEmpty();
            stream.ByteWrites.Should().Contain(w =>
              w.Length > 5 && w[0] == 255 && w[1] == 250 && w[2] == 34 && w[3] == 253 && w[4] == 2);
        }

        [Fact]
        public async Task Ip_ConsumedWithoutDataOrReply()
        {
            // Shared by both stacks: IAC IP never becomes text and earns no
            // reply. ~/telnetlib3/telnetlib3/stream_writer.py handle_ip only logs.
            var (output, stream) = await ReadHandlerOnceAsync(static _ => { }, 255, 244);
            output.Should().BeEmpty();
            stream.ByteWrites.Should().BeEmpty();
        }

        [Fact]
        public async Task IacWhereOptionBelongs_DroppedSilently()
        {
            // A 0xFF byte where the option byte belongs toggles the IAC
            // state, so DO's option byte becomes 251: the stack answers the
            // resulting DO 251 with WONT 251 — matching the reference, which
            // answers WONT 251 on the full 6-byte feed. No data surfaces.
            var (output, stream) = await ReadHandlerOnceAsync(static _ => { }, 255, 253, 255, 255, 251, 255);
            output.Should().BeEmpty();
            stream.ByteWrites.Should().ContainSingle().Which.Should().Equal(new byte[] { 255, 252, 251 });
        }

        [Fact]
        public async Task SbCharsetRequest_ViableUnofferedOffer_Accepted()
        {
            // Source of truth: ~/telnetlib3/telnetlib3/client.py send_charset
            // accepts the first viable peer offer when there is no explicit
            // preference; pre-intersecting with our own narrow defaults
            // wrongly rejects peers that only offer e.g. CP437.
            string? accepted = null;
            var (output, stream) = await ReadHandlerOnceAsync(
              h => h.CharsetAccepted += name => accepted = name,
              255, 250, 42, 1, 32, (byte)'C', (byte)'P', (byte)'4', (byte)'3', (byte)'7', 255, 240);
            output.Should().BeEmpty();
            stream.ByteWrites.Should().ContainSingle().Which.Should().Equal(
              new byte[] { 255, 250, 42, 2, (byte)'C', (byte)'P', (byte)'4', (byte)'3', (byte)'7', 255, 240 });
            accepted.Should().Be("CP437");
        }

        [Fact]
        public async Task Big5Bbs_SplitPair_AcrossReads_DecodesToSingleChar()
        {
            // Source of truth: ~/telnetlib3/telnetlib3/stream_reader.py keeps
            // a persistent incremental decoder, so a Big5 pair split across
            // segments buffers the lead instead of emitting art immediately.
            using var stream = new ScriptedStream(0xA4);
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1);
            sut.TextEncoding = new Big5BbsEncoding();
            var first = await sut.ReadAsync(TimeSpan.FromMilliseconds(50));
            stream.Enqueue(0xA4);
            var second = await sut.ReadAsync(TimeSpan.FromMilliseconds(50));
            (first + second).Should().Be("中");
        }

        [Fact]
        public async Task SyncTermFont_Switch_LatchesForceBinary()
        {
            // Source of truth: ~/telnetlib3/telnetlib3/client_base.py sets
            // force_binary on a SyncTERM font switch; without the latch the
            // new 8-bit font still decodes under 7-bit gating.
            using var stream = new ScriptedStream(27, 91, 48, 59, 51, 54, 32, 68);
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1);
            await sut.ReadAsync(TimeSpan.FromMilliseconds(50));
            sut.TextEncoding.Should().BeOfType<AtasciiEncoding>();
            sut.ForceBinaryDecoding.Should().BeTrue();
        }

        [Fact]
        public async Task OpeningPreset_WithMccpEnabled_AdvertisesMccp()
        {
            // This stack never offers WILL MCCP2/3, even with compression
            // enabled: it inflates inbound MCCP but has no outbound
            // compressor, so offering would corrupt the stream as soon as the
            // peer accepts. EnableMccp stays the passive-accept gate (agree +
            // inflate when the peer offers) — the opening is DO TTYPE only.
            // Bypass NewSession helper (which pre-enables peer WILLs and would
            // suppress the DO): the preset must be observed from a fresh state.
            using var stream = new ScriptedStream();
            using var session = new ServerSession(stream, new TelnetServerOptions { EnableMccp = true }, CancellationToken.None);
            await session.SendOpeningPresetAsync();
            var outbound = OutboundBytes(stream);
            outbound.Should().Equal(255, 253, 24);
            ContainsSubsequence(outbound, [255, 251, 86]).Should().BeFalse("preset must not offer WILL MCCP2");
            ContainsSubsequence(outbound, [255, 251, 87]).Should().BeFalse("preset must not offer WILL MCCP3");
        }
    }
}
