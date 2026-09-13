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
            return new ServerSession(stream, options ?? new TelnetServerOptions(), CancellationToken.None);
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
            OutboundBytes(stream).Should().Equal(255, 250, 32, 1, 255, 240);
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
            // Source of truth: ~/telnetlib3/telnetlib3 answers environment
            // changes on whichever option was negotiated; gating INFO on
            // OLD-only drops updates for NEW-only sessions, leaving
            // LANG/COLUMNS/LINES stale there.
            using (GlobalStateGuard.SkipProactive(true))
            {
                using var stream = new ScriptedStream();
                using var client = new Client(stream, new CancellationToken());
                stream.Enqueue(255, 253, 39);
                (await ReadClientOnceAsync(client)).Should().BeEmpty();
                client.Settings.EnvironmentUser = "carol";
                (await ReadClientOnceAsync(client)).Should().BeEmpty();
                var expected = EnvironFrame(2, 39,
                  EnvironEntry(0, "USER", "carol"),
                  EnvironEntry(0, "TERM", "vt100"),
                  EnvironEntry(0, "LANG", "C"),
                  EnvironEntry(0, "COLUMNS", "80"),
                  EnvironEntry(0, "LINES", "24"));
                stream.ByteWrites.Should().Contain(w => w.SequenceEqual(expected));
            }
        }

        [Fact]
        public async Task EnvironInfo_ExplicitDisplay_IsVolunteered()
        {
            // Source of truth: ~/telnetlib3/telnetlib3/client.py withholds
            // DISPLAY unless configured; here Settings.EnvironmentDisplay
            // (null by default) is the explicit opt-in and then appears in INFO.
            using (GlobalStateGuard.SkipProactive(true))
            {
                using var stream = new ScriptedStream();
                using var client = new Client(stream, new CancellationToken());
                stream.Enqueue(255, 253, 36);
                (await ReadClientOnceAsync(client)).Should().BeEmpty();
                client.Settings.EnvironmentDisplay = "host:0";
                (await ReadClientOnceAsync(client)).Should().BeEmpty();
                var expected = EnvironFrame(2, 36,
                  EnvironEntry(0, "DISPLAY", "host:0"),
                  EnvironEntry(0, "TERM", "vt100"),
                  EnvironEntry(0, "LANG", "C"),
                  EnvironEntry(0, "COLUMNS", "80"),
                  EnvironEntry(0, "LINES", "24"));
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
            // for a SEND first.
            var (output, stream) = await ReadHandlerOnceAsync(static _ => { }, 255, 253, 5);
            output.Should().BeEmpty();
            var will = new byte[] { 255, 251, 5 };
            var snapshot = new byte[] { 255, 250, 5, 0, 251, 5, 255, 240 };
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
            // RP, LNEXT, XON, XOFF and AO negotiated values; the edit table
            // must know them or those rows can never trigger local editing.
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
            // the SLC table on the first ACKed MODE; without it the peer never
            // learns our special characters.
            using var stream = new ScriptedStream();
            using var session = NewSession(stream);
            session.SetLinemodeEntry(3, 2, 5);
            await AgreeLinemodeAsync(session, stream);
            stream.Enqueue(255, 250, 34, 1, 5, 255, 240);
            (await session.ReadAsync(TimeSpan.FromMilliseconds(500))).Should().BeEmpty();
            var slc = new byte[] { 255, 250, 34, 3, 3, 2, 5, 255, 240 };
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
            // A 0xFF byte where the option byte belongs is not a real option:
            // drop it with no reply and no data, for both negotiation verbs.
            var (output, stream) = await ReadHandlerOnceAsync(static _ => { }, 255, 253, 255, 255, 251, 255);
            output.Should().BeEmpty();
            stream.ByteWrites.Should().BeEmpty();
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
            // Source of truth: ~/telnetlib3/telnetlib3/server.py offers WILL
            // MCCP2 and WILL MCCP3 when compression is enabled (and no TLS);
            // passive-accept only means peers never learn we can compress.
            using var stream = new ScriptedStream();
            using var session = NewSession(stream, new TelnetServerOptions { EnableMccp = true });
            await session.SendOpeningPresetAsync();
            var outbound = OutboundBytes(stream);
            ContainsSubsequence(outbound, [255, 251, 86]).Should().BeTrue("preset should offer WILL MCCP2");
            ContainsSubsequence(outbound, [255, 251, 87]).Should().BeTrue("preset should offer WILL MCCP3");
        }
    }
}
