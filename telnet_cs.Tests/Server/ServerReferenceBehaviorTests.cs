namespace telnet_cs.Tests
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Net.Sockets;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using FluentAssertions;
    using Xunit;
    using telnet_cs.Server;

    /// <summary>
    /// Server behavior pins: opening negotiation preset, defaults, idle/timeout
    /// accounting, session shutdown, inbound processing, authentication echo
    /// handling, REPL editing, and client waiting, pinned against the
    /// telnetlib3 server behavior.
    /// </summary>
    public class ServerReferenceBehaviorTests
    {
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
        public async Task OpeningPreset_MatchesReference()
        {
            // Opening preset sends only DO TTYPE, then advances after the TTYPE ack
            // without unsolicited TSPEED/OLD_ENVIRON/LINEMODE.
            // Reference: server.py:251-256 (begin_negotiation sends only
            // iac(DO,TTYPE)); server_base.py:295-309
            // (negotiation_should_advance = any remote/local option
            // enabled); :326-329 (check_negotiation gates
            // begin_advanced_negotiation); server.py:258-289 body :270-280
            // (WILL SGA iff not line_mode, WILL BINARY, DO NAWS, DO
            // CHARSET iff default_encoding; default encoding="utf8" at
            // server.py:102; NO TSPEED/OLD_ENVIRON/LINEMODE — LINEMODE only
            // in LinemodeServer:904-913).
            // Repro: opening = FF FD 18 only (no FF FD 2A pre-ack); after
            // FF FB 18 (WILL TTYPE) -> FF FA 18 01 FF F0, FF FB 03,
            // FF FB 00, FF FD 1F, FF FD 2A; FF FD 20 / FF FD 24 / FF FD 22
            // absent before and after.
            using var stream = new ScriptedStream();
            using var session = new ServerSession(stream, new TelnetServerOptions(), CancellationToken.None);
            await session.SendOpeningPresetAsync();
            var opening = OutboundBytes(stream);
            ContainsSubsequence(opening, new byte[] { 255, 253, 24 }).Should().BeTrue("DO TTYPE opens the reference preset");
            stream.Enqueue(255, 251, 24);
            (await session.ReadAsync(TimeSpan.FromMilliseconds(300))).Should().BeEmpty();
            var advanced = OutboundBytes(stream);
            ContainsSubsequence(advanced, new byte[] { 255, 253, 42 }).Should().BeTrue("DO CHARSET follows the TTYPE ack in the reference preset");
            ContainsSubsequence(advanced, new byte[] { 255, 253, 32 }).Should().BeFalse("DO TSPEED is never unsolicited");
            ContainsSubsequence(advanced, new byte[] { 255, 253, 36 }).Should().BeFalse("DO OLD_ENVIRON is never unsolicited");
            ContainsSubsequence(advanced, new byte[] { 255, 253, 34 }).Should().BeFalse("DO LINEMODE is never unsolicited");
        }

        [Fact]
        public void RequestLinemode_DefaultFalse()
        {
            // Line mode is off by default (char mode); defaulting RequestLinemode
            // to true would emit DO LINEMODE.
            // Reference: server.py:75 (CONFIG.line_mode: bool = False),
            // :106 (TelnetServer.__init__ line_mode=False), :1100
            // (create_server line_mode=False) — char mode by default.
            // Defaulting RequestLinemode to true inverts it to DO LINEMODE.
            // Note: ServerAcceptTests.TelnetServerOptions_HasDocumentedDefaults
            // pins the current default; this test pins the reference default (false).
            new TelnetServerOptions().RequestLinemode.Should().BeFalse();
        }

        [Fact]
        public void DefaultCharsetOffers_MatchReference()
        {
            // Default charset offers match the reference order.
            // Reference: server.py:557-592, executable return :574-591 =
            // ["UTF-8","UTF-16","LATIN1","CP1252","ISO-8859-15","CP437",
            // "SHIFT_JIS","CP932","BIG5","CP950","GBK","GB2312","CP936",
            // "EUC-KR","CP949","US-ASCII"] (the :571-572 docstring lists
            // US-ASCII 4th; the executable return — source of truth — has
            // it last, matching this test). CJK peers that only ACCEPT
            // e.g. BIG5 otherwise stay US-ASCII.
            new TelnetServerOptions().CharsetOffers.Should().Equal(
                "UTF-8", "UTF-16", "LATIN1", "CP1252", "ISO-8859-15", "CP437",
                "SHIFT_JIS", "CP932", "BIG5", "CP950", "GBK", "GB2312",
                "CP936", "EUC-KR", "CP949", "US-ASCII");
        }

        [Fact]
        public async Task IacOnlyTraffic_ResetsIdle()
        {
            // IAC-only traffic still counts as peer activity and resets the idle stamp.
            // Reference: server_base.py:204-211 (data_received
            // unconditionally stamps _last_received = now() and counts
            // _rx_bytes += len(data) BEFORE _process_data_chunk).
            // Repro: seed _last_received -60 s; data_received(b'\xff\xf1')
            // (IAC NOP) advances _last_received to now. IAC keepalives
            // must not idle out.
            using var stream = new ScriptedStream(255, 241);
            using var session = new ServerSession(stream, new TelnetServerOptions(), CancellationToken.None);
            var before = session.Context.LastActivityUtc;
            (await session.ReadAsync(TimeSpan.FromMilliseconds(300))).Should().BeEmpty();
            session.Context.LastActivityUtc.Should().BeAfter(before);
        }

        [Fact]
        public void SetTimeout_NotCountedAsActivity()
        {
            // Arming a timeout does not itself count as peer activity.
            // Reference: server.py:428-447 (set_timeout cancels the old
            // timer, call_later's a new one, stores
            // _extra["timeout"]=duration; never touches _last_received).
            // Repro: _last_received identical before/after
            // set_timeout(300); get_extra_info("timeout")==300. Arming a
            // longer timeout must not itself look like peer activity.
            using var stream = new ScriptedStream();
            using var session = new ServerSession(stream, new TelnetServerOptions(), CancellationToken.None);
            var before = session.Context.LastActivityUtc;
            session.SetTimeout(TimeSpan.FromMinutes(5));
            session.Context.LastActivityUtc.Should().Be(before);
        }

        [Fact]
        public async Task Stop_ClosesAcceptedSessions()
        {
            // Stop closes accepted sessions.
            // Reference: server.py:957-963 (Server.close():
            // self._server.close(); for protocol in list(self._protocols):
            // protocol._transport.close()).
            // Repro (live create_server + raw socket): 1 protocol
            // registered, is_closing()==False pre-close; after close()
            // the transport is None/closing (True). Leaving sessions past
            // Stop would strand them until GC/Dispose.
            using var server = new TelnetServer(0);
            server.Start();
            var acceptTask = server.AcceptSessionAsync(CancellationToken.None);
            using var raw = new TcpClient();
            await raw.ConnectAsync("127.0.0.1", server.Port);
            using var session = await acceptTask.WaitAsync(TimeSpan.FromSeconds(10));
            session.IsConnected.Should().BeTrue();
            server.Stop();
            session.IsConnected.Should().BeFalse();
        }

        [Fact]
        public async Task InboundProcessed_WithoutCallerRead()
        {
            // Inbound negotiation is processed without requiring a caller read (push path).
            // Reference: server_base.py:204-246 (data_received ->
            // _process_data_chunk :237-239 -> cmd_received ->
            // _check_negotiation_timer :245-246 -> check_negotiation
            // :326-329 -> begin_advanced_negotiation), plus
            // stream_writer.py:2145-2226 (handle_will: server WILL NAWS ->
            // iac(DO,NAWS) + pending SB+NAWS). No reader.read() in the
            // path. A caller that accepts then writes without reading
            // stalls TTYPE/ENVIRON/CHARSET here.
            // Repro: data_received(b'\xff\xfb\x1f') with zero reads ->
            // outbound FF FD 1F (+ advanced preset).
            using var stream = new ScriptedStream(255, 251, 31);
            using var session = new ServerSession(stream, new TelnetServerOptions(), CancellationToken.None);
            await Task.Delay(200);
            ContainsSubsequence(OutboundBytes(stream), new byte[] { 255, 253, 31 }).Should().BeTrue();
        }

        [Fact]
        public async Task AuthenticateAsync_SendsNoWillEcho()
        {
            // Authentication sends no WILL ECHO; ECHO stays deferred.
            // Reference: no Authenticate/authenticate/login symbol exists
            // in server.py/server_base.py/server_shell.py (grep empty);
            // WILL ECHO is emitted only at server.py:709 inside
            // _negotiate_echo :681-709, called only from
            // check_negotiation:302 and on_ttype:620 (plus
            // LinemodeServer:915-919 skip) — deferred ECHO per :258-275.
            // Emitting WILL ECHO at login re-arms the MUD password-mode
            // hazard that deferring ECHO exists to avoid.
            using var stream = new ScriptedStream("user\npass\n");
            using var session = new ServerSession(stream, new TelnetServerOptions(), CancellationToken.None);
            (await session.AuthenticateAsync((user, pass) => Task.FromResult(true), TimeSpan.FromSeconds(5))).Should().BeTrue();
            ContainsSubsequence(OutboundBytes(stream), new byte[] { 255, 251, 1 }).Should().BeFalse();
        }

        [Fact]
        public async Task ReplSlcCommand_Handled()
        {
            // The REPL "slc" command prints the special-line-characters table instead of "no such command".
            // Reference: server_shell.py:235-237 (command=="slc" ->
            // _write(get_slcdata)), :238-243 (linemode/toggle/dump),
            // :283-284 ("no such command." only on fallthrough).
            // Repro ("slc\rquit\r" input):
            // output is the "Special Line Characters:..." table and
            // 'no such command' not in output.
            using var stream = new ScriptedStream("slc\nquit\n");
            using var session = new ServerSession(stream, new TelnetServerOptions(), CancellationToken.None);
            await ServerShells.RunReplAsync(session, CancellationToken.None);
            var text = string.Concat(stream.StringWrites) + Encoding.ASCII.GetString(OutboundBytes(stream));
            text.Should().NotContain("no such command");
        }

        [Fact]
        public async Task ReplBackspace_EditsLine()
        {
            // The REPL backspace edits the current line (grapheme delete).
            // Reference: server_shell.py:122-130 (_backspace_grapheme ->
            // "\b \b" * width), :139-174 (_LineEditor.feed), :162-167
            // (char in ("\b","\x7f") -> grapheme delete).
            // Repro: _LineEditor after "helpp" -> command=='helpp'; after
            // \x08 -> command=='help', echo=='\x08 \x08'; CR returns
            // 'help' (so the help branch hits, not "no such command").
            using var stream = new ScriptedStream("helpp\x08\nquit\n");
            using var session = new ServerSession(stream, new TelnetServerOptions(), CancellationToken.None);
            await ServerShells.RunReplAsync(session, CancellationToken.None);
            var text = string.Concat(stream.StringWrites) + Encoding.ASCII.GetString(OutboundBytes(stream));
            text.Should().NotContain("no such command");
        }

        [Fact]
        public void Server_ExposesWaitForClient()
        {
            // A wait-for-next-client primitive is exposed (C# shape: WaitForClientAsync).
            // Reference: server.py:993-1005 (async def wait_for_client:
            // return await self._new_client.get()), :955
            // (Queue(maxsize=1000)), :922-937 (_enqueue_client,
            // oldest-dropped), :1007-1016 (_register_protocol +
            // _waiter_connected callback).
            // Repro: hasattr(Server,"wait_for_client")==True; live
            // create_server + WILL TTYPE client -> await wait_for_client()
            // returns a TelnetServer. Callers must hand-roll it here without this API.
            typeof(TelnetServer).GetMethod("WaitForClientAsync").Should().NotBeNull();
        }
    }
}
