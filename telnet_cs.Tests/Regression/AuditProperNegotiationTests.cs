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
    /// Audit §2 proper-behavior tests. F-N1–N4/N10 FAIL against current
    /// behavior; the F-N5–N9 pins PASS (ours is already RFC 1143-correct there
    /// and must stay that way).
    /// </summary>
    public class AuditProperNegotiationTests
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
            // F-N3: TM carries no Q-method state; every DO TM earns WILL TM.
            var (_, writes) = await ReadScriptedAsync(255, 253, 6, 255, 253, 6);
            writes.Should().Equal(255, 251, 6, 255, 251, 6);
        }

        [Fact]
        public async Task DoLogout_SendsNoNegotiationBytes()
        {
            // F-N4: DO LOGOUT closes via hook; no WONT goes on the wire.
            var (_, writes) = await ReadScriptedAsync(255, 253, 18);
            writes.Should().BeEmpty();
        }

        [Fact]
        public async Task StrayTtypeIs_IgnoredWithoutWont()
        {
            // F-N10: an unsolicited answer must not earn a state-bypassing WONT.
            var (_, writes) = await ReadScriptedAsync(255, 250, 24, 0, 120, 116, 101, 114, 109, 255, 240);
            writes.Should().BeEmpty();
        }

        [Fact]
        public async Task StrayNaws_AssumedEnabledWithoutWont()
        {
            // F-N10: an unnegotiated NAWS report is consumed and honored
            // (reference assume-enabled), never answered with WONT. The
            // dimensions land in the session collector (pinned passing by
            // NawsVerbFirst_Accepted); the framing layer itself stays silent.
            var (output, writes) = await ReadScriptedAsync(255, 250, 31, 0, 80, 0, 24, 255, 240);
            output.Should().BeEmpty();
            writes.Should().BeEmpty();
        }

        [Fact]
        public async Task StrayStatusIs_ParsedWithoutWont()
        {
            // F-N10: an inbound STATUS IS is consumed silently — never WONT —
            // and its items are recorded in the session collector. The record
            // pin follows with the store at implementation time; this pins
            // the wire half (silence) now.
            var (output, writes) = await ReadScriptedAsync(255, 250, 5, 0, 251, 3, 253, 3, 255, 240);
            output.Should().BeEmpty();
            writes.Should().BeEmpty();
        }

        [Fact]
        public async Task Server_DoTtype_RefusedWithWont()
        {
            // F-N2: a server never provides TTYPE; DO TTYPE earns WONT.
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
            // F-N2: a client never consumes NAWS; WILL NAWS earns DONT.
            using var stream = new ScriptedStream(255, 251, 31);
            using var client = new Client(stream, TimeSpan.FromMilliseconds(50), CancellationToken.None);
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
            // F-N2: a server never provides these; DO earns WONT, never WILL.
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
            // F-N2: a client never provides these; WILL earns DONT, never DO.
            // (NAWS pinned separately by Client_WillNaws_RefusedWithDont.)
            using var stream = new ScriptedStream(255, 251, option);
            using var client = new Client(stream, TimeSpan.FromMilliseconds(50), CancellationToken.None);
            await client.ReadAsync(TimeSpan.FromMilliseconds(300));
            byte[] writes = stream.ByteWrites.SelectMany(w => w).ToArray();
            ContainsFrame(writes, new byte[] { 255, 254, (byte)option }).Should().BeTrue();
            ContainsFrame(writes, new byte[] { 255, 253, (byte)option }).Should().BeFalse();
        }

        [Fact]
        public async Task Client_DoEcho_RefusedEvenWhenAllowed()
        {
            // F-N2: a client never echoes for the server; DO ECHO earns WONT
            // even with AllowRemoteEcho opted in.
            using var stream = new ScriptedStream();
            using var client = new Client(stream, new CancellationToken());
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
            // F-N2: a server never lets the peer echo; WILL ECHO earns no
            // reply and no state change (reference raises, caught, silent).
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
            // F-N1: after the peer WONTs BINARY, automatic reads must stay
            // silent until new explicit stimulus — no DO/WONT ping-pong.
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
            // F-N5 pin (ours correct): at most one wire request per negotiation.
            var state = new NegotiationState();
            state.RequestEnable(31).Should().Be(Commands.Do);
            state.RequestDisable(31).Should().BeNull("queued while outstanding");
            state.RequestEnable(31).Should().BeNull("second toggle cancels the queue");
        }

        [Fact]
        public void SimultaneousDisableThenWill_StaysDisabledSilent()
        {
            // F-N6 pin (ours correct): WANTNO EMPTY + WILL resolves to NO
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
            // F-N8 pin (ours correct): redundant WILL in YES is ignored.
            var state = new NegotiationState();
            state.ReceivedWill(31, agree: true).Should().Be(Commands.Do);
            state.ReceivedWill(31, agree: true).Should().BeNull();
        }

        [Fact]
        public void FreshDisable_Silent()
        {
            // F-N9 pin (ours correct): never initiate DONT/WONT for disabled.
            var state = new NegotiationState();
            state.OfferDisable(3).Should().BeNull();
            state.RequestDisable(3).Should().BeNull();
        }

        [Fact]
        public void RefusedWill_AnsweredEveryTime()
        {
            // F-N7 pin (ours correct): strict RFC answers every refusal.
            var state = new NegotiationState();
            state.ReceivedWill(7, agree: false).Should().Be(Commands.Dont);
            state.ReceivedWill(7, agree: false).Should().Be(Commands.Dont);
        }
    }
}
