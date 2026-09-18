namespace telnet_cs.Tests
{
    using System;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using FluentAssertions;
    using Xunit;
    using telnet_cs.Protocol;
    using telnet_cs.Server;

    public class ServerSessionBehaviorTests
    {
        private const int Iac = 255;
        private const int Sb = 250;
        private const int Se = 240;

        private static ServerSession NewSession(ScriptedStream stream)
        {
            var session = new ServerSession(stream, new TelnetServerOptions(), CancellationToken.None);
            _ = session.Negotiation.ReceivedWill((int)Options.TerminalType, agree: true);
            _ = session.Negotiation.ReceivedWill((int)Options.TerminalSpeed, agree: true);
            _ = session.Negotiation.ReceivedWill((int)Options.XDisplay, agree: true);
            _ = session.Negotiation.ReceivedWill((int)Options.OldEnvironment, agree: true);
            _ = session.Negotiation.ReceivedWill((int)Options.NewEnvironment, agree: true);
            _ = session.Negotiation.ReceivedWill((int)Options.CharacterSet, agree: true);
            return session;
        }

        [Fact]
        public async Task TerminatedRead_TruncatesAtFirstTerminator()
        {
            // Framing parity with the client reader and telnetlib3's
            // readuntil (which consumes through the separator): pipelined
            // "hi\nrest" must yield "hi\n", not swallow the next line into
            // this one (which would desync credential reads).
            using var stream = new ScriptedStream("hi\nrest");
            using var session = NewSession(stream);
            (await session.TerminatedReadAsync("\n", TimeSpan.FromSeconds(2))).Should().Be("hi\n");
        }

        [Fact]
        public async Task TerminatedRead_StashesRemainderForNextRead()
        {
            // The tail past the terminator stays buffered for the next read —
            // but a tail with no terminator of its own still ends in
            // TimeoutException rather than a partial return.
            using var stream = new ScriptedStream("hi\nrest");
            using var session = NewSession(stream);
            (await session.TerminatedReadAsync("\n", TimeSpan.FromSeconds(2))).Should().Be("hi\n");
            Func<Task> act = () => session.TerminatedReadAsync("\n", TimeSpan.FromMilliseconds(200));
            await act.Should().ThrowAsync<TimeoutException>();
        }

        [Fact]
        public async Task WriteBytes_UpdatesSentAccounting()
        {
            // Accounting must cover every send path: the string path notes
            // the write on the session context, so the byte path must too,
            // or binary-heavy sessions undercount and look idle.
            using var stream = new ScriptedStream();
            using var session = NewSession(stream);
            await session.WriteAsync(new byte[] { 65, 66, 67 });
            session.Context.CharsSent.Should().BeGreaterThan(0);
        }

        [Fact]
        public async Task OldEnvironRequest_IgnoresNewVariantIs()
        {
            // Option 39 is NEW-ENVIRON (RFC 1572) while the waiter asked on
            // OLD-ENVIRON, option 36 (RFC 1408); verb 0 is IS in both. The
            // variants are tracked separately, so the OLD wait still times
            // out empty — but the unsolicited NEW frame is now stored in
            // ClientNewEnvironment (reference delivers IS even unsolicited).
            using var stream = new ScriptedStream(Iac, Sb, 39, 0, 0, 65, 1, 98, Iac, Se);
            using var session = NewSession(stream);
            (await session.RequestEnvironmentAsync(TimeSpan.FromMilliseconds(400))).Should().BeEmpty();
            session.ClientNewEnvironment.Should().ContainSingle(kv => kv.Key == "A" && kv.Value == "b");
        }

        [Fact]
        public async Task CharsetAccepted_EmptyName_IsNotLatched()
        {
            // RFC 2066 section 2: ACCEPTED carries <Charset> "identical to
            // one of the character sets in the REQUEST" — an empty name
            // matches nothing and would poison later decoding, so the
            // outcome must be the rejection path (null), not "".
            using var stream = new ScriptedStream(Iac, Sb, 42, 2, Iac, Se);
            using var session = NewSession(stream);
            (await session.RequestCharsetAsync(TimeSpan.FromSeconds(2))).Should().BeNull();
            session.ClientCharset.Should().BeNull();
        }

        [Fact]
        public async Task XDisplayRequest_WhileOutstanding_DoesNotSendTwice()
        {
            // RFC 1096 defines one SEND answered by one IS ("may not be
            // sent spontaneously, but only in response"), and telnetlib3's
            // request_xdisploc refuses to resend while a request is
            // outstanding. Overlapping calls must therefore share the one
            // in-flight SEND; this does not forbid a retry after the first
            // request completes or times out.
            using var stream = new ScriptedStream();
            using var session = NewSession(stream);
            var first = session.RequestXDisplayAsync(TimeSpan.FromMilliseconds(500));
            await Task.Delay(100);
            var second = session.RequestXDisplayAsync(TimeSpan.FromMilliseconds(300));
            await Task.WhenAll(first, second);
            // Request requires prior WILL XDISPLOC (enabled in NewSession helper);
            // the agreement also arms the advanced preset and probes, so count
            // only the XDISPLOC SEND frames for the single-active rule.
            stream.ByteWrites.Where(w => w.SequenceEqual(new byte[] { 255, 250, 35, 1, 255, 240 })).Should().HaveCount(1);
        }

        [Fact]
        public void SessionTimeout_SnapshotWithPerSessionOverride()
        {
            // Reassessment: the session documents snapshot semantics
            // ("initialised from IdleTimeout; SetTimeout overrides it per
            // session"), and server-level mutations apply to subsequently
            // accepted sessions only. Live-follow would couple sessions
            // sharing one options instance and fight SetTimeout, so the
            // documented contract is pinned instead.
            using var stream = new ScriptedStream();
            var options = new TelnetServerOptions { IdleTimeout = TimeSpan.FromMinutes(7) };
            using var session = new ServerSession(stream, options, CancellationToken.None);
            session.Timeout.Should().Be(TimeSpan.FromMinutes(7));
            session.Timeout = TimeSpan.FromMinutes(9);
            session.Timeout.Should().Be(TimeSpan.FromMinutes(9));
        }

        [Fact]
        public void ServerStop_ClearsBoundPort()
        {
            // Repo-design invariant (not a .NET standard — TcpListener
            // exposes no port): Port reads 0 before Start via the
            // OS-assigned-port discovery doc, so it must read 0 again once
            // listening stops rather than report a stale bound port.
            // Keeping this requires clearing in Stop/Dispose and re-setting
            // in Start, plus a doc line.
            using var server = new TelnetServer(0);
            server.Start();
            server.Port.Should().BeGreaterThan(0);
            server.Stop();
            server.Port.Should().Be(0);
        }
    }
}
