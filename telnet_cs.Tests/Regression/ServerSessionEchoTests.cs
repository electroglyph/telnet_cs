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
    /// Game-driven ECHO tests: <c>SetEchoAsync</c> / <c>WriteWithEchoAsync</c>
    /// wire semantics through the RFC 1143 machine. Wire-exact over
    /// <c>ScriptedStream</c> + <c>ServerSession</c> + <c>ReadAsync</c>.
    /// </summary>
    public class ServerSessionEchoTests
    {
        private static ServerSession NewSession(ScriptedStream stream, TelnetServerOptions? options = null)
        {
            // Peer agreement for the collectors (state-only, no wire bytes).
            var session = new ServerSession(stream, options ?? new TelnetServerOptions(), CancellationToken.None);
            session.Negotiation.ReceivedWill((int)Options.TerminalType, agree: true);
            session.Negotiation.ReceivedWill((int)Options.TerminalSpeed, agree: true);
            session.Negotiation.ReceivedWill((int)Options.XDisplay, agree: true);
            session.Negotiation.ReceivedWill((int)Options.OldEnvironment, agree: true);
            session.Negotiation.ReceivedWill((int)Options.CharacterSet, agree: true);
            return session;
        }

        private static byte[] OutboundBytes(ScriptedStream stream)
        {
            return stream.ByteWrites.SelectMany(static b => b).ToArray();
        }

        private static int[] TtypeIsFrame(string value)
        {
            return [255, 250, 24, 0, .. Encoding.Latin1.GetBytes(value), 255, 240];
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
        public async Task SetEchoAsync_Suppress_SendsWillEcho()
        {
            // Bare session (no seeded peer WILLs): the confirmation read's
            // flush has no advanced preset to release, so the wire is echo-only.
            using var stream = new ScriptedStream();
            using var session = new ServerSession(stream, new TelnetServerOptions(), CancellationToken.None);
            await session.SetEchoAsync(true, CancellationToken.None);
            OutboundBytes(stream).Should().Equal(255, 251, 1);
            session.Negotiation[(int)Options.Echo].Us.Should().Be(NegotiationState.SideState.WantYes);

            // The peer's DO confirmation completes the offer with no reply
            // (the confirmation itself advances negotiation, so the same
            // flush also releases the advanced preset — pin echo, not that).
            stream.Enqueue(255, 253, 1);
            (await session.ReadAsync(TimeSpan.FromSeconds(5), CancellationToken.None)).Should().BeEmpty();
            session.Negotiation[(int)Options.Echo].Us.Should().Be(NegotiationState.SideState.Yes);
            ContainsSubsequence(OutboundBytes(stream), [255, 251, 1]).Should().BeTrue();
            ContainsSubsequence(OutboundBytes(stream), [255, 252, 1]).Should().BeFalse();
        }

        [Fact]
        public async Task SetEchoAsync_Unsuppress_AfterAgreedWill_SendsWontEcho()
        {
            using var stream = new ScriptedStream();
            using var session = new ServerSession(stream, new TelnetServerOptions(), CancellationToken.None);
            await session.SetEchoAsync(true, CancellationToken.None);
            stream.Enqueue(255, 253, 1);
            (await session.ReadAsync(TimeSpan.FromSeconds(5), CancellationToken.None)).Should().BeEmpty();
            session.Negotiation[(int)Options.Echo].Us.Should().Be(NegotiationState.SideState.Yes);

            await session.SetEchoAsync(false, CancellationToken.None);
            OutboundBytes(stream).TakeLast(3).Should().Equal(255, 252, 1);
            session.Negotiation[(int)Options.Echo].Us.Should().Be(NegotiationState.SideState.WantNo);
        }

        [Fact]
        public async Task SetEchoAsync_WontFromNo_SendsNothing()
        {
            // A bare WONT from No is a Q-method no-op: no bytes, no state move.
            using var stream = new ScriptedStream();
            using var session = new ServerSession(stream, new TelnetServerOptions(), CancellationToken.None);
            await session.SetEchoAsync(false, CancellationToken.None);
            OutboundBytes(stream).Should().BeEmpty();
            session.Negotiation[(int)Options.Echo].Us.Should().Be(NegotiationState.SideState.No);
        }

        [Fact]
        public async Task SetEchoAsync_WontWhileWillOutstanding_QueuesAndDrainsOnDo()
        {
            using var stream = new ScriptedStream();
            using var session = new ServerSession(stream, new TelnetServerOptions(), CancellationToken.None);
            await session.SetEchoAsync(true, CancellationToken.None);
            await session.SetEchoAsync(false, CancellationToken.None);
            // Queued behind the outstanding WILL: nothing further goes out yet.
            OutboundBytes(stream).Should().Equal(255, 251, 1);

            // The DO reply to the WILL drains the queued WONT.
            stream.Enqueue(255, 253, 1);
            (await session.ReadAsync(TimeSpan.FromSeconds(5), CancellationToken.None)).Should().BeEmpty();
            OutboundBytes(stream).Should().Equal(255, 251, 1, 255, 252, 1);
            session.Negotiation[(int)Options.Echo].Us.Should().Be(NegotiationState.SideState.WantNo);
        }

        [Fact]
        public async Task SetEchoAsync_DoubleWill_SendsOnce()
        {
            using var stream = new ScriptedStream();
            using var session = new ServerSession(stream, new TelnetServerOptions(), CancellationToken.None);
            await session.SetEchoAsync(true, CancellationToken.None);
            await session.SetEchoAsync(true, CancellationToken.None);
            OutboundBytes(stream).Should().Equal(255, 251, 1);
        }

        [Fact]
        public async Task SetEchoAsync_ManualWillWithOfferEchoFalse_CompletesHandshake()
        {
            // A confirming DO completes an outstanding WILL regardless of the
            // agree flag, so a manual WILL works with OfferEcho off and needs
            // no AllowRemoteEcho feed change (which would wrongly answer WONT).
            var options = new TelnetServerOptions { OfferEcho = false };
            using var stream = new ScriptedStream();
            using var session = new ServerSession(stream, options, CancellationToken.None);
            await session.SetEchoAsync(true, CancellationToken.None);
            stream.Enqueue(255, 253, 1);
            (await session.ReadAsync(TimeSpan.FromSeconds(5), CancellationToken.None)).Should().BeEmpty();
            session.Negotiation[(int)Options.Echo].Us.Should().Be(NegotiationState.SideState.Yes);
            ContainsSubsequence(OutboundBytes(stream), [255, 251, 1]).Should().BeTrue();
            ContainsSubsequence(OutboundBytes(stream), [255, 252, 1]).Should().BeFalse();
        }

        [Fact]
        public async Task WriteWithEchoAsync_MaskedPrompt_FusesWillBeforePrompt()
        {
            using var stream = new ScriptedStream();
            using var session = new ServerSession(stream, new TelnetServerOptions(), CancellationToken.None);
            byte[] promptBytes = Encoding.UTF8.GetBytes("Password: ");
            byte[] expected = [255, 251, 1, .. promptBytes];

            // Bombard the session with concurrent broadcast writes while the
            // fused write goes out: the toggle + prompt must land as one unit.
            using var cts = new CancellationTokenSource();
            Task[] writers = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    await session.WriteAsync("broadcast", CancellationToken.None).ConfigureAwait(false);
                }
            })).ToArray();
            try
            {
                await session.WriteWithEchoAsync("Password: ", suppress: true, CancellationToken.None);
            }
            finally
            {
                cts.Cancel();
                await Task.WhenAll(writers);
            }

            stream.ByteWrites.Should().ContainSingle(w => w.SequenceEqual(expected));
            session.Negotiation[(int)Options.Echo].Us.Should().Be(NegotiationState.SideState.WantYes);
        }

        [Theory]
        [InlineData("XTERM")]
        [InlineData("Mudlet")]
        public async Task FlushDeferred_ManualWontThenTtypeAnswer_SendsNoAutoWill(string term)
        {
            // A game-driven WONT stands down the deferred auto-offer: later
            // TTYPE answers arm it, but no WILL ECHO goes out — for MUD and
            // non-MUD fingerprints alike.
            using var stream = new ScriptedStream();
            stream.Enqueue([.. TtypeIsFrame(term), .. TtypeIsFrame(term)]);
            using var session = NewSession(stream);
            await session.SetEchoAsync(false, CancellationToken.None);
            (await session.RequestTerminalTypesAsync(TimeSpan.FromSeconds(5))).Should().Equal(term);
            ContainsSubsequence(OutboundBytes(stream), [255, 251, 1]).Should().BeFalse();
        }

        [Fact]
        public async Task SetEchoAsync_ManualWillToMudClient_SendsWill()
        {
            // The deferred offer stays withheld for a MUD client, but an
            // explicit manual WILL still goes out (caller intent wins).
            using var stream = new ScriptedStream();
            stream.Enqueue([.. TtypeIsFrame("Mudlet"), .. TtypeIsFrame("Mudlet")]);
            using var session = NewSession(stream);
            (await session.RequestTerminalTypesAsync(TimeSpan.FromSeconds(5))).Should().Equal("Mudlet");
            ContainsSubsequence(OutboundBytes(stream), [255, 251, 1]).Should().BeFalse();

            await session.SetEchoAsync(true, CancellationToken.None);
            ContainsSubsequence(OutboundBytes(stream), [255, 251, 1]).Should().BeTrue();
        }

        [Fact]
        public async Task SetEchoAsync_UnderDisableAllNegotiation_SendsNothing()
        {
            var options = new TelnetServerOptions { DisableAllNegotiation = true };
            using var stream = new ScriptedStream();
            using var session = new ServerSession(stream, options, CancellationToken.None);
            await session.SetEchoAsync(true, CancellationToken.None);
            stream.ByteWrites.Should().BeEmpty();
            stream.SingleByteWrites.Should().BeEmpty();
            stream.StringWrites.Should().BeEmpty();
            session.Negotiation[(int)Options.Echo].Us.Should().Be(NegotiationState.SideState.No);
        }

        [Fact]
        public async Task WriteWithEchoAsync_UnderDisableAllNegotiation_WritesPromptOnly()
        {
            var options = new TelnetServerOptions { DisableAllNegotiation = true };
            using var stream = new ScriptedStream();
            using var session = new ServerSession(stream, options, CancellationToken.None);
            await session.WriteWithEchoAsync("hi", suppress: true, CancellationToken.None);
            OutboundBytes(stream).Should().Equal((byte)'h', (byte)'i');
            ContainsSubsequence(OutboundBytes(stream), [255, 251, 1]).Should().BeFalse();
            ContainsSubsequence(OutboundBytes(stream), [255, 252, 1]).Should().BeFalse();
            session.Negotiation[(int)Options.Echo].Us.Should().Be(NegotiationState.SideState.No);
        }

        [Fact]
        public async Task RequestEnableAsync_UnderDisableAllNegotiation_SendsNothing()
        {
            // Centralization pin: the public Request* entry points honor the
            // switch before the Q-machine is touched (no bytes, no state).
            var options = new TelnetServerOptions { DisableAllNegotiation = true };
            using var stream = new ScriptedStream();
            using var session = new ServerSession(stream, options, CancellationToken.None);
            await session.RequestEnableAsync(Options.SuppressGoAhead, CancellationToken.None);
            await session.RequestDisableAsync(Options.SuppressGoAhead, CancellationToken.None);
            stream.ByteWrites.Should().BeEmpty();
            stream.SingleByteWrites.Should().BeEmpty();
            stream.StringWrites.Should().BeEmpty();
            session.Negotiation[(int)Options.SuppressGoAhead].Him.Should().Be(NegotiationState.SideState.No);
        }
    }
}
