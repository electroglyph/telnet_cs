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
    using telnet_cs.Server;
    using telnet_cs.Transport;

    /// <summary>
    /// Negotiation behavior tests (RFC 1143 Q-method, TIMING-MARK, LOGOUT,
    /// stray subnegotiation, and role-directional refusals).
    /// </summary>
    public class Rfc1143NegotiationTests
    {
        private static async Task<(string Output, byte[] Writes)> ReadScriptedAsync(params int[] reads)
        {
            using var stream = new ScriptedStream(reads);
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1);
            var output = await sut.ReadAsync(TimeSpan.FromMilliseconds(50));
            return (output, stream.ByteWrites.SelectMany(w => w).ToArray());
        }

        private static int CountOccurrences(byte[] writes, byte[] frame)
        {
            int count = 0;
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
                    count++;
                }
            }

            return count;
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
        public async Task TimingMark_AnsweredStatelessly()
        {
            // Source of truth: TIMING-MARK (option 6, RFC 860) is a stateless ping.
            // ~/telnetlib3/telnetlib3/stream_writer.py handle_do answers every DO TM
            // with WILL TM ("No state is stored"), and iac() bypasses the
            // local-option/pending duplicate suppressions for TM, so two inbound
            // DO TM frames earn two WILL TM frames. An unsolicited WILL TM is ignored.
            // Our code: telnet_cs/IO/ByteStreamHandler.cs routes DO TM through the
            // generic Q-method (NegotiationState.cs ReceivedDo), so the first DO in
            // state NO answers WILL and moves to YES, while the second in YES returns
            // null and stays silent.
            // Proof: wire [255,253,6, 255,253,6] must produce [255,251,6, 255,251,6];
            // producing only one WILL proves Q-method state was applied where the
            // reference keeps none. This test is correct.
            var (_, writes) = await ReadScriptedAsync(255, 253, 6, 255, 253, 6);
            writes.Should().Equal(255, 251, 6, 255, 251, 6);
        }

        [Fact]
        public async Task DoLogout_SendsNoNegotiationBytes()
        {
            // Source of truth: telnetlib3 closes on DO LOGOUT with zero negotiation
            // bytes. ~/telnetlib3/telnetlib3/stream_writer.py handle_do for LOGOUT (18)
            // invokes the logout callback which closes the transport; it never emits
            // WILL/WONT. WILL LOGOUT earns DONT; DONT stays a silent callback.
            // Our code: telnet_cs/IO/ByteStreamHandler.cs WeAgree omits LOGOUT so the
            // Q-layer returns WONT, and the handler still emits FF FC 12 before firing
            // LogoutRequested, adding a negotiation frame the reference never sends.
            // Proof: wire [255,253,18] must produce no bytes; observing FF FC 12 proves
            // the extra refusal. The close-hook half is separate; this pins the wire
            // half. This test is correct.
            var (_, writes) = await ReadScriptedAsync(255, 253, 18);
            writes.Should().BeEmpty();
        }

        [Fact]
        public async Task StrayTtypeIs_IgnoredWithoutWont()
        {
            // Source of truth: an unsolicited TTYPE IS is consumed silently.
            // ~/telnetlib3/telnetlib3/stream_writer.py _handle_sb_ttype logs and returns
            // on IS when no SEND is outstanding; it never synthesizes WONT, which would
            // bypass NegotiationState and desync wire from machine. RFC 1091 defines IS
            // only as an answer to SEND; refusal belongs to WILL/WONT/DO/DONT, not to
            // subnegotiation payloads.
            // Our code: telnet_cs/IO/ByteStreamHandler.cs stray-SEND gate sends WONT
            // when payload[0] != 1 without updating negotiation state, so SB TTYPE IS
            // ("xterm") earns FF FC 24 here.
            // Proof: wire FF FA 18 00 "xterm" FF F0 must produce no bytes; observing
            // FF FC 18 proves the synthetic refusal. This test is correct.
            var (_, writes) = await ReadScriptedAsync(255, 250, 24, 0, 120, 116, 101, 114, 109, 255, 240);
            writes.Should().BeEmpty();
        }

        [Fact]
        public async Task StrayNaws_AssumedEnabledWithoutWont()
        {
            // Source of truth: an unnegotiated NAWS report is honored, not refused.
            // ~/telnetlib3/telnetlib3/stream_writer.py _handle_sb_naws logs
            // "assuming NAWS-enabled", sets remote NAWS true, unpacks "!HH" columns
            // and rows, and fires the callback with no WONT. RFC 1073 defines the
            // 4-byte payload IAC SB NAWS W(2) H(2) IAC SE with no verb byte.
            // Our handler gate WONTs any SB whose payload[0] != SEND, while
            // ServerSession.Collectors.cs TryConsumeNaws accepts the strict
            // RFC 1073 4-byte shape; the framing layer must stop
            // refusing what the session would accept. Dimensions land in the session
            // collector, so the handler half stays silent by design.
            // Proof: wire FF FA 1F 00 50 00 18 FF F0 must yield empty output and empty
            // writes; observing FF FC 1F proves the spurious refusal. This test is
            // correct.
            var (output, writes) = await ReadScriptedAsync(255, 250, 31, 0, 80, 0, 24, 255, 240);
            output.Should().BeEmpty();
            writes.Should().BeEmpty();
        }

        [Fact]
        public async Task StrayStatusIs_ParsedWithoutWont()
        {
            // Source of truth: inbound STATUS IS is informational and never earns WONT.
            // ~/telnetlib3/telnetlib3/stream_writer.py _receive_status parses the WILL/
            // WONT/DO/DONT items for logging only; it never calls iac() and never
            // touches negotiation state. RFC 859 defines SEND/IS as the status verbs;
            // a peer status report is not a negotiation event. The parsed per-option
            // states belong in a session collector (same shape as ClientWindowSize),
            // so this pins the wire half (silence) now and the record store follows
            // with the implementation.
            // Our code hits the same stray-SEND gate as TTYPE/NAWS and emits
            // FF FC 05 for an IS payload.
            // Proof: wire FF FA 05 00 FB 03 FD 03 FF F0 must yield empty output and
            // empty writes; observing FF FC 05 proves the spurious refusal. This test
            // is correct.
            var (output, writes) = await ReadScriptedAsync(255, 250, 5, 0, 251, 3, 253, 3, 255, 240);
            output.Should().BeEmpty();
            writes.Should().BeEmpty();
        }

        [Fact]
        public async Task Server_DoTtype_RefusedWithWont()
        {
            // Source of truth: a server never provides TTYPE. Option numbers per
            // telnet_cs/Protocol/Options.cs and ~/telnetlib3/telnetlib3/telopt.py:
            // TTYPE=24 (0x18). ~/telnetlib3/telnetlib3/stream_writer.py handle_do
            // refuses DO TTYPE/LINEMODE/NAWS/NEW_ENVIRON/XDISPLOC/LFLOW/TSPEED/SNDLOC
            // on the server role with WONT and records a directional refusal.
            // Our code: telnet_cs/IO/ByteStreamHandler.cs WeAgree agrees to TTYPE in
            // both roles, so a ServerSession answers WILL 24 and arms a responder.
            // Proof: feed a default ServerSession FF FD 18; the reference bytes are
            // FF FC 18 with no FF FB 18. Asserting ContainsFrame(WONT) plus
            // NotContains(WILL) fails only while the symmetric agree remains. This
            // test is correct.
            using var stream = new ScriptedStream(255, 253, 24);
            using var session = new ServerSession(stream, new TelnetServerOptions(), CancellationToken.None);
            await session.ReadAsync(TimeSpan.FromMilliseconds(500));
            byte[] writes = stream.ByteWrites.SelectMany(w => w).ToArray();
            ContainsFrame(writes, new byte[] { 255, 252, 24 }).Should().BeTrue();
            ContainsFrame(writes, new byte[] { 255, 251, 24 }).Should().BeFalse();
        }

        [Fact]
        public async Task Client_WillNaws_RefusedWithDont()
        {
            // Source of truth: a client never consumes NAWS (option 31). The client
            // is the sender of window size; a peer WILL NAWS is refused.
            // ~/telnetlib3/telnetlib3/stream_writer.py handle_will answers WILL
            // NAWS/LINEMODE/SNDLOC on the client role with DONT, never DO.
            // Our code agrees to NAWS in both roles (WeAgree includes WindowSize),
            // so a Client answers DO 31 and then treats peer SBs as stray.
            // Proof: feed a Client FF FB 1F; the reference bytes contain FF FE 1F.
            // Missing DONT proves the symmetric agree. This test is correct.
            using var stream = new ScriptedStream(255, 251, 31);
            using var client = await Client.CreateAsync(stream, TimeSpan.FromMilliseconds(50), CancellationToken.None);
            await client.ReadAsync(TimeSpan.FromMilliseconds(300));
            byte[] writes = stream.ByteWrites.SelectMany(w => w).ToArray();
            ContainsFrame(writes, new byte[] { 255, 254, 31 }).Should().BeTrue();
        }

        [Theory]
        [InlineData(31)] // NAWS
        [InlineData(34)] // LINEMODE
        [InlineData(39)] // NEW_ENVIRON
        [InlineData(35)] // XDISPLOC
        [InlineData(33)] // LFLOW
        [InlineData(32)] // TSPEED
        [InlineData(23)] // SNDLOC
        public async Task Server_DirectionalRefusals_RefusedWithWont(int option)
        {
            // Source of truth: a server never provides these seven options plus TTYPE
            // (pinned separately). Numbers per Options.cs / telopt.py: NAWS=31,
            // TSPEED=32, LFLOW=33, LINEMODE=34, XDISPLOC=35, SNDLOC=23, NEW_ENVIRON=39.
            // handle_do on the server role answers each DO with WONT, never WILL.
            // Our symmetric WeAgree answers WILL for all of them, then arms responders
            // and SB gates that the reference never arms.
            // Proof: feed a ServerSession FF FD <opt>; reference bytes contain
            // FF FC <opt> and never FF FB <opt>. Each InlineData case is independently
            // repro-able. These tests are correct.
            // (TTYPE pinned separately by Server_DoTtype_RefusedWithWont.)
            using var stream = new ScriptedStream(255, 253, option);
            using var session = new ServerSession(stream, new TelnetServerOptions(), CancellationToken.None);
            await session.ReadAsync(TimeSpan.FromMilliseconds(500));
            byte[] writes = stream.ByteWrites.SelectMany(w => w).ToArray();
            ContainsFrame(writes, new byte[] { 255, 252, (byte)option }).Should().BeTrue();
            ContainsFrame(writes, new byte[] { 255, 251, (byte)option }).Should().BeFalse();
        }

        [Theory]
        [InlineData(34)] // LINEMODE
        [InlineData(23)] // SNDLOC
        public async Task Client_DirectionalRefusals_RefusedWithDont(int option)
        {
            // Source of truth: a client never provides LINEMODE (34) or SNDLOC (23).
            // handle_will on the client role answers WILL with DONT, never DO
            // (NAWS pinned separately; TTYPE/TSPEED/XDISPLOC/ENV/LFLOW follow the same
            // client-decline rule except CHARSET which is bidirectional).
            // Our symmetric agree answers DO for both, then misroutes peer SBs.
            // Proof: feed a Client FF FB <opt>; reference bytes contain FF FE <opt>
            // and never FF FD <opt>. Both cases fail only while the agree stays
            // role-blind. These tests are correct.
            // (NAWS pinned separately by Client_WillNaws_RefusedWithDont.)
            using var stream = new ScriptedStream(255, 251, option);
            using var client = await Client.CreateAsync(stream, TimeSpan.FromMilliseconds(50), CancellationToken.None);
            await client.ReadAsync(TimeSpan.FromMilliseconds(300));
            byte[] writes = stream.ByteWrites.SelectMany(w => w).ToArray();
            ContainsFrame(writes, new byte[] { 255, 254, (byte)option }).Should().BeTrue();
            ContainsFrame(writes, new byte[] { 255, 253, (byte)option }).Should().BeFalse();
        }

        [Fact]
        public async Task Client_DoEcho_RefusedEvenWhenAllowed()
        {
            // Source of truth: a client never echoes for the server, even opted in.
            // handle_do checks ECHO on the client role first and answers WONT
            // unconditionally (a 4.4BSD fingerprint guard: answering WILL freezes some
            // servers). The generic ECHO entry that honors AllowRemoteEcho is only
            // reached for non-client roles.
            // Our code: ByteStreamHandler.cs AgreeEcho returns AllowRemoteEcho-gated true
            // for DO ECHO, so an opted-in client answers WILL 1.
            // Proof: ApplyOptions(AllowRemoteEcho=true) then feed FF FD 01; reference
            // bytes contain FF FC 01 and never FF FB 01. Asserting both directions
            // fails only while the opt-in overrides the role gate. This test is correct.
            using var stream = new ScriptedStream();
            using var client = await Client.CreateAsync(stream, TimeSpan.FromSeconds(30), new CancellationToken());
            client.ApplyOptions(new TelnetClientOptions { AllowRemoteEcho = true });
            stream.Enqueue(255, 253, 1);
            await client.ReadAsync(TimeSpan.FromMilliseconds(300));
            byte[] writes = stream.ByteWrites.SelectMany(w => w).ToArray();
            ContainsFrame(writes, new byte[] { 255, 252, 1 }).Should().BeTrue();
            ContainsFrame(writes, new byte[] { 255, 251, 1 }).Should().BeFalse();
        }

        [Fact]
        public async Task Server_WillEcho_SilentWithoutStateChange()
        {
            // Server-role WILL ECHO is benign (MUD clients send it): it is
            // swallowed without reply and without negotiation state change,
            // instead of tearing down the connection.
            using var stream = new ScriptedStream(255, 251, 1);
            using var session = new ServerSession(stream, new TelnetServerOptions(), CancellationToken.None);
            await session.ReadAsync(TimeSpan.FromMilliseconds(500));
            byte[] writes = stream.ByteWrites.SelectMany(w => w).ToArray();
            ContainsFrame(writes, new byte[] { 255, 253, 1 }).Should().BeFalse();
            session.Negotiation.IsEnabledByPeer(1).Should().BeFalse();
        }

        [Fact]
        public async Task BinaryRefusal_StopsAutoReRequest()
        {
            // Source of truth: RFC 1143 forbids automatically re-requesting after a
            // rejection ("MUST NOT automatically respond to the rejection of a request
            // by submitting a new request"). The reference shares this shape:
            // server.py re-sends DO BINARY only when no DO BINARY is pending, and
            // feed_byte keeps the pending entry after WONT, so the second automatic
            // attempt is suppressed. Explicit RequestEnableAsync remains valid new
            // stimulus and clears the refusal memory.
            // Our code: Protocol/NegotiationState.cs tracks WasRefusedByPeer/Us, but
            // ServerSession.Collectors.cs CheckEncodingAsync re-requests BINARY whenever
            // locally enabled and not peer-enabled, without consulting the flag, and it
            // runs on every ServerSession.ReadAsync.
            // Proof: opening preset sends WILL/DO BINARY once; after peer DO then WONT,
            // a further ReadAsync must stay silent, keeping CountOccurrences(DO BINARY)
            // at 1. Observing 2 proves the automatic path ignored refusal memory. This
            // test is correct; only automatic paths must be gated, explicit requests
            // still clear the flag.
            using var stream = new ScriptedStream();
            using var session = new ServerSession(stream, new TelnetServerOptions(), CancellationToken.None);
            await session.SendOpeningPresetAsync(CancellationToken.None);
            stream.Enqueue(255, 253, 0);
            await session.ReadAsync(TimeSpan.FromMilliseconds(500));
            stream.Enqueue(255, 252, 0);
            await session.ReadAsync(TimeSpan.FromMilliseconds(500));
            await session.ReadAsync(TimeSpan.FromMilliseconds(200));
            byte[] writes = stream.ByteWrites.SelectMany(w => w).ToArray();
            CountOccurrences(writes, new byte[] { 255, 253, 0 }).Should().Be(1);
        }

        [Fact]
        public void RapidToggle_SendsSingleFrame()
        {
            // Him-side disables go out immediately even with a DO outstanding
            // (reference iac() never gates DONT): the toggle sends DO then
            // DONT at once, and the re-enable queues silently behind the DONT.
            var state = new NegotiationState();
            state.RequestEnable(31).Should().Be(Commands.Do);
            state.RequestDisable(31).Should().Be(Commands.Dont);
            state.RequestEnable(31).Should().BeNull("re-enable queues behind the outstanding DONT");
        }

        [Fact]
        public void SimultaneousDisableThenWill_StaysDisabledSilent()
        {
            // WANTNO EMPTY + WILL resolves to NO
            // with no reply ("DONT answered by WILL").
            var state = new NegotiationState();
            state.ReceivedWill(1, agree: true).Should().Be(Commands.Do);
            state.RequestDisable(1).Should().Be(Commands.Dont);
            state.ReceivedWill(1, agree: true).Should().BeNull("DONT answered by WILL resolves to NO");
            state.IsEnabledByPeer(1).Should().BeFalse();
        }

        [Fact]
        public void DuplicateWill_NoReplyNoSideEffects()
        {
            // Redundant WILL in YES is ignored.
            var state = new NegotiationState();
            state.ReceivedWill(31, agree: true).Should().Be(Commands.Do);
            state.ReceivedWill(31, agree: true).Should().BeNull();
        }

        [Fact]
        public void FreshDisable_Silent()
        {
            // Never initiate DONT/WONT for disabled.
            var state = new NegotiationState();
            state.OfferDisable(3).Should().BeNull();
            state.RequestDisable(3).Should().BeNull();
        }

        [Fact]
        public void RefusedWill_AnsweredEveryTime()
        {
            // Every refused WILL is answered, including repeats: duplicate
            // refusals are idempotent, and a re-sent request still earns
            // its own reply.
            var state = new NegotiationState();
            state.ReceivedWill(7, agree: false).Should().Be(Commands.Dont);
            state.ReceivedWill(7, agree: false).Should().Be(Commands.Dont);
        }
    }
}
