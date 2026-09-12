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
            // Source of truth: GA suppression is gated on local WILL SGA only.
            // RFC 858 section 5 ties GA suppression to our own SGA offer, and
            // stream_writer.py send_ga returns False only when local SGA is enabled;
            // a peer WILL SGA alone (remote, affecting our receive path) never blocks
            // our transmit GA.
            // Our code: Client/BaseClient.GaWaiters.cs suppresses when either
            // IsEnabledByUs(SGA) or IsEnabledByPeer(SGA) is true, so a peer-only WILL
            // wrongly blocks SendGaAsync.
            // Proof: after peer WILL SGA with no local WILL, SendGaAsync must return
            // true and emit FF F9; false/empty proves the over-broad gate. This test is
            // correct.
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
            // Source of truth: the blocking waiter signals timeout with an exception,
            // not silent false. The async reference wait_for returns True and never
            // False, while close cancels waiters (CancelledError); the sync wrapper
            // sync.py raises TimeoutError on timeout. This C# method bundles the
            // timeout role, so throwing TimeoutException matches the blocking
            // semantics.
            // Our code: Client/BaseClient.GaWaiters.cs returns false on timeout, polls
            // on a 50 ms slice, and consumes application data into PendingText while
            // waiting (the reference never reads; data stays in TelnetReader and the
            // waiter fires via _check_waiters).
            // Proof: waiting on an unsatisfiable predicate with 200 ms must throw
            // TimeoutException; returning false proves the silent contract. This test
            // is correct for the timeout half; wake-latency and no-consume are pinned
            // by the same fix.
            using var stream = new ScriptedStream();
            using var client = new Client(stream, TimeSpan.FromMilliseconds(50), CancellationToken.None);
            Func<Task> act = () => client.WaitForNegotiationAsync(_ => false, TimeSpan.FromMilliseconds(200));
            await act.Should().ThrowAsync<TimeoutException>();
        }

        [Fact]
        public async Task WaitForNegotiation_Cancelled_Throws()
        {
            // Source of truth: close/cancel surfaces as cancellation. The reference
            // cancels waiter futures on close (_cancel_waiters -> fut.cancel() ->
            // CancelledError); silent false matches neither async nor sync behavior.
            // Our code catches OperationCanceledException and returns false, hiding
            // the cancel.
            // Proof: waiting with an already-cancelled token must throw
            // OperationCanceledException; returning false proves the swallow. This
            // test is correct.
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
            // Source of truth: bytes arriving during a subnegotiation wait belong to
            // the application. In the reference, application bytes stay queued in the
            // reader (feed_data) while negotiation completes via writer callbacks;
            // request_ttype just sends SEND and never consumes the reader. The sibling
            // client waiter already preserves via PendingText re-queue; the server poll
            // does not.
            // Our code: ServerSession.Collectors.cs PollForResponseAsync discards the
            // return value of ReadAsync, so interleaved typing during
            // RequestTerminalTypesAsync is silently dropped (all Request*Async funnel
            // through it).
            // Proof: queue "typed-line\r\n" plus two TTYPE IS answers; the poll must
            // return the chain while the next ReadAsync still returns the typed line.
            // Empty there proves data loss. This test is correct; the fix is to
            // preserve like the client waiter.
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
            // Source of truth: idleness is receive-only. ~/telnetlib3/telnetlib3/_base.py
            // defines idle from _last_received, assigned only in data_received; writes
            // never touch it, and set_timeout never moves the clock. A server that only
            // transmits still times out.
            // Our code: Server/TelnetSessionContext.cs moves LastActivityUtc on both
            // NoteRead and NoteWritten, so periodic WriteAsync("tick") keeps a silent
            // peer from ever timing out.
            // Proof: 300 ms timeout with writes every 100 ms and no reads must still
            // report IsIdleTimedOut; false proves transmit wrongly feeds idleness. The
            // documented "no text read or written" line goes with the fix. This test is
            // correct.
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
            // Source of truth: session accounting is in raw wire bytes, negotiation
            // included. server_base.py counts len(data) on receipt and stream_writer.py
            // counts len(buf) on transmit; multibyte UTF-8 and IAC frames count as
            // wire bytes, not decoded chars.
            // Our code: Server/TelnetSessionContext.cs adds text.Length on read (chars)
            // and mixes text.Length with byteCount on write, so "é" (2 wire bytes C3 A9,
            // 1 char) under-reports and negotiation-heavy sessions under-report more.
            // Proof: reading C3 A9 as "é" must leave CharsReceived at 2; observing 1
            // proves char counting. Status rx/tx comparability follows from the same
            // fix. This test is correct.
            var options = new TelnetServerOptions { TextEncoding = System.Text.Encoding.UTF8 };
            using var stream = new ScriptedStream(195, 169);
            using var session = new ServerSession(stream, options, CancellationToken.None);
            (await session.ReadAsync(TimeSpan.FromMilliseconds(500))).Should().Be("é");
            session.Context.CharsReceived.Should().Be(2);
        }

        [Fact]
        public async Task Typescript_RecordsServerOutputOnly()
        {
            // Source of truth: the typescript records server output only.
            // _session_context.py documents typescript_file as "all server output is
            // appended", and client_shell.py writes only the server-to-client chunk;
            // stdin/client input never enters the file. Byte and string write paths
            // both count as output there.
            // Our code: TelnetSessionContext.cs records both NoteRead and string
            // NoteWritten while recording nothing on the byte[] write path, so the
            // transcript mixes directions and misses byte writes.
            // Proof: WriteAsync("cmd") plus ReadAsync("reply") must leave the
            // transcript as exactly "reply"; observing "cmdreply" proves input was
            // recorded. This test is correct; the fix records output only on every
            // write path.
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
            // Source of truth: a default client sends zero bytes on connect.
            // client_base.py begin_negotiation iterates always_will/always_do, both
            // empty by default, so no IAC goes out until explicit options are given.
            // Our code: Client/Client.Connect.cs fires ProactiveOptionNegotiation
            // (IAC DO SGA) unless SkipProactiveOptionNegotiation/skipProactive is set,
            // so every new Client emits FF FD 03 first.
            // Proof: constructing a default Client over an empty ScriptedStream must
            // leave ByteWrites empty; observing FF FD 03 proves the proactive chatter.
            // This pins the default posture; the opt-out stays pinned passing
            // elsewhere. Deliberately outside any global skip guard. This test is
            // correct.
            using var stream = new ScriptedStream();
            using var client = new Client(stream, TimeSpan.FromMilliseconds(50), CancellationToken.None);
            stream.ByteWrites.Should().BeEmpty();
        }

        [Fact]
        public void LinemodeBuffer_TrapsSignalByDefault()
        {
            // Source of truth: with trapsig on and no server SLC yet, ^C traps to
            // IAC IP. ~/telnetlib3/telnetlib3/slc.py BSD_SLC_TAB maps IP to 0x03 (plus
            // ABORT/SUSP/EOF/EW/AYT/EC/EL), and client_shell.py builds the trapsig map
            // from that full table, so Feed("\x03") yields IAC IP.
            // Our code: Client/LinemodeBuffer.cs DefaultSlc contains only EC/EL/EW, so
            // TryTrap never matches IP/ABORT/SUSP/EOF/BRK/AYT and buffers "\x03" as
            // data with echo instead of trapping.
            // Proof: new LinemodeBuffer(trapSignal:true).Feed('\x03').Data must equal
            // [255, 244] (IAC IP); buffered echo proves the subset default. This test
            // is correct; the fix defaults to the full reference table.
            var buffer = new LinemodeBuffer(trapSignal: true);
            buffer.Feed('\x03').Data.Should().Equal(255, 244);
        }

        [Fact]
        public async Task Repl_Quit_SaysGoodbyeAfterBlankLine()
        {
            // Source of truth: the reference shell ends quit with a blank line plus
            // "Goodbye.". server_shell.py writes the prompt, awaits a line, always
            // writes CRLF after the read, then on "quit" writes "Goodbye." + CRLF and
            // breaks.
            // Our code: Server/ServerShells.cs QuitAsync writes "Bye." with no
            // preceding blank line, so the prompt/read/quit stream differs
            // wire-visibly.
            // Proof: feeding "quit\n" must produce a transcript containing
            // "tel:sh> \r\nGoodbye."; observing "Bye." without the blank line proves
            // the text gap. This test is correct for the wire-visible text half; the
            // introspection command set stays an explicit leftover until toggling and
            // byte-dump surfaces exist.
            using var stream = new ScriptedStream(Ascii("quit\n"));
            using var session = new ServerSession(stream, new TelnetServerOptions(), CancellationToken.None);
            await ServerShells.RunReplAsync(session, CancellationToken.None);
            string.Concat(stream.StringWrites).Should().Contain("tel:sh> \r\nGoodbye.");
        }

        [Fact]
        public async Task Repl_UnknownCommand_MatchesReferenceText()
        {
            // Source of truth: unknown input reports "no such command.".
            // server_shell.py answers any non-empty unknown command with exactly that
            // string.
            // Our code: Server/ServerShells.cs answers "Unknown command. Type 'help'.",
            // breaking automation expecting the reference text.
            // Proof: feeding "bogus\nquit\n" must produce a transcript containing
            // "no such command."; the longer sentence proves the mismatch. This test
            // is correct.
            using var stream = new ScriptedStream(Ascii("bogus\nquit\n"));
            using var session = new ServerSession(stream, new TelnetServerOptions(), CancellationToken.None);
            await ServerShells.RunReplAsync(session, CancellationToken.None);
            string.Concat(stream.StringWrites).Should().Contain("no such command.");
        }
    }
}
