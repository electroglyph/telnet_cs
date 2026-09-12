namespace telnet_cs.Tests
{
    using System;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using FluentAssertions;
    using Xunit;
    using telnet_cs.Client;
    using telnet_cs.Protocol;
    using telnet_cs.Server;
    using telnet_cs.Transport;

    /// <summary>
    /// Audit §6 proper-behavior tests. F-L1–L4/L7/L9/L10/L13/L16 FAIL
    /// against current behavior.
    /// </summary>
    public class AuditProperLifecycleTests
    {
        private static int[] Ascii(string text) => text.Select(c => (int)c).ToArray();

        [Fact]
        public async Task ClientGa_PeerOnlySga_StillSends()
        {
            // F-L1: GA is suppressed only by OUR WILL SGA (local), never by
            // the peer's WILL alone (reference send_ga).
            using var stream = new ScriptedStream(255, 251, 3);
            using var client = new Client(stream, TimeSpan.FromMilliseconds(50), CancellationToken.None);
            (await client.ReadAsync(TimeSpan.FromMilliseconds(300))).Should().BeEmpty();
            (await client.SendGaAsync(CancellationToken.None)).Should().BeTrue();
            byte[] writes = stream.ByteWrites.SelectMany(w => w).ToArray();
            writes.Should().Contain([255, 249]);
        }

        [Fact]
        public async Task WaitForNegotiation_Timeout_Throws()
        {
            // F-L2: the async waiter signals timeout with an exception
            // (reference sync wrapper raises TimeoutError), not silent false.
            using var stream = new ScriptedStream();
            using var client = new Client(stream, TimeSpan.FromMilliseconds(50), CancellationToken.None);
            Func<Task> act = () => client.WaitForNegotiationAsync(_ => false, TimeSpan.FromMilliseconds(200));
            await act.Should().ThrowAsync<TimeoutException>();
        }

        [Fact]
        public async Task WaitForNegotiation_Cancelled_Throws()
        {
            // F-L2: close/cancel surfaces as cancellation (reference
            // CancelledError), not silent false.
            using var stream = new ScriptedStream();
            using var client = new Client(stream, TimeSpan.FromMilliseconds(50), CancellationToken.None);
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            Func<Task> act = () => client.WaitForNegotiationAsync(_ => false, Timeout.InfiniteTimeSpan, cts.Token);
            await act.Should().ThrowAsync<OperationCanceledException>();
        }

        [Fact]
        public async Task TtypePoll_PreservesApplicationText()
        {
            // F-L3: bytes that arrive during a subnegotiation wait belong to
            // the application; the poll must not consume them.
            using var stream = new ScriptedStream(
                [.. Ascii("typed-line\r\n"), 255, 250, 24, 0, (byte)'x', 255, 240,
                 255, 250, 24, 0, (byte)'x', 255, 240]);
            using var session = new ServerSession(stream, new TelnetServerOptions(), CancellationToken.None);
            (await session.RequestTerminalTypesAsync(TimeSpan.FromSeconds(5))).Should().Equal("x");
            (await session.ReadAsync(TimeSpan.FromSeconds(2))).Should().Be("typed-line\r\n");
        }

        [Fact]
        public async Task IdleTimeout_IgnoresTransmitActivity()
        {
            // F-L4: idleness is receive-only (reference _last_received); a
            // server that only transmits still times out.
            using var stream = new ScriptedStream();
            using var session = new ServerSession(stream, new TelnetServerOptions(), CancellationToken.None);
            session.SetTimeout(TimeSpan.FromMilliseconds(300));
            for (int i = 0; i < 5; i++)
            {
                await session.WriteAsync("tick", CancellationToken.None);
                await Task.Delay(100);
            }

            session.IsIdleTimedOut.Should().BeTrue();
        }

        [Fact]
        public async Task SessionCounters_CountWireBytes()
        {
            // F-L9: session accounting is in wire bytes, not decoded chars.
            var options = new TelnetServerOptions { TextEncoding = System.Text.Encoding.UTF8 };
            using var stream = new ScriptedStream(195, 169);
            using var session = new ServerSession(stream, options, CancellationToken.None);
            (await session.ReadAsync(TimeSpan.FromMilliseconds(500))).Should().Be("é");
            session.Context.CharsReceived.Should().Be(2);
        }

        [Fact]
        public async Task Typescript_RecordsServerOutputOnly()
        {
            // F-L10: the typescript mirrors the reference file: server output
            // only, not the echoed-back client input.
            using var stream = new ScriptedStream(Ascii("reply"));
            using var session = new ServerSession(stream, new TelnetServerOptions(), CancellationToken.None);
            var transcript = new StringWriter();
            session.Context.Typescript = transcript;
            await session.WriteAsync("cmd", CancellationToken.None);
            (await session.ReadAsync(TimeSpan.FromMilliseconds(500))).Should().Be("reply");
            transcript.ToString().Should().Be("reply");
        }

        [Fact]
        public void ClientCtor_Default_SendsNothingOnConnect()
        {
            // F-L16: a default client sends zero bytes on connect, like the
            // reference (explicit options / opt-in chatter excepted).
            // Deliberately outside GlobalStateGuard.SkipProactive: this pins
            // the default, not the opt-out.
            using var stream = new ScriptedStream();
            using var client = new Client(stream, TimeSpan.FromMilliseconds(50), CancellationToken.None);
            stream.ByteWrites.Should().BeEmpty();
        }

        [Fact]
        public void LinemodeBuffer_TrapsSignalByDefault()
        {
            // F-L13: with trapsig on and no server SLC yet, ^C must trap to
            // IAC IP like the reference BSD_SLC_TAB defaults.
            var buffer = new LinemodeBuffer(trapSignal: true);
            buffer.Feed('\x03').Data.Should().Equal(255, 244);
        }

        [Fact]
        public async Task Repl_Quit_SaysGoodbyeAfterBlankLine()
        {
            // F-L7: quit ends the session the reference way — a blank line
            // after the read, then "Goodbye.", not "Bye.".
            using var stream = new ScriptedStream(Ascii("quit\n"));
            using var session = new ServerSession(stream, new TelnetServerOptions(), CancellationToken.None);
            await ServerShells.RunReplAsync(session, CancellationToken.None);
            string.Concat(stream.StringWrites).Should().Contain("tel:sh> \r\nGoodbye.");
        }

        [Fact]
        public async Task Repl_UnknownCommand_MatchesReferenceText()
        {
            // F-L7: unknown input reports "no such command." like the
            // reference shell.
            using var stream = new ScriptedStream(Ascii("bogus\nquit\n"));
            using var session = new ServerSession(stream, new TelnetServerOptions(), CancellationToken.None);
            await ServerShells.RunReplAsync(session, CancellationToken.None);
            string.Concat(stream.StringWrites).Should().Contain("no such command.");
        }
    }
}
