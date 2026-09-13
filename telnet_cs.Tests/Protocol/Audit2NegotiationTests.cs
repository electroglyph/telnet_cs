namespace telnet_cs.Tests
{
    using System;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using FluentAssertions;
    using Xunit;
    using telnet_cs.Client;
    using telnet_cs.IO;
    using telnet_cs.Protocol;

    /// <summary>
    /// Round-2 audit (§2) pins: each test asserts the telnetlib3 negotiation
    /// behavior, so every test here fails against the current code.
    /// </summary>
    public class Audit2NegotiationTests
    {
        private static async Task<string> ReadClientOnceAsync(Client client)
        {
            return await client.ReadAsync(TimeSpan.FromMilliseconds(100));
        }

        private static async Task<(string Output, byte[] Writes)> ReadScriptedAsync(params int[] reads)
        {
            using var stream = new ScriptedStream(reads);
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1);
            var output = await sut.ReadAsync(TimeSpan.FromMilliseconds(50));
            return (output, stream.ByteWrites.SelectMany(w => w).ToArray());
        }

        private static async Task<(string Output, ScriptedStream Stream)> ReadConfiguredAsync(Action<ByteStreamHandler> configure, params int[] reads)
        {
            var stream = new ScriptedStream(reads);
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1);
            configure(sut);
            var output = await sut.ReadAsync(TimeSpan.FromMilliseconds(50));
            return (output, stream);
        }

        private static byte[] OutboundBytes(ScriptedStream stream)
        {
            return stream.ByteWrites.SelectMany(w => w).ToArray();
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

        [Fact]
        public async Task DontWhileEnabled_SendsNoReply()
        {
            // audit2 §2 F2-DONT-WONT.
            // Reference: stream_writer.py:2129-2144, esp. :2140-2143 ("a
            // DONT can not be declined ... no need to affirm"); :858-863
            // (DONT only clears state).
            // Repro (client writer): feed DO SGA then DONT SGA
            // (FF FD 03 FF FE 03) -> inband=b'' sent=FF FB 03 (only the
            // first WILL; the DONT is silent, local[SGA]=False).
            var (output, writes) = await ReadScriptedAsync(255, 253, 3, 255, 254, 3);
            output.Should().BeEmpty();
            writes.Should().Equal(255, 251, 3);
        }

        [Fact]
        public async Task WontWhileEnabled_SendsNoReply()
        {
            // audit2 §2 F2-DONT-WONT, symmetric.
            // Reference: stream_writer.py:2325-2349, esp. :2333-2334 and
            // :2348-2349 ("not possible to decline WONT": only
            // remote[opt]=False, no send).
            // Repro (client writer): feed WILL SGA then WONT SGA
            // (FF FB 03 FF FC 03) -> inband=b'' sent=FF FD 03 (only the
            // first DO).
            var (output, writes) = await ReadScriptedAsync(255, 251, 3, 255, 252, 3);
            output.Should().BeEmpty();
            writes.Should().Equal(255, 253, 3);
        }

        [Fact]
        public async Task ClientWillLogout_SendsNoReply()
        {
            // audit2 §2 F2-LOGOUT-CLIENT.
            // Reference: stream_writer.py:2253-2256 (client WILL LOGOUT
            // raises ValueError), :885-888 (negotiation toggle), _base.py
            // :75-78 (except ValueError -> debug, swallowed).
            // Repro (client writer): direct feed_byte FF FB 12 raises
            // ValueError "cannot recv WILL LOGOUT on client end",
            // sent=b''; via _process_data_chunk -> inband=b'' sent=b''.
            // Contrast: server FF FB 12 -> sent=FF FE 12 (:1973-1976).
            var (output, writes) = await ReadScriptedAsync(255, 251, 18);
            output.Should().BeEmpty();
            writes.Should().BeEmpty();
        }

        [Fact]
        public async Task ClientDoLogout_DoesNotClose()
        {
            // audit2 §2 F2-LOGOUT-CLIENT.
            // Reference: stream_writer.py:2036-2037 (client DO LOGOUT
            // raises ValueError, ignored — bypasses :2048-2049/:1968-1970
            // close()), _base.py:77-78 (swallow).
            // Repro (client writer): feed FF FD 12 -> direct raises
            // ValueError, sent=b''; chunk path -> inband=b'' sent=b''
            // transport_closing=False.
            using (GlobalStateGuard.SkipProactive(true))
            {
                using var stream = new ScriptedStream();
                using var client = new Client(stream, new CancellationToken());
                stream.Enqueue(255, 253, 18);
                (await ReadClientOnceAsync(client)).Should().BeEmpty();
                stream.Connected.Should().BeTrue();
                stream.ByteWrites.SelectMany(w => w).ToArray().Should().BeEmpty();
            }
        }

        [Fact]
        public async Task RepeatWillUnknown_SendsSingleRefusal()
        {
            // audit2 §2 F2-UNKNOWN-RESEND.
            // Reference: stream_writer.py:2315-2320 (else branch: iac(DONT)
            // + rejected_will), :1089-1096 (DONT: first sets remote=False
            // and sends; repeat with opt in remote and not enabled ->
            // return False).
            // Repro (client writer): feed FF FB 07 FF FB 07 (opt 7
            // unknown) step-by-step — after 2nd byte sent=FF FE 07, after
            // 5th byte sent unchanged FF FE 07; rejected_will={0x07}.
            var (output, writes) = await ReadScriptedAsync(255, 251, 7, 255, 251, 7);
            output.Should().BeEmpty();
            writes.Should().Equal(255, 254, 7);
        }

        [Fact]
        public void DisableWhileEnableOutstanding_SendsDontImmediately()
        {
            // audit2 §2 2.7 race.
            // Reference: stream_writer.py:1052-1103, esp. :1073-1079
            // (pending-gate covers only DO/WILL), :1089-1099 (DONT/WONT
            // never pending-gated — only remote==False skips).
            // Repro (client writer): iac(DO,SGA) -> True sent=FF FD 03
            // pending={FD 03}; iac(DONT,SGA) -> True sent=FF FD 03 FF FE 03
            // (immediate second send).
            var negotiation = new NegotiationState();
            negotiation.RequestEnable(3).Should().Be(Commands.Do);
            negotiation.RequestDisable(3).Should().Be(Commands.Dont);
        }

        [Fact]
        public async Task ServerWillNaws_SendsDoWithoutSbVolunteer()
        {
            // audit2 §2 F2-NAWS.
            // Reference: stream_writer.py:2229-2234 (WILL NAWS -> iac(DO)
            // + remote=True + pending[SB+NAWS], no send) vs :2100-2101
            // (DO NAWS -> _send_naws); :2627-2647 (_send_naws def).
            // Repro (server writer): feed FF FB 1F -> inband=b''
            // sent=FF FD 1F exactly, pending[SB+NAWS]=True but no SB bytes.
            // Contrast client FF FB 1F -> FF FE 1F (:2202-2206).
            var (output, stream) = await ReadConfiguredAsync(static s => { s.IsServerRole = true; }, 255, 251, 31);
            output.Should().BeEmpty();
            OutboundBytes(stream).Should().Equal(255, 253, 31);
        }

        [Theory]
        [InlineData(24)] // TTYPE
        [InlineData(32)] // TSPEED
        [InlineData(35)] // XDISPLOC
        [InlineData(39)] // NEW_ENVIRON
        [InlineData(33)] // LFLOW
        public async Task ClientWill_ServerOnlyOption_IsRefused(int option)
        {
            // audit2 §2 F2-CLIENT-WILL.
            // Reference: stream_writer.py:2264-2277, esp. :2273-2277 (non-
            // server client DONTs every WILL except CHARSET:
            // XDISPLOC/TTYPE/TSPEED/NEW_ENVIRON/LFLOW); telopt.py:15-18
            // (TTYPE=0x18=24, TSPEED=0x20=32, LFLOW=0x21=33, XDISPLOC=0x23
            // =35, NEW_ENVIRON=0x27=39).
            // Repro (client writer): FF FB 18->FF FE 18, FF FB 20->FF FE 20,
            // FF FB 23->FF FE 23, FF FB 27->FF FE 27, FF FB 21->FF FE 21;
            // each inband=b''.
            var (output, writes) = await ReadScriptedAsync(255, 251, option);
            output.Should().BeEmpty();
            writes.Should().Equal(255, 254, (byte)option);
        }

        [Fact]
        public async Task ClientWillComport_SendsSignatureProbe()
        {
            // audit2 §2 F2-COMPORT-PROBE.
            // Reference: stream_writer.py:2238-2239 (COM_PORT + client ->
            // request_comport_signature()), :1217-1234 esp. :1224 (gate on
            // remote[COM_PORT]) + :1231 ([IAC,SB,COM_PORT,0x00,IAC,SE]).
            // Repro (client writer): feed FF FB 2C -> sent=FF FD 2C FF FA
            // 2C 00 FF F0. Server contrast: DO only (FF FD 2C).
            var (output, stream) = await ReadConfiguredAsync(static _ => { }, 255, 251, 44);
            output.Should().BeEmpty();
            ContainsSubsequence(OutboundBytes(stream), new byte[] { 255, 250, 44, 0, 255, 240 }).Should().BeTrue();
        }

        [Fact]
        public async Task ClientWillMccp3_SendsStartSb()
        {
            // audit2 §2/§8 F2-MCCP3-START.
            // Reference: stream_writer.py:2240-2245 esp. :2243 (send
            // IAC SB MCCP3 IAC SE) + :2244 (mccp3_active=True), gated by
            // :2225-2228 (compression False -> DONT).
            // Repro (client writer, default compression=None ~ EnableMccp):
            // feed FF FB 57 -> sent=FF FD 57 FF FA 57 FF F0,
            // mccp3_active=True. With compression=False -> FF FE 57.
            var (output, stream) = await ReadConfiguredAsync(static s => { s.EnableMccp = true; }, 255, 251, 87);
            output.Should().BeEmpty();
            ContainsSubsequence(OutboundBytes(stream), new byte[] { 255, 250, 87, 255, 240 }).Should().BeTrue();
        }
    }
}
