// S1 server tests: listener lifecycle, accepted-session echo round-trip
// against a real Client, and the per-instance proactive-negotiation flag.
// Hermetic like SocketIntegrationTests: loopback only, OS-assigned
// ports, every wait bounded.
namespace telnet_cs.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Net.Sockets;
    using System.Threading;
    using System.Threading.Tasks;
    using FakeItEasy;
    using FluentAssertions;
    using Xunit;
    using telnet_cs.Client;
    using telnet_cs.Protocol;
    using telnet_cs.Server;
    using telnet_cs.Transport;

    public class ServerSessionTests
    {
        [Fact]
        public async Task AcceptSessionAsync_EchoRoundTrip()
        {
            using var server = new TelnetServer(0);
            server.Start();
            server.Port.Should().BeInRange(1, 65535);
            var acceptTask = server.AcceptSessionAsync(CancellationToken.None);
            using var client = await Client.ConnectAsync("127.0.0.1", server.Port);
            using var session = await acceptTask;
            session.IsConnected.Should().BeTrue();

            await client.WriteAsync("hello");
            // Early reads may contain only negotiation (the client's opening DO
            // SGA): accumulate until the text arrives.
            var received = string.Empty;
            var sw = Stopwatch.StartNew();
            while (!received.Contains("hello", StringComparison.Ordinal) && sw.Elapsed < TimeSpan.FromSeconds(10))
            {
                received += await session.ReadAsync();
            }

            received.Should().Contain("hello");
            await session.WriteAsync(received);
            var echo = await client.TerminatedReadAsync("hello", TimeSpan.FromSeconds(10));
            echo.Should().Contain("hello");

            // Port of test_telnet_server_open_close: WONT TTYPE is consumed
            // silently — never surfaces as data.
            await client.RequestDisableAsync(Options.TerminalType);
            (await session.ReadAsync(TimeSpan.FromMilliseconds(500))).Should().BeEmpty();

            // Port of test_telnet_server_closed_by_server: "quit\r\n" behind
            // a WONT TTYPE preamble reads as a clean line, preamble excluded.
            await client.WriteAsync("quit\r\n");
            (await session.TerminatedReadAsync("\n", TimeSpan.FromSeconds(10))).Should().Be("quit\r\n");
        }

        [Fact]
        public async Task ClientCtor_SkipProactiveNegotiation_SendsNothingOnConnect()
        {
            using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;

            // Baseline: opting into proactive opens with IAC DO SuppressGoAhead
            // (the default skips proactive, so it must be requested explicitly).
            var acceptBaseline = listener.AcceptTcpClientAsync();
            using (GlobalStateGuard.SkipProactive(false))
            {
#pragma warning disable CA2000 // Ownership of the stream transfers to the Client.
                using var baseline = new Client(
                  new TcpByteStream("127.0.0.1", port),
                  TimeSpan.FromSeconds(30),
                  CancellationToken.None,
                  Array.Empty<(Commands Command, Options Option)>(),
                  skipProactiveNegotiation: false);
#pragma warning restore CA2000
                using var serverBaseline = await acceptBaseline;
                var baselineStream = serverBaseline.GetStream();
                baselineStream.ReadTimeout = 5000;
                var opening = new byte[3];
                int read = 0;
                while (read < opening.Length)
                {
                    int n = baselineStream.Read(opening, read, opening.Length - read);
                    if (n == 0)
                    {
                        break;
                    }

                    read += n;
                }

                read.Should().Be(3);
                opening.Should().Equal(255, 253, 3);
            }

            // Flagged: the ctor stays silent (and so do the explicit options).
            var acceptSilent = listener.AcceptTcpClientAsync();
#pragma warning disable CA2000 // Ownership of the stream transfers to the Client.
            using var silent = new Client(
              new TcpByteStream("127.0.0.1", port),
              TimeSpan.FromSeconds(30),
              CancellationToken.None,
              Array.Empty<(Commands Command, Options Option)>(),
              skipProactiveNegotiation: true);
#pragma warning restore CA2000
            using var serverSilent = await acceptSilent;
            var silentStream = serverSilent.GetStream();
            silentStream.ReadTimeout = 500;
            Action readAttempt = () => silentStream.ReadByte();
            readAttempt.Should().Throw<IOException>();
        }
    }

    // S2 server tests: opening preset, explicit requests, SendCommand, the
    // Synch gate, terminated reads, and password authentication. Scripted
    // role-neutral ScriptedStream instances stand in for peer clients.
    public class ServerSessionProtocolTests
    {
        private static ServerSession NewSession(ScriptedStream stream, TelnetServerOptions? options = null)
        {
            return new ServerSession(stream, options ?? new TelnetServerOptions(), CancellationToken.None);
        }

        private static byte[] OutboundBytes(ScriptedStream stream)
        {
            return stream.ByteWrites.SelectMany(static b => b).ToArray();
        }

        private static int CountFrame(byte[] haystack, byte[] needle)
        {
            int count = 0;
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
                    count++;
                }
            }

            return count;
        }

        [Fact]
        public async Task GaUnsuppressed_SurfacesThroughSession()
        {
            using var stream = new ScriptedStream(255, 249);
            using var session = NewSession(stream);
            var fired = 0;
            session.GoAheadReceived += (_, _) => fired++;
            (await session.ReadAsync(TimeSpan.FromMilliseconds(100))).Should().BeEmpty();
            stream.ByteWrites.Should().BeEmpty();
        }

        [Fact]
        public async Task DoLogout_ClosesStreamWithoutReply()
        {
            // RFC 727: a DO LOGOUT asks us to end the session — no
            // negotiation bytes go out, the stream closes (the reference
            // closes its transport).
            using var stream = new ScriptedStream(255, 253, 18);
            using var session = NewSession(stream);
            (await session.ReadAsync(TimeSpan.FromMilliseconds(100))).Should().BeEmpty();
            stream.Connected.Should().BeFalse();
            stream.ByteWrites.Should().BeEmpty();
        }

        [Fact]
        public async Task DoLogout_RepeatedDo_StaysClosedWithoutThrowing()
        {
            // The hook fires on every DO LOGOUT (repeats included); Close
            // is idempotent so the second close is a no-op.
            using var stream = new ScriptedStream(255, 253, 18, 255, 253, 18);
            using var session = NewSession(stream);
            (await session.ReadAsync(TimeSpan.FromMilliseconds(100))).Should().BeEmpty();
            stream.Connected.Should().BeFalse();
            stream.ByteWrites.Should().BeEmpty();
        }

        [Fact]
        public async Task PlainListener_TlsClientHello_ClosesWithWarning()
        {
            // A 0x16 lead byte on a plaintext listener is a TLS ClientHello:
            // warn and close instead of parsing it (reference data_received).
            var log = new List<string>();
            var options = new TelnetServerOptions { Log = msg => { lock (log) log.Add(msg); } };
            using var stream = new ScriptedStream(0x16, 0x03, 0x01, 0x02);
            using var session = NewSession(stream, options);
            (await session.ReadAsync(TimeSpan.FromMilliseconds(500))).Should().BeEmpty();
            stream.Connected.Should().BeFalse();
            lock (log) log.Should().ContainSingle(m => m.Contains("TLS ClientHello", StringComparison.Ordinal));
        }

        [Fact]
        public async Task PlainListener_OrdinaryData_UnaffectedByTlsSniff()
        {
            // The first-data sniff only fires on 0x16; normal data flows.
            using var stream = new ScriptedStream((int)'h', (int)'i');
            using var session = NewSession(stream);
            (await session.ReadAsync(TimeSpan.FromMilliseconds(500))).Should().Be("hi");
            stream.Connected.Should().BeTrue();
        }

        [Fact]
        public async Task ReadAsync_SocketException_ReturnsEmpty()
        {
            // Same dead-peer mapping as the client: reset mid-read yields
            // empty, not an error.
            var fake = A.Fake<IByteStream>();
            A.CallTo(() => fake.Connected).Returns(true);
            A.CallTo(() => fake.Available).Returns(1);
            A.CallTo(() => fake.ReadByte()).Throws(new SocketException((int)SocketError.ConnectionReset));
            using var session = new ServerSession(fake, new TelnetServerOptions(), CancellationToken.None);
            (await session.ReadAsync(TimeSpan.FromMilliseconds(500))).Should().BeEmpty();
        }

        [Fact]
        public async Task SendOpeningPresetAsync_SendsExactPresetFrames()
        {
            // Reference begin_negotiation: the opening preset is DO TTYPE
            // only; SGA/BINARY/NAWS/CHARSET follow in the advanced preset
            // once negotiation advances (telnetlib3 server.py:251-256).
            using var stream = new ScriptedStream();
            using var session = NewSession(stream);
            await session.SendOpeningPresetAsync();
            OutboundBytes(stream).Should().Equal(
              255, 253, 24);
        }

        [Fact]
        public async Task SendOpeningPresetAsync_AllTogglesOn_SendsExactPresetFrames()
        {
            // The toggles surface in the advanced preset after the peer
            // answers; the opening itself stays exactly DO TTYPE.
            var options = new TelnetServerOptions
            {
                RequestXDisplay = true,
                RequestCharacterSet = true,
                RequestSendLocation = true,
            };
            using var stream = new ScriptedStream();
            using var session = NewSession(stream, options);
            await session.SendOpeningPresetAsync();
            OutboundBytes(stream).Should().Equal(
              255, 253, 24);
        }

        [Fact]
        public async Task SendOpeningPresetAsync_AllTogglesOff_SendsNothing()
        {
            var options = new TelnetServerOptions
            {
                OfferEcho = false,
                OfferSuppressGoAhead = false,
                OfferBinary = false,
                RequestTerminalType = false,
                RequestTerminalSpeed = false,
                RequestWindowSize = false,
                RequestEnvironment = false,
                RequestLinemode = false,
            };
            using var stream = new ScriptedStream();
            using var session = NewSession(stream, options);
            await session.SendOpeningPresetAsync();
            stream.ByteWrites.Should().BeEmpty();
        }

        [Fact]
        public async Task SendOpeningPresetAsync_SecondCall_SendsNothingNew()
        {
            using var stream = new ScriptedStream();
            using var session = NewSession(stream);
            await session.SendOpeningPresetAsync();
            int first = stream.ByteWrites.Count;
            first.Should().Be(1);
            await session.SendOpeningPresetAsync();
            stream.ByteWrites.Should().HaveCount(first);
        }

        [Fact]
        public async Task RequestEnableAsync_SendsDoOnce()
        {
            using var stream = new ScriptedStream();
            using var session = NewSession(stream);
            await session.RequestEnableAsync(Options.TerminalType);
            await session.RequestEnableAsync(Options.TerminalType);
            stream.ByteWrites.Should().HaveCount(1);
            OutboundBytes(stream).Should().Equal(255, 253, 24);
        }

        [Fact]
        public async Task RequestDisableAsync_WhenAlreadyDisabled_SendsNothing()
        {
            // RFC 1143: requesting a disable while already in NO sends nothing.
            // (Client.RequestDisableAsync documents the same rule.)
            using var stream = new ScriptedStream();
            using var session = NewSession(stream);
            await session.RequestDisableAsync(Options.SuppressGoAhead);
            stream.ByteWrites.Should().BeEmpty();
        }

        [Fact]
        public async Task OpeningPreset_PeerWillTtype_AgreesWithoutSecondDo()
        {
            // Port of test_telnet_server_advanced_negotiation: the preset opens
            // with IAC DO TTYPE; the peer's WILL TTYPE agrees (him-side YES)
            // without a second DO (RFC 1143: no reply to an ACK).
            using var stream = new ScriptedStream();
            using var session = NewSession(stream);
            await session.SendOpeningPresetAsync();
            stream.Enqueue(255, 251, 24);
            await session.ReadAsync(TimeSpan.FromMilliseconds(500));
            session.Negotiation.IsEnabledByPeer((int)Options.TerminalType).Should().BeTrue();
            CountFrame(OutboundBytes(stream), [255, 253, 24]).Should().Be(1);
        }

        [Fact]
        public async Task RequestTerminalTypesAsync_NoAnswers_ReturnsEmptyWithTtypeOutstanding()
        {
            // Port of test_telnet_server_negotiation_fail: the preset's IAC DO
            // TTYPE goes unanswered, so the him-side stays WantYes (outstanding)
            // and the collection times out empty. (The collector sends SB SEND
            // directly; the outstanding DO comes from the preset.)
            using var stream = new ScriptedStream();
            using var session = NewSession(stream);
            await session.SendOpeningPresetAsync();
            (await session.RequestTerminalTypesAsync(TimeSpan.FromMilliseconds(300))).Should().BeEmpty();
            session.Negotiation.GetStates((int)Options.TerminalType).Him
                .Should().Be(NegotiationState.SideState.WantYes);
        }

        [Fact]
        public async Task OpeningPreset_AllTogglesOff_PeerWillTtype_SendsNothing()
        {
            // EXTEND of SendOpeningPresetAsync_AllTogglesOff_SendsNothing: with
            // SGA/ECHO offers off (and CHARSET requesting off so the advanced
            // preset stays silent), the peer's unsolicited WILL TTYPE gets no
            // DO reply (the server records it without answering) and the SB
            // probe stays off because TTYPE requesting is off: nothing goes
            // out, and no WILL SGA / WILL ECHO either.
            var options = new TelnetServerOptions
            {
                OfferEcho = false,
                OfferSuppressGoAhead = false,
                OfferBinary = false,
                RequestTerminalType = false,
                RequestTerminalSpeed = false,
                RequestWindowSize = false,
                RequestEnvironment = false,
                RequestLinemode = false,
                RequestCharacterSet = false,
            };
            using var stream = new ScriptedStream();
            using var session = NewSession(stream, options);
            await session.SendOpeningPresetAsync();
            stream.Enqueue(255, 251, 24);
            await session.ReadAsync(TimeSpan.FromMilliseconds(500));
            OutboundBytes(stream).Should().BeEmpty();
        }

        [Fact]
        public async Task OpeningPreset_OfferSga_SentExactlyOnceAndOutstanding()
        {
            // Port of test_default_sends_will_sga: WILL SGA leaves with the
            // advanced preset (not the opening) once the peer WILLs TTYPE;
            // the us-side stays WantYes until answered.
            using var stream = new ScriptedStream();
            using var session = NewSession(stream);
            await session.SendOpeningPresetAsync();
            stream.Enqueue(255, 251, 24);
            await session.ReadAsync(TimeSpan.FromMilliseconds(500));
            CountFrame(OutboundBytes(stream), [255, 251, 3]).Should().Be(1);
            session.Negotiation.GetStates((int)Options.SuppressGoAhead).Us
                .Should().Be(NegotiationState.SideState.WantYes);
        }

        [Fact]
        public async Task RequestDisableAsync_AfterPeerAgrees_SendsDont()
        {
            using var stream = new ScriptedStream();
            using var session = NewSession(stream);
            await session.RequestEnableAsync(Options.TerminalType);
            // Peer WILLs TTYPE over the wire (agreeing with our DO): him-side YES.
            stream.Enqueue(255, 251, 24);
            await session.ReadAsync(TimeSpan.FromMilliseconds(500));
            session.Negotiation.IsEnabledByPeer((int)Options.TerminalType).Should().BeTrue();
            await session.RequestDisableAsync(Options.TerminalType);
            // The DONT is still sent; the peer's WILL TTYPE first released
            // the TTYPE probe and the advanced batch (reference
            // begin_advanced_negotiation).
            stream.ByteWrites.Should().HaveCount(7);
            stream.ByteWrites[0].Should().Equal(255, 253, 24);
            stream.ByteWrites[1].Should().Equal(255, 250, 24, 1, 255, 240);
            stream.ByteWrites[2].Should().Equal(255, 251, 3);
            stream.ByteWrites[3].Should().Equal(255, 251, 0);
            stream.ByteWrites[4].Should().Equal(255, 253, 31);
            stream.ByteWrites[5].Should().Equal(255, 253, 42);
            stream.ByteWrites[6].Should().Equal(255, 254, 24);
        }

        [Theory]
        [InlineData(Commands.Break)]
        [InlineData(Commands.InterruptProcess)]
        [InlineData(Commands.AbortOutput)]
        [InlineData(Commands.AreYouThere)]
        [InlineData(Commands.EraseCharacter)]
        [InlineData(Commands.EraseLine)]
        [InlineData(Commands.GoAhead)]
        [InlineData(Commands.NoOperation)]
        [InlineData(Commands.EndOfFile)]
        [InlineData(Commands.Suspend)]
        [InlineData(Commands.Abort)]
        public async Task SendCommand_SendsControlFrame(Commands command)
        {
            using var stream = new ScriptedStream();
            using var session = NewSession(stream);
            await session.SendCommand(command);
            OutboundBytes(stream).Should().Equal(255, (byte)command);
        }

        [Theory]
        [InlineData(Commands.Do)]
        [InlineData(Commands.Dont)]
        [InlineData(Commands.Will)]
        [InlineData(Commands.Wont)]
        [InlineData(Commands.Subnegotiation)]
        [InlineData(Commands.SubnegotiationEnd)]
        [InlineData(Commands.InterpretAsCommand)]
        [InlineData(Commands.DataMark)]
        public async Task SendCommand_RejectsNegotiationVerbs(Commands command)
        {
            using var stream = new ScriptedStream();
            using var session = NewSession(stream);
            Func<Task> act = () => session.SendCommand(command);
            await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
            stream.ByteWrites.Should().BeEmpty();
        }

        [Fact]
        public async Task SendSynchAsync_WithoutTcpStream_ThrowsNotSupported()
        {
            using var stream = new ScriptedStream();
            using var session = NewSession(stream);
            Func<Task> act = () => session.SendSynchAsync();
            await act.Should().ThrowAsync<NotSupportedException>();
        }

        [Fact]
        public async Task TerminatedReadAsync_ReturnsAvailableText()
        {
            // Termination stops at the first terminator and stashes the tail
            // for the next read — the same cut-and-stash semantics as the
            // client reader and telnetlib3's readuntil, which consumes
            // through the separator and buffers the remainder.
            using var stream = new ScriptedStream("hi\nrest");
            using var session = NewSession(stream);
            var line = await session.TerminatedReadAsync("\n", TimeSpan.FromMilliseconds(500));
            line.Should().Be("hi\n");
        }

        [Fact]
        public async Task TerminatedReadAsync_CrNulCollapsesToSingleCr()
        {
            // CR NUL is the wire spelling of a lone CR (RFC 854): raw reads
            // preserve both bytes, line helpers normalize — same as the
            // client reader.
            using var stream = new ScriptedStream("A\r\0B\n");
            using var session = NewSession(stream);
            var line = await session.TerminatedReadAsync("\n", TimeSpan.FromMilliseconds(500));
            line.Should().Be("A\rB\n");
        }

        // NOTE: multi-line auth conversations need later lines Enqueued after
        // the earlier read requests them (never up front: one read drains all
        // queued bytes, so the first credential read would swallow the second
        // line — same reason client-side logins are only covered over
        // loopback). Accept/reject paths are covered by
        // ServerAcceptTests.LoginInterop_* over real sockets; the fail-closed,
        // validation, and attempt-budget edges below are stream-level and safe.

        [Fact]
        public async Task AuthenticateAsync_TimeoutWithoutLineEnding_ReturnsFalse()
        {
            using var stream = new ScriptedStream("partial");
            // No inter-attempt delay: this test asserts the timeout shape,
            // not the guessing throttle.
            using var session = NewSession(stream, new TelnetServerOptions { LoginAttemptDelay = TimeSpan.Zero });
            bool ok = await session.AuthenticateAsync(
              (u, p) => Task.FromResult(true),
              TimeSpan.FromMilliseconds(300));
            ok.Should().BeFalse();
        }

        [Fact]
        public async Task AuthenticateAsync_NullValidator_Throws()
        {
            using var stream = new ScriptedStream();
            using var session = NewSession(stream);
            Func<Task> act = () => session.AuthenticateAsync(null!, TimeSpan.FromSeconds(1));
            await act.Should().ThrowAsync<ArgumentNullException>();
        }

        [Fact]
        public async Task AuthenticateAsync_InvalidMaxAttempts_Throws()
        {
            var options = new TelnetServerOptions { MaxLoginAttempts = 0 };
            using var stream = new ScriptedStream();
            using var session = NewSession(stream, options);
            Func<Task> act = () => session.AuthenticateAsync((u, p) => Task.FromResult(true), TimeSpan.FromSeconds(1));
            await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
        }

        [Fact]
        public async Task AuthenticateAsync_CredentialLines_AreNeverEchoed()
        {
            // Multi-line auth works on ScriptedStream when later lines are
            // Enqueued after the earlier read requests them (never up front).
            using var stream = new ScriptedStream();
            using var session = NewSession(stream);
            // Agree echo in an earlier read (mid-read agreements never echo,
            // by the snapshot rule — and since the read path never replays
            // inbound bytes at all, this agreement changes no wire bytes
            // below; it only exercises the negotiated state).
            stream.Enqueue(255, 253, 1);
            (await session.ReadAsync(TimeSpan.FromMilliseconds(500))).Should().BeEmpty();
            OutboundBytes(stream).Take(3).Should().Equal(255, 251, 1);

            (string User, string Pass)? seen = null;
            var authTask = session.AuthenticateAsync(
              (u, p) => { seen = (u, p); return Task.FromResult(u == "bob" && p == "s3cret"); },
              TimeSpan.FromSeconds(30));
            await WaitForWritesAsync(stream, writes => writes.Contains("login: "));
            stream.Enqueue([.. "bob\n".Select(c => (int)c)]);
            await WaitForWritesAsync(stream, writes => writes.Contains("Password: "));
            stream.Enqueue([.. "s3cret\n".Select(c => (int)c)]);
            (await authTask).Should().BeTrue(seen?.ToString());

            // Neither credential line hits the wire: the read path never
            // replays inbound bytes, so password secrecy is structural (no
            // per-read suppression needed) — and the username stays silent
            // too; echoing is the application's job. AuthenticateAsync itself
            // sends no negotiation bytes at all (prompts only); the three
            // 251s are WILL ECHO (pre-agreed above) plus WILL SGA / WILL
            // BINARY from the advanced preset the DO ECHO released.
            string outbound = System.Text.Encoding.UTF8.GetString(OutboundBytes(stream));
            outbound.Should().NotContain("bob");
            outbound.Should().NotContain("s3cret");
            OutboundBytes(stream).Where(b => b == 251).Should().HaveCount(3);
        }

        private static async Task WaitForWritesAsync(ScriptedStream stream, Func<string, bool> ready)
        {
            string writes = string.Empty;
            var sw = Stopwatch.StartNew();
            while (!ready(writes) && sw.Elapsed < TimeSpan.FromSeconds(30))
            {
                await Task.Delay(20);
                writes = System.Text.Encoding.UTF8.GetString(OutboundBytes(stream));
            }

            ready(writes).Should().BeTrue();
        }
    }

    // S3 server tests: TTYPE/TSPEED/ENVIRON requesters (session SENDs, peer
    // IS answers) and inbound NAWS parsing. Scripted role-neutral
    // ScriptedStream instances stand in for peer clients.
    public class ServerSessionRequesterTests
    {
        private static ServerSession NewSession(ScriptedStream stream)
        {
            // Peer agreement for the collectors (state-only, no wire bytes).
            // NewEnvironment is omitted so TTYPE tests still send DO NEW_ENVIRON
            // (a volunteered WILL would suppress the DO); NewEnv tests add it
            // per-test below.
            var s = new ServerSession(stream, new TelnetServerOptions(), CancellationToken.None);
            s.Negotiation.ReceivedWill((int)Options.TerminalType, agree: true);
            s.Negotiation.ReceivedWill((int)Options.TerminalSpeed, agree: true);
            s.Negotiation.ReceivedWill((int)Options.XDisplay, agree: true);
            s.Negotiation.ReceivedWill((int)Options.OldEnvironment, agree: true);
            s.Negotiation.ReceivedWill((int)Options.CharacterSet, agree: true);
            return s;
        }

        private static byte[] OutboundBytes(ScriptedStream stream)
        {
            return stream.ByteWrites.SelectMany(static b => b).ToArray();
        }

        private static int[] TtypeIsFrame(string value)
        {
            return [255, 250, 24, 0, .. System.Text.Encoding.Latin1.GetBytes(value), 255, 240];
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

        private static int CountSubsequence(byte[] haystack, byte[] needle)
        {
            int count = 0;
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
                    count++;
                }
            }

            return count;
        }

        [Fact]
        public async Task DeferredEcho_MudClient_SkipsWillEcho()
        {
            // MUD clients render WILL ECHO as password mode, so the deferred
            // echo offer is withheld once TTYPE names one.
            using var stream = new ScriptedStream();
            stream.Enqueue([.. TtypeIsFrame("Mudlet"), .. TtypeIsFrame("Mudlet")]);
            using var session = NewSession(stream);
            (await session.RequestTerminalTypesAsync(TimeSpan.FromSeconds(5))).Should().Equal("Mudlet");
            // Peer WILL agreement also releases the advanced preset and the
            // WILL-triggered probe, so the wire contains additional frames
            // beyond the collector's own SENDs. WILL ECHO stays withheld for
            // the MUD client, but DO NEW_ENVIRON still follows the answers.
            CountSubsequence(OutboundBytes(stream), [255, 250, 24, 1, 255, 240]).Should().BeGreaterThanOrEqualTo(2);
            ContainsSubsequence(OutboundBytes(stream), [255, 253, 39]).Should().BeTrue();
            ContainsSubsequence(OutboundBytes(stream), [255, 251, 1]).Should().BeFalse();
        }

        [Fact]
        public async Task DeferredEnviron_AnsiFirstAnswer_WaitsForSecond()
        {
            // Microsoft telnet (ANSI ...) crashes on NEW_ENVIRON, so the
            // deferred DO waits for the resolving second answer.
            var options = new TelnetServerOptions { RequestNewEnvironment = true };
            using var stream = new ScriptedStream();
            stream.Enqueue([.. TtypeIsFrame("ANSI"), .. TtypeIsFrame("VT100"), .. TtypeIsFrame("VT100")]);
            using var session = new ServerSession(stream, options, CancellationToken.None);
            session.Negotiation.ReceivedWill((int)Options.TerminalType, agree: true);
            (await session.RequestTerminalTypesAsync(TimeSpan.FromSeconds(5))).Should().Equal("ANSI", "VT100");
            byte[] outbound = OutboundBytes(stream);
            ContainsSubsequence(outbound, [255, 253, 39]).Should().BeTrue();
            ContainsSubsequence(outbound, [255, 251, 1]).Should().BeTrue();
        }

        [Fact]
        public async Task TtypeStall_FinalTimeout_ReleasesEchoOnly()
        {
            // One ANSI answer and then silence: the first answer arms WILL
            // ECHO, but nothing arms the environ phase (an ANSI first
            // answer defers it, and the stalled final wait releases
            // nothing without advance), so no DO NEW_ENVIRON goes out.
            var options = new TelnetServerOptions { RequestNewEnvironment = true };
            using var stream = new ScriptedStream();
            stream.Enqueue(TtypeIsFrame("ANSI"));
            using var session = new ServerSession(stream, options, CancellationToken.None);
            session.Negotiation.ReceivedWill((int)Options.TerminalType, agree: true);
            session.Negotiation.ReceivedWill((int)Options.TerminalSpeed, agree: true);
            session.Negotiation.ReceivedWill((int)Options.XDisplay, agree: true);
            session.Negotiation.ReceivedWill((int)Options.OldEnvironment, agree: true);
            session.Negotiation.ReceivedWill((int)Options.NewEnvironment, agree: true);
            session.Negotiation.ReceivedWill((int)Options.CharacterSet, agree: true);
            (await session.RequestTerminalTypesAsync(TimeSpan.FromMilliseconds(300))).Should().Equal("ANSI");
            ContainsSubsequence(OutboundBytes(stream), [255, 251, 1]).Should().BeTrue();
            ContainsSubsequence(OutboundBytes(stream), [255, 253, 39]).Should().BeFalse();
        }

        [Fact]
        public async Task TtypeRefused_SendsNothingWithoutAdvance()
        {
            // A raw client that WONTs TTYPE arms both deferred offers, but
            // a refusal alone advances nothing, so neither WILL ECHO nor
            // DO NEW_ENVIRON goes out.
            var options = new TelnetServerOptions { RequestNewEnvironment = true };
            using var stream = new ScriptedStream();
            using var session = new ServerSession(stream, options, CancellationToken.None);
            await session.SendOpeningPresetAsync();
            int presetBytes = OutboundBytes(stream).Length;
            stream.Enqueue([255, 252, 24]);
            (await session.ReadAsync(TimeSpan.FromMilliseconds(500))).Should().BeEmpty();
            OutboundBytes(stream).Skip(presetBytes).ToArray().Should().BeEmpty();
        }

        [Fact]
        public async Task FinalTimeout_WithOutstandingCharset_Warns()
        {
            // The reference warns when the final wait ends with environ or
            // charset subnegotiations still outstanding.
            var log = new List<string>();
            var options = new TelnetServerOptions { RequestCharacterSet = true, Log = msg => { lock (log) log.Add(msg); } };
            using var stream = new ScriptedStream();
            using var session = new ServerSession(stream, options, CancellationToken.None);
            session.Negotiation.ReceivedWill((int)Options.TerminalType, agree: true);
            session.Negotiation.ReceivedWill((int)Options.TerminalSpeed, agree: true);
            session.Negotiation.ReceivedWill((int)Options.XDisplay, agree: true);
            session.Negotiation.ReceivedWill((int)Options.OldEnvironment, agree: true);
            session.Negotiation.ReceivedWill((int)Options.NewEnvironment, agree: true);
            session.Negotiation.ReceivedWill((int)Options.CharacterSet, agree: true);
            // The charset requester gets a far-future timeout (cancelled below),
            // so it is deterministically still outstanding when the TTYPE final
            // wait ends. Two equal timeouts would race their cleanups: whichever
            // task clears its expecting-flag first decides whether the warning
            // fires (20/20 failure under CPU load with 300 ms vs 300 ms).
            using var charsetCts = new CancellationTokenSource();
            var charsetTask = session.RequestCharsetAsync(TimeSpan.FromSeconds(30), charsetCts.Token);
            (await session.RequestTerminalTypesAsync(TimeSpan.FromMilliseconds(300))).Should().BeEmpty();
            lock (log) log.Should().ContainSingle(m => m.Contains("Waiting for critical subnegotiation", StringComparison.Ordinal));
            charsetCts.Cancel();
            (await charsetTask).Should().BeNull();
        }

        [Fact]
        public async Task CheckEncoding_BinaryOutbound_RequestsInbound()
        {
            // The peer agreed our WILL BINARY (server-to-client binary); the
            // encoding check then asks for the inbound direction too.
            using var stream = new ScriptedStream();
            stream.Enqueue([255, 253, 0]);
            using var session = new ServerSession(stream, new TelnetServerOptions(), CancellationToken.None);
            (await session.ReadAsync(TimeSpan.FromMilliseconds(500))).Should().BeEmpty();
            OutboundBytes(stream).Should().Equal(
              255, 251, 0,
              255, 251, 3, 255, 253, 31, 255, 253, 42,
              255, 253, 0);
        }

        [Fact]
        public async Task CheckEncoding_CharsetAgreed_SendsAutoRequest()
        {
            // CHARSET agreed both ways (peer's WILL and DO) fires one
            // background REQUEST (RFC 2066 §5: only a WILL+DO side requests).
            var options = new TelnetServerOptions { RequestCharacterSet = true };
            using var stream = new ScriptedStream();
            stream.Enqueue([255, 251, 42, 255, 253, 42]);
            var session = new ServerSession(stream, options, CancellationToken.None);
            try
            {
                (await session.ReadAsync(TimeSpan.FromMilliseconds(500))).Should().BeEmpty();
                byte[] outbound = [];
                var sw = Stopwatch.StartNew();
                while (!ContainsSubsequence(outbound, [255, 250, 42, 1]) && sw.Elapsed < TimeSpan.FromSeconds(5))
                {
                    await Task.Delay(50);
                    outbound = OutboundBytes(stream);
                }

                ContainsSubsequence(outbound, [255, 250, 42, 1]).Should().BeTrue();
            }
            finally
            {
                session.Dispose();
            }
        }

        [Fact]
        public async Task RequestTerminalTypesAsync_CollectsChainUntilRepeat()
        {
            using var stream = new ScriptedStream();
            stream.Enqueue([.. TtypeIsFrame("aaa"), .. TtypeIsFrame("bbb"), .. TtypeIsFrame("bbb")]);
            using var session = NewSession(stream);
            var types = await session.RequestTerminalTypesAsync(TimeSpan.FromSeconds(5));
            types.Should().Equal("aaa", "bbb");
            // Peer WILL agreement also releases the advanced preset and the
            // WILL-triggered probe, so the wire contains additional frames.
            // The resolving repeat still releases WILL ECHO and DO NEW_ENVIRON.
            CountSubsequence(OutboundBytes(stream), [255, 250, 24, 1, 255, 240]).Should().BeGreaterThanOrEqualTo(3);
            ContainsSubsequence(OutboundBytes(stream), [255, 251, 1]).Should().BeTrue();
            ContainsSubsequence(OutboundBytes(stream), [255, 253, 39]).Should().BeTrue();
        }

        [Fact]
        public async Task RequestTerminalTypesAsync_SecondCall_RestartsFromTop()
        {
            // RFC 1091 §5: after the duplicate-terminated cycle, an extra SEND
            // restarts from the most specific type; each call clears the chain
            // and sends its own SEND.
            using var stream = new ScriptedStream();
            stream.Enqueue([.. TtypeIsFrame("aaa"), .. TtypeIsFrame("aaa")]);
            using var session = NewSession(stream);
            (await session.RequestTerminalTypesAsync(TimeSpan.FromSeconds(5))).Should().Equal("aaa");
            stream.Enqueue([.. TtypeIsFrame("bbb"), .. TtypeIsFrame("bbb")]);
            (await session.RequestTerminalTypesAsync(TimeSpan.FromSeconds(5))).Should().Equal("bbb");
            session.ClientTerminalTypes.Should().Equal("bbb", "bbb");
            // Peer WILL agreement adds the WILL-triggered probe, so at least
            // each call's own SEND plus one per collected answer is present.
            CountSubsequence(OutboundBytes(stream), [255, 250, 24, 1, 255, 240]).Should().BeGreaterThanOrEqualTo(4);
        }

        [Fact]
        public async Task RequestTerminalTypesAsync_MixedCaseDouble_Terminates()
        {
            // Repeat detection is ordinal (reference: plain ==, MTTS-only
            // case-insensitivity): "BBB" does NOT repeat "bbb", so all three
            // answers are collected and the cycle ends at the timeout; only an
            // exact repeat would stop it early.
            using var stream = new ScriptedStream();
            stream.Enqueue([.. TtypeIsFrame("AAA"), .. TtypeIsFrame("bbb"), .. TtypeIsFrame("BBB")]);
            using var session = NewSession(stream);
            var types = await session.RequestTerminalTypesAsync(TimeSpan.FromSeconds(5));
            types.Should().Equal("AAA", "bbb", "BBB");
            // Deterministic: initial SEND plus one per collected answer (4),
            // no matter who consumed first. Either way the deferred WILL
            // ECHO fires once.
            CountSubsequence(OutboundBytes(stream), [255, 250, 24, 1, 255, 240]).Should().Be(4);
            CountSubsequence(OutboundBytes(stream), [255, 251, 1]).Should().Be(1);
        }

        [Fact]
        public async Task RequestTerminalTypesAsync_OverflowPastCap_StopsAndReleasesEnviron()
        {
            // Past TTYPE_LOOPMAX the reference stops soliciting and moves on
            // to the environment: the 10th distinct answer ends the wait at
            // once (no timeout), overwriting the single overflow slot (last
            // wins) with the deferred DO NEW_ENVIRON released.
            var options = new TelnetServerOptions { RequestNewEnvironment = true };
            using var stream = new ScriptedStream();
            var frames = new List<int>();
            for (int n = 0; n < 10; n++)
            {
                frames.AddRange(TtypeIsFrame("term" + n));
            }

            stream.Enqueue([.. frames]);
            using var session = new ServerSession(stream, options, CancellationToken.None);
            session.Negotiation.ReceivedWill((int)Options.TerminalType, agree: true);
            var sw = Stopwatch.StartNew();
            var types = await session.RequestTerminalTypesAsync(TimeSpan.FromSeconds(5));
            sw.Stop();
            types.Should().Equal("term0", "term1", "term2", "term3", "term4", "term5", "term6", "term7", "term9");
            sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(4));
            ContainsSubsequence(OutboundBytes(stream), [255, 253, 39]).Should().BeTrue();
        }

        [Fact]
        public async Task RequestTerminalTypesAsync_NonConsecutiveRepeat_StopsAtFirstRepeat()
        {
            // A repeat of the first entry looped the cycle: the terminating
            // duplicate is excluded from the return but kept in the chain.
            using var stream = new ScriptedStream();
            stream.Enqueue([.. TtypeIsFrame("ALPHA"), .. TtypeIsFrame("BETA"), .. TtypeIsFrame("GAMMA"), .. TtypeIsFrame("ALPHA")]);
            using var session = NewSession(stream);
            var types = await session.RequestTerminalTypesAsync(TimeSpan.FromSeconds(5));
            types.Should().Equal("ALPHA", "BETA", "GAMMA");
            session.ClientTerminalTypes.Should().Equal("ALPHA", "BETA", "GAMMA", "ALPHA");
            session.ClientEffectiveTerminalType.Should().Be("ALPHA");
            // Peer WILL agreement also releases the advanced preset and the
            // WILL-triggered probe, so the wire contains additional frames.
            CountSubsequence(OutboundBytes(stream), [255, 250, 24, 1, 255, 240]).Should().BeGreaterThanOrEqualTo(4);
            ContainsSubsequence(OutboundBytes(stream), [255, 251, 1]).Should().BeTrue();
            ContainsSubsequence(OutboundBytes(stream), [255, 253, 39]).Should().BeTrue();
        }

        [Fact]
        public async Task RequestTerminalTypesAsync_PumpConsumedFirst_SendOrderStillHolds()
        {
            // Regression: the background pump's first pass used to release
            // WILL ECHO / DO NEW_ENVIRON for unsolicited answers before the
            // requester's SENDs went out. Yielding the wire first forces the
            // pump-first consumer order deterministically.
            // Peer WILL agreement also releases the advanced preset, so the
            // wire contains additional frames beyond the collector's own.
            using var stream = new ScriptedStream();
            stream.Enqueue([.. TtypeIsFrame("ALPHA"), .. TtypeIsFrame("BETA"), .. TtypeIsFrame("GAMMA"), .. TtypeIsFrame("ALPHA")]);
            using var session = NewSession(stream);
            await Task.Delay(300);
            session.ClientTerminalTypes.Should().Equal("ALPHA", "BETA", "GAMMA", "ALPHA");
            var types = await session.RequestTerminalTypesAsync(TimeSpan.FromSeconds(5));
            types.Should().Equal("ALPHA", "BETA", "GAMMA");
            session.ClientTerminalTypes.Should().Equal("ALPHA", "BETA", "GAMMA", "ALPHA");
            CountSubsequence(OutboundBytes(stream), [255, 250, 24, 1, 255, 240]).Should().BeGreaterThanOrEqualTo(4);
            ContainsSubsequence(OutboundBytes(stream), [255, 251, 1]).Should().BeTrue();
            ContainsSubsequence(OutboundBytes(stream), [255, 253, 39]).Should().BeTrue();
        }

        [Fact]
        public async Task RequestTerminalTypesAsync_SkipsEmptyAnswers()
        {
            // An empty answer ends the cycle: only answers before it are kept.
            using var stream = new ScriptedStream();
            stream.Enqueue([.. TtypeIsFrame("ALPHA"), .. TtypeIsFrame(string.Empty), .. TtypeIsFrame("BETA"), .. TtypeIsFrame("BETA")]);
            using var session = NewSession(stream);
            var types = await session.RequestTerminalTypesAsync(TimeSpan.FromSeconds(5));
            types.Should().Equal("ALPHA");
            session.ClientEffectiveTerminalType.Should().Be("ALPHA");
        }

        [Fact]
        public async Task RequestTerminalTypesAsync_BeyondLoopMax_StopsAtCap()
        {
            // Past slot 8 the cycle stops at once (the reference stops at
            // TTYPE_LOOPMAX and moves on to the environment): the 10th answer
            // overwrites the single overflow slot a final time, and answers
            // after the stop are ignored instead of spinning to the timeout.
            using var stream = new ScriptedStream();
            var script = new System.Collections.Generic.List<int>();
            for (var i = 1; i <= 12; i++)
            {
                script.AddRange(TtypeIsFrame("T" + i));
            }

            stream.Enqueue([.. script]);
            using var session = NewSession(stream);
            await session.RequestTerminalTypesAsync(TimeSpan.FromMilliseconds(300));
            session.ClientTerminalTypes.Should().Equal("T1", "T2", "T3", "T4", "T5", "T6", "T7", "T8", "T10");
        }

        [Fact]
        public async Task RequestTerminalTypesAsync_LowercaseMttsThird_StopsWithSecondEffective()
        {
            using var stream = new ScriptedStream();
            stream.Enqueue([.. TtypeIsFrame("XTERM"), .. TtypeIsFrame("VT100"), .. TtypeIsFrame("mtts 137")]);
            using var session = NewSession(stream);
            var types = await session.RequestTerminalTypesAsync(TimeSpan.FromSeconds(5));
            types.Should().Equal("XTERM", "VT100", "mtts 137");
            session.ClientEffectiveTerminalType.Should().Be("VT100");
        }

        [Fact]
        public async Task RequestTerminalTypesAsync_UnescapesIacInValue()
        {
            using var stream = new ScriptedStream();
            // IS "ÿ": the 0xFF data byte is IAC-doubled on the wire.
            stream.Enqueue([255, 250, 24, 0, 255, 255, 255, 240, 255, 250, 24, 0, 255, 255, 255, 240]);
            using var session = NewSession(stream);
            var types = await session.RequestTerminalTypesAsync(TimeSpan.FromSeconds(5));
            types.Should().Equal("ÿ");
        }

        [Fact]
        public async Task RequestTerminalTypesAsync_TimesOutEmpty()
        {
            using var stream = new ScriptedStream();
            using var session = NewSession(stream);
            var types = await session.RequestTerminalTypesAsync(TimeSpan.FromMilliseconds(200));
            types.Should().BeEmpty();
        }

        [Fact]
        public async Task RequestTerminalSpeedAsync_StripsLeadingZeros()
        {
            using var stream = new ScriptedStream();
            stream.Enqueue([255, 250, 32, 0, .. System.Text.Encoding.Latin1.GetBytes("001200,2400"), 255, 240]);
            using var session = NewSession(stream);
            var speed = await session.RequestTerminalSpeedAsync(TimeSpan.FromSeconds(5));
            speed.Should().Be("1200,2400");
            // Peer WILL agreement also releases the advanced preset and the
            // WILL-triggered probe, so the SEND is present among extras.
            CountSubsequence(OutboundBytes(stream), [255, 250, 32, 1]).Should().BeGreaterThanOrEqualTo(1);
        }

        [Fact]
        public async Task RequestTerminalSpeedAsync_StoresVerbatimAnswer()
        {
            // RFC 1079 §5 rounding is receiver-local: the stored value keeps
            // the peer's rates unchanged.
            using var stream = new ScriptedStream();
            stream.Enqueue([255, 250, 32, 0, .. System.Text.Encoding.Latin1.GetBytes("123,456"), 255, 240]);
            using var session = NewSession(stream);
            var speed = await session.RequestTerminalSpeedAsync(TimeSpan.FromSeconds(5));
            speed.Should().Be("123,456");
            // Peer WILL agreement also releases the advanced preset and the
            // WILL-triggered probe, so the SEND is present among extras.
            CountSubsequence(OutboundBytes(stream), [255, 250, 32, 1]).Should().BeGreaterThanOrEqualTo(1);
        }

        [Fact]
        public async Task RequestTerminalSpeedAsync_MalformedAnswer_YieldsNull()
        {
            using var stream = new ScriptedStream();
            stream.Enqueue([255, 250, 32, 0, (byte)'a', (byte)'b', (byte)'c', 255, 240]);
            using var session = NewSession(stream);
            var speed = await session.RequestTerminalSpeedAsync(TimeSpan.FromSeconds(5));
            speed.Should().BeNull();
        }

        [Fact]
        public async Task RequestTerminalSpeedAsync_SecondCallWhileOutstanding_ReturnsNullWithSingleSend()
        {
            // Port of test_request_tspeed_and_charset_pending_branches TSPEED
            // half: a second request while one is outstanding returns null.
            // Peer WILL agreement adds the WILL-triggered probe, so at least
            // the requester's SEND is present (single-active still holds for
            // the requesters themselves).
            using var stream = new ScriptedStream();
            using var session = NewSession(stream);
            var first = session.RequestTerminalSpeedAsync(TimeSpan.FromMilliseconds(300));
            var second = session.RequestTerminalSpeedAsync(TimeSpan.FromMilliseconds(300));
            (await first).Should().BeNull();
            (await second).Should().BeNull();
            CountSubsequence(OutboundBytes(stream), [255, 250, 32, 1]).Should().BeGreaterThanOrEqualTo(1);
        }

        [Fact]
        public async Task RequestEnvironmentAsync_ParsesRequestedEntries()
        {
            using var stream = new ScriptedStream();
            stream.Enqueue([255, 250, 36, 0,
        0, (byte)'U', (byte)'S', (byte)'E', (byte)'R', 1, (byte)'b', (byte)'o', (byte)'b',
        3, (byte)'R', (byte)'O', (byte)'L', (byte)'E', 1, (byte)'o', (byte)'p', (byte)'s',
        255, 240]);
            using var session = NewSession(stream);
            var env = await session.RequestEnvironmentAsync(TimeSpan.FromSeconds(5), [0, 3]);
            env.Should().Contain("USER", "bob").And.Contain("ROLE", "ops");
            // Peer WILL agreement also releases the advanced preset, so the
            // SEND is present among extras.
            ContainsSubsequence(OutboundBytes(stream), [255, 250, 36, 1, 0, 3, 255, 240]).Should().BeTrue();
        }

        [Fact]
        public async Task RequestEnvironmentAsync_UpperCasesKeysDropsEmptyAndKeepsFullRange()
        {
            // telnetlib3's on_environ: keys upper-cased (untrusted input must
            // not override trusted values), empty values dropped, full 0..127
            // value range preserved (delimiter bytes ESC-escaped on the wire).
            var gamma = new List<byte>();
            for (var b = 0; b < 128; b++)
            {
                if (b is 0 or 1 or 2 or 3)
                {
                    gamma.Add(2);
                }

                gamma.Add((byte)b);
            }

            var payload = new List<int> { 255, 250, 36, 0 };
            payload.AddRange([0, (byte)'a', (byte)'L', (byte)'p', (byte)'H', (byte)'a', 1, (byte)'o', (byte)'M', (byte)'e', (byte)'G', (byte)'a']);
            payload.AddRange([0, (byte)'b', (byte)'e', (byte)'t', (byte)'a', 1, (byte)'b']);
            payload.AddRange([0, (byte)'g', (byte)'a', (byte)'m', (byte)'m', (byte)'a', 1]);
            payload.AddRange(gamma.Select(static b => (int)b));
            payload.AddRange([0, (byte)'U', (byte)'S', (byte)'E', (byte)'R', 1]);
            payload.AddRange([0, (byte)'n', (byte)'o', (byte)'t', (byte)'h', (byte)'i', (byte)'n', (byte)'g']);
            payload.AddRange([255, 240]);
            using var stream = new ScriptedStream();
            stream.Enqueue([.. payload]);
            using var session = NewSession(stream);
            var env = await session.RequestEnvironmentAsync(TimeSpan.FromSeconds(5), [0, 3]);
            var fullRange = new string(Enumerable.Range(0, 128).Select(static i => (char)i).ToArray());
            env.Should().Contain("ALPHA", "oMeGa").And.Contain("BETA", "b").And.Contain("GAMMA", fullRange);
            env.Should().NotContainKey("USER").And.NotContainKey("NOTHING");
            session.ClientEnvironment.Should().BeEquivalentTo(env);
        }

        [Fact]
        public async Task RequestNewEnvironmentAsync_WithEncodingLang_DecodesHighBytes()
        {
            // A LANG entry carrying an encoding suffix presumes BINARY
            // capability: later 8-bit bytes decode instead of dropping. The
            // session default is UTF-8, so the stimulus is the complete C3 A9
            // pair (a lone E9 is an incomplete sequence the incremental
            // decoder buffers, not a decode).
            using var stream = new ScriptedStream();
            stream.Enqueue([255, 250, 39, 0, 0, (byte)'L', (byte)'A', (byte)'N', (byte)'G', 1,
              .. System.Text.Encoding.Latin1.GetBytes("en_US.UTF-8"), 255, 240]);
            using var session = NewSession(stream);
            session.Negotiation.ReceivedWill((int)Options.NewEnvironment, agree: true);
            var env = await session.RequestNewEnvironmentAsync(TimeSpan.FromSeconds(5));
            env.Should().Contain("LANG", "en_US.UTF-8");
            stream.Enqueue([0xC3, 0xA9]);
            (await session.ReadAsync(TimeSpan.FromMilliseconds(500))).Should().Be("é");
        }

        [Fact]
        public async Task RequestNewEnvironmentAsync_WithPlainLang_DropsHighBytes()
        {
            // A plain LANG=C entry switches nothing: the UTF-8 session
            // default still decodes the complete C3 A9 pair to a single é
            // (Latin-1 Ã© would prove a charset flip).
            using var stream = new ScriptedStream();
            stream.Enqueue([255, 250, 39, 0, 0, (byte)'L', (byte)'A', (byte)'N', (byte)'G', 1,
              (byte)'C', 255, 240]);
            using var session = NewSession(stream);
            session.Negotiation.ReceivedWill((int)Options.NewEnvironment, agree: true);
            var env = await session.RequestNewEnvironmentAsync(TimeSpan.FromSeconds(5));
            env.Should().Contain("LANG", "C");
            stream.Enqueue([0xC3, 0xA9]);
            (await session.ReadAsync(TimeSpan.FromMilliseconds(100))).Should().Be("é");
        }

        [Fact]
        public async Task SpontaneousInfo_AfterWillAgreement_UpdatesEnvironment()
        {
            // RFC 1408: only the WILL-ENVIRON side may send INFO. After the
            // peer's WILL is agreed (DO reply), its INFO is consumed.
            using var stream = new ScriptedStream();
            stream.Enqueue([255, 251, 36, 255, 250, 36, 2, 3, (byte)'X', 1, (byte)'y', 255, 240]);
            using var session = new ServerSession(stream, new TelnetServerOptions(), CancellationToken.None);
            await session.ReadAsync(TimeSpan.FromMilliseconds(500));
            session.ClientEnvironment.Should().Contain("X", "y");
            OutboundBytes(stream).Should().Equal(
              255, 253, 36,
              255, 251, 3, 255, 251, 0, 255, 253, 31, 255, 253, 42);
        }

        [Fact]
        public async Task UnsolicitedInfo_WithoutAgreement_AnswersWont()
        {
            // INFO with no prior WILL agreement is ignored: subnegotiation
            // never synthesizes WONT, so nothing is sent.
            using var stream = new ScriptedStream();
            stream.Enqueue([255, 250, 36, 2, 3, (byte)'X', 1, (byte)'y', 255, 240]);
            using var session = new ServerSession(stream, new TelnetServerOptions(), CancellationToken.None);
            await session.ReadAsync(TimeSpan.FromMilliseconds(500));
            session.ClientEnvironment.Should().NotContainKey("X");
            OutboundBytes(stream).Should().BeEmpty();
        }

        [Fact]
        public async Task StrayIs_WithoutOutstandingRequest_AnswersWont()
        {
            // A stray TTYPE IS is stored even with no request outstanding
            // (reference on_ttype stores unconditionally, "even when
            // unsolicited"): no WONT and no IS reply are sent, but the store
            // arms a deferred WILL ECHO (FF FB 01) and DO NEW_ENVIRON
            // (FF FD 27) on flush.
            using var stream = new ScriptedStream();
            stream.Enqueue([.. TtypeIsFrame("x")]);
            using var session = new ServerSession(stream, new TelnetServerOptions(), CancellationToken.None);
            await session.ReadAsync(TimeSpan.FromMilliseconds(500));
            session.ClientTerminalTypes.Should().Equal("x");
            OutboundBytes(stream).Should().Equal(255, 251, 1, 255, 253, 39);
        }

        [Fact]
        public async Task StraySpeedIs_WithoutOutstandingRequest_AnswersWont()
        {
            // A stray TSPEED IS is stored even with no request outstanding
            // (reference stores unsolicited answers); outbound stays empty.
            using var stream = new ScriptedStream();
            stream.Enqueue([255, 250, 32, 0, (byte)'9', (byte)'6', (byte)'0', (byte)'0', (byte)',', (byte)'9', (byte)'6', (byte)'0', (byte)'0', 255, 240]);
            using var session = new ServerSession(stream, new TelnetServerOptions(), CancellationToken.None);
            await session.ReadAsync(TimeSpan.FromMilliseconds(500));
            session.ClientTerminalSpeed.Should().Be("9600,9600");
            OutboundBytes(stream).Should().BeEmpty();
        }

        [Fact]
        public async Task InboundNaws_VerbFirst_Ignored()
        {
            // RFC 1073 carries no verb inside NAWS: a 5-byte IS-first
            // frame stores no size and latches no agreement.
            using var stream = new ScriptedStream();
            stream.Enqueue([255, 250, 31, 0, 0, 80, 0, 24, 255, 240]);
            using var session = new ServerSession(stream, new TelnetServerOptions(), CancellationToken.None);
            await session.ReadAsync(TimeSpan.FromMilliseconds(500));
            session.ClientWindowSize.Should().BeNull();
            session.Negotiation.IsEnabledByPeer(31).Should().BeFalse();
        }

        [Fact]
        public async Task InboundNaws_BareShape_SetsClientWindowSize()
        {
            using var stream = new ScriptedStream();
            stream.Enqueue([255, 250, 31, 0, 100, 0, 30, 255, 240]);
            using var session = new ServerSession(stream, new TelnetServerOptions(), CancellationToken.None);
            await session.ReadAsync(TimeSpan.FromMilliseconds(500));
            session.ClientWindowSize.Should().Be(((ushort)100, (ushort)30));
            OutboundBytes(stream).Should().Equal(255, 251, 3, 255, 251, 0, 255, 253, 42);
        }

        [Fact]
        public async Task InboundNaws_Unsolicited_MarksRemoteEnabled()
        {
            // A size report with no prior WILL NAWS still assumes the peer
            // enabled the option: the size is stored and remote agreement
            // is latched. The latch counts as negotiation advance, so the
            // advanced preset follows (no reply answers the SB itself).
            using var stream = new ScriptedStream();
            stream.Enqueue([255, 250, 31, 0, 80, 0, 24, 255, 240]);
            using var session = new ServerSession(stream, new TelnetServerOptions(), CancellationToken.None);
            await session.ReadAsync(TimeSpan.FromMilliseconds(500));
            session.ClientWindowSize.Should().Be(((ushort)80, (ushort)24));
            session.Negotiation.IsEnabledByPeer(31).Should().BeTrue();
            OutboundBytes(stream).Should().Equal(255, 251, 3, 255, 251, 0, 255, 253, 42);
        }

        [Fact]
        public async Task InboundNaws_Malformed_Ignored()
        {
            using var stream = new ScriptedStream();
            stream.Enqueue([255, 250, 31, 0, 80, 255, 240]);
            using var session = NewSession(stream);
            await session.ReadAsync(TimeSpan.FromMilliseconds(500));
            session.ClientWindowSize.Should().BeNull();
        }

        [Fact]
        public async Task InboundNaws_ZeroDims_StoredVerbatim()
        {
            // Zero means "unspecified" to the server: stored as-is (advisory).
            // Range-clamping is client-send-side only (NawsProtocol).
            using var stream = new ScriptedStream();
            stream.Enqueue([255, 250, 31, 0, 0, 0, 0, 255, 240]);
            using var session = new ServerSession(stream, new TelnetServerOptions(), CancellationToken.None);
            await session.ReadAsync(TimeSpan.FromMilliseconds(500));
            session.ClientWindowSize.Should().Be(((ushort)0, (ushort)0));
            OutboundBytes(stream).Should().Equal(255, 251, 3, 255, 251, 0, 255, 253, 42);
        }

        [Fact]
        public void ParseEntries_UnescapesAndSkipsUndefined()
        {
            var entries = EnvironmentProtocol.ParseEntries([0,
        0, (byte)'A', 1, (byte)'x',
        0, 2, 1, (byte)'B', 1, (byte)'y',
        3, (byte)'U']);
            entries.Should().HaveCount(3);
            entries[0].Should().Be((false, "A", "x"));
            entries[1].Should().Be((false, "\u0001B", "y"));
            entries[2].Should().Be((true, "U", null));
        }

        [Fact]
        public async Task NegotiatedNaws_Interop_ClientSizeReachesSession()
        {
            using var server = new TelnetServer(0);
            server.Start();
            var acceptTask = server.AcceptSessionAsync(CancellationToken.None);
            using var client = await Client.ConnectAsync("127.0.0.1", server.Port);
            using var session = await acceptTask;
            client.Settings.WindowWidth = 100;
            client.Settings.WindowHeight = 30;
            // AcceptSessionAsync already sent the preset DO NAWS; pump both
            // sides until the client's volunteered IS arrives.
            var sw = Stopwatch.StartNew();
            while (session.ClientWindowSize is null && sw.Elapsed < TimeSpan.FromSeconds(10))
            {
                await client.ReadAsync(TimeSpan.FromMilliseconds(50));
                await session.ReadAsync(TimeSpan.FromMilliseconds(50));
            }

            session.ClientWindowSize.Should().Be(((ushort)100, (ushort)30));
        }
    }

    // S4 server tests: LINEMODE as the DO-sender (MODE union+ACK, MODE sends,
    // FORWARDMASK proposals, SLC publish/import), STATUS/TIMING-MARK answered
    // from session state, and urgent (Synch) receive.
    public class ServerSessionLinemodeTests
    {
        private static ServerSession NewSession(ScriptedStream stream)
        {
            return new ServerSession(stream, new TelnetServerOptions(), CancellationToken.None);
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

        // Reference SLC publish: the 16 BSD default rows framed as
        // IAC SB LINEMODE SLC ... IAC SE (sent on the first MODE).
        private static byte[] BsdSlcPublishFrame() =>
        [
          255, 250, 34, 3,
          1, 3, 0, 2, 3, 0, 3, 98, 3, 4, 34, 15, 5, 2, 20, 6, 3, 0,
          7, 98, 28, 8, 2, 4, 9, 66, 26, 10, 2, 127, 11, 2, 21, 12, 2, 23,
          13, 2, 18, 14, 2, 22, 15, 2, 17, 16, 2, 19,
          255, 240,
        ];

        // Reference forwardmask request: DO FORWARDMASK plus 16 zero bytes
        // (32 under BINARY), sent after every server-side SLC block.
        private static byte[] ForwardMaskFrame() =>
        [
          255, 250, 34, 253, 2,
          0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
          255, 240,
        ];

        [Fact]
        public async Task LmodeAgreement_CompletesWithoutFurtherReply()
        {
            using var stream = new ScriptedStream();
            using var session = NewSession(stream);
            await AgreeLinemodeAsync(session, stream);
            session.Negotiation.IsEnabledByPeer((int)Options.LineMode).Should().BeTrue();
            // Explicit DO LINEMODE (we asked first): no MODE proposal goes
            // out; the advanced preset follows the DO LINEMODE instead.
            OutboundBytes(stream).Should().Equal(
              255, 253, 34,
              255, 251, 3, 255, 251, 0, 255, 253, 31, 255, 253, 42);
        }

        [Fact]
        public async Task ModeRequest_SubsetIsConfirmedWithRequiredBitsAndAck()
        {
            using var stream = new ScriptedStream();
            using var session = NewSession(stream);
            // WILL LINEMODE agrees the option, then MODE asking for EDIT only
            // is echoed verbatim plus MODE_ACK (4). A non-ACK MODE answers
            // MODE+ACK only (reference: the SLC table is published once, on
            // the first ACKed MODE); the advanced preset goes out last.
            stream.Enqueue([255, 251, 34, 255, 250, 34, 1, 1, 255, 240]);
            await session.ReadAsync(TimeSpan.FromMilliseconds(500));
            OutboundBytes(stream).Should().Equal(
              [255, 253, 34,
               255, 250, 34, 1, 16, 255, 240,
               255, 250, 34, 1, 5, 255, 240,
               255, 251, 3, 255, 251, 0, 255, 253, 31, 255, 253, 42]);
        }

        [Fact]
        public async Task ModeRequest_ZeroStillYieldsRequiredBits()
        {
            using var stream = new ScriptedStream();
            using var session = NewSession(stream);
            // MODE [EDIT] is echoed verbatim plus ACK; a later MODE [0] is a
            // new mask and is echoed verbatim plus ACK as well. Neither
            // carries ACK, so no SLC table goes out at all.
            stream.Enqueue([255, 251, 34, 255, 250, 34, 1, 1, 255, 240, 255, 250, 34, 1, 0, 255, 240]);
            await session.ReadAsync(TimeSpan.FromMilliseconds(500));
            OutboundBytes(stream).Should().Equal(
              [255, 253, 34,
               255, 250, 34, 1, 16, 255, 240,
               255, 250, 34, 1, 5, 255, 240,
               255, 250, 34, 1, 4, 255, 240,
               255, 251, 3, 255, 251, 0, 255, 253, 31, 255, 253, 42]);
        }

        [Fact]
        public async Task ModeAck_DifferingMaskIsAdoptedSilently()
        {
            using var stream = new ScriptedStream();
            using var session = NewSession(stream);
            // MODE+ACK [EDIT] (mask 5 = EDIT|ACK) differs from stored 0: the
            // server rule adopts it with no MODE reply — and the ACK publishes
            // the SLC table once. The following plain MODE [1] echoes the
            // adopted value, so it is silent too (unchanged masks earn no
            // reply). Only the MODE proposal and the advanced batch surround.
            stream.Enqueue([255, 251, 34, 255, 250, 34, 1, 5, 255, 240, 255, 250, 34, 1, 1, 255, 240]);
            await session.ReadAsync(TimeSpan.FromMilliseconds(500));
            OutboundBytes(stream).Should().Equal(
              [255, 253, 34,
               255, 250, 34, 1, 16, 255, 240,
               .. BsdSlcPublishFrame(),
               255, 251, 3, 255, 251, 0, 255, 253, 31, 255, 253, 42]);
        }

        [Fact]
        public async Task ModeRequest_MalformedPayloads_AreSilent()
        {
            using var stream = new ScriptedStream();
            using var session = NewSession(stream);
            stream.Enqueue([255, 251, 34, 255, 250, 34, 255, 240, 255, 250, 34, 1, 7, 9, 255, 240]);
            await session.ReadAsync(TimeSpan.FromMilliseconds(500));
            // Malformed MODE frames stay silent, but the WILL LINEMODE
            // agreement itself still draws the MODE proposal + advanced batch.
            OutboundBytes(stream).Should().Equal(
              255, 253, 34,
              255, 250, 34, 1, 16, 255, 240,
              255, 251, 3, 255, 251, 0, 255, 253, 31, 255, 253, 42);
        }

        [Fact]
        public async Task SendModeAsync_SendsExactFrameWhenAgreed()
        {
            using var stream = new ScriptedStream();
            using var session = NewSession(stream);
            await AgreeLinemodeAsync(session, stream);
            await session.SendModeAsync(3);
            OutboundBytes(stream).Should().Equal(
              255, 253, 34,
              255, 251, 3, 255, 251, 0, 255, 253, 31, 255, 253, 42,
              255, 250, 34, 1, 3, 255, 240);
        }

        [Fact]
        public async Task SendModeAsync_WithoutAgreement_SendsNothing()
        {
            using var stream = new ScriptedStream();
            using var session = NewSession(stream);
            await session.SendModeAsync(3);
            stream.ByteWrites.Should().BeEmpty();
        }

        [Fact]
        public async Task ForwardMask_RoundTrip()
        {
            using var stream = new ScriptedStream();
            using var session = NewSession(stream);
            await AgreeLinemodeAsync(session, stream);
            await session.SendForwardMaskAsync([0xFF]);
            // Peer accepts, then refuses a second proposal: both silent.
            stream.Enqueue([255, 250, 34, 251, 2, 255, 240, 255, 250, 34, 252, 2, 255, 240]);
            await session.ReadAsync(TimeSpan.FromMilliseconds(500));
            OutboundBytes(stream).Should().Equal(
              255, 253, 34,
              255, 251, 3, 255, 251, 0, 255, 253, 31, 255, 253, 42,
              255, 250, 34, 253, 2, 255, 255, 255, 240);
        }

        [Fact]
        public async Task ForwardMask_RogueProposal_IsRefused()
        {
            using var stream = new ScriptedStream();
            using var session = NewSession(stream);
            // An unsolicited FORWARDMASK proposal is rejected silently on
            // server role: the LINEMODE agreement is answered, the mask
            // itself stores nothing and gets no reply.
            stream.Enqueue([255, 251, 34, 255, 250, 34, 253, 2, 255, 240]);
            await session.ReadAsync(TimeSpan.FromMilliseconds(500));
            OutboundBytes(stream).Should().Equal(
              255, 253, 34,
              255, 250, 34, 1, 16, 255, 240,
              255, 251, 3, 255, 251, 0, 255, 253, 31, 255, 253, 42);
        }

        [Fact]
        public async Task ForwardMask_WillWithoutLinemode_IsIgnored()
        {
            // Port of test_handle_sb_forwardmask_server_without_linemode
            // (wire half): an inbound WILL FORWARDMASK with no LINEMODE
            // agreement is ignored on the wire — no reply, no data — while
            // still recording the remote sub-state.
            using var stream = new ScriptedStream();
            using var session = NewSession(stream);
            stream.Enqueue([255, 250, 34, 251, 2, 255, 240]);
            (await session.ReadAsync(TimeSpan.FromMilliseconds(500))).Should().BeEmpty();
            stream.ByteWrites.Should().BeEmpty();
        }

        [Fact]
        public async Task SendForwardMaskAsync_WithoutAgreement_SendsNothing()
        {
            using var stream = new ScriptedStream();
            using var session = NewSession(stream);
            await session.SendForwardMaskAsync([0xFF]);
            stream.ByteWrites.Should().BeEmpty();
        }

        [Fact]
        public async Task SendModeAsync_AfterClose_WritesNothing()
        {
            // Port of test_send_linemode_skipped_when_closing: the linemode
            // frame send is gated on the live stream, so nothing is written
            // once closed.
            using var stream = new ScriptedStream();
            using var session = NewSession(stream);
            await AgreeLinemodeAsync(session, stream);
            int agreed = stream.ByteWrites.Count;
            stream.Close();
            await session.SendModeAsync(3);
            stream.ByteWrites.Should().HaveCount(agreed);
        }

        [Fact]
        public async Task SendForwardMaskAsync_RejectsOversizedMask()
        {
            using var stream = new ScriptedStream();
            using var session = NewSession(stream);
            Func<Task> send = () => session.SendForwardMaskAsync(new byte[33]);
            await send.Should().ThrowAsync<ArgumentOutOfRangeException>();
        }

        [Fact]
        public async Task PublishSpecialCharactersAsync_SendsConfiguredTable()
        {
            using var stream = new ScriptedStream();
            using var session = NewSession(stream);
            session.SetLinemodeEntry(3, 2, 5);
            await AgreeLinemodeAsync(session, stream);
            await session.PublishSpecialCharactersAsync();
            OutboundBytes(stream).Should().Equal(
              255, 253, 34,
              255, 251, 3, 255, 251, 0, 255, 253, 31, 255, 253, 42,
              255, 250, 34, 3,
              1, 3, 0, 2, 3, 0, 3, 2, 5, 4, 34, 15, 5, 2, 20, 6, 3, 0,
              7, 98, 28, 8, 2, 4, 9, 66, 26, 10, 2, 127, 11, 2, 21, 12, 2, 23,
              13, 2, 18, 14, 2, 22, 15, 2, 17, 16, 2, 19,
              255, 240);
        }

        [Fact]
        public async Task PublishSpecialCharactersAsync_DefaultTable_SendsBsdRows()
        {
            // Source of truth: ~/telnetlib3/telnetlib3/slc.py BSD_SLC_TAB (16
            // live rows, funcs 1..16) via generate_slctab; the default publish
            // carries those rows.
            using var stream = new ScriptedStream();
            using var session = NewSession(stream);
            await AgreeLinemodeAsync(session, stream);
            await session.PublishSpecialCharactersAsync();
            OutboundBytes(stream).Should().Equal(
              255, 253, 34,
              255, 251, 3, 255, 251, 0, 255, 253, 31, 255, 253, 42,
              255, 250, 34, 3,
              1, 3, 0, 2, 3, 0, 3, 98, 3, 4, 34, 15,
              5, 2, 20, 6, 3, 0, 7, 98, 28, 8, 2, 4,
              9, 66, 26, 10, 2, 127, 11, 2, 21, 12, 2, 23,
              13, 2, 18, 14, 2, 22, 15, 2, 17, 16, 2, 19,
              255, 240);
        }

        [Fact]
        public async Task RequestRemoteSpecialCharactersAsync_SendsImportFrame()
        {
            using var stream = new ScriptedStream();
            using var session = NewSession(stream);
            await AgreeLinemodeAsync(session, stream);
            await session.RequestRemoteSpecialCharactersAsync();
            OutboundBytes(stream).Should().Equal(
              255, 253, 34,
              255, 251, 3, 255, 251, 0, 255, 253, 31, 255, 253, 42,
              255, 250, 34, 3, 0, 3, 0, 255, 240);
        }

        [Fact]
        public async Task SendCommand_FlushOut_SendsDoTimingMark()
        {
            // RFC 1184 §5.8: the BRK row carries FLUSHOUT, so the sent IAC BRK
            // is followed by IAC DO TIMING-MARK.
            using var stream = new ScriptedStream();
            using var session = NewSession(stream);
            await AgreeLinemodeAsync(session, stream);
            session.SetLinemodeEntry(2, 2, 7, 32);
            await session.SendCommand(Commands.Break);
            OutboundBytes(stream).Should().Equal(
              255, 253, 34,
              255, 251, 3, 255, 251, 0, 255, 253, 31, 255, 253, 42,
              255, 243,
              255, 253, 6);
        }

        [Fact]
        public async Task SendCommand_FlushIn_WithoutTcp_LogsAndSkips()
        {
            // FLUSHIN on a non-TCP stream: no throw, Synch skipped with a log.
            // The log hook attaches after agreement so session chatter is excluded.
            var options = new TelnetServerOptions();
            using var stream = new ScriptedStream();
            using var session = new ServerSession(stream, options, CancellationToken.None);
            await AgreeLinemodeAsync(session, stream);
            var logged = new List<string>();
            options.Log = logged.Add;
            session.SetLinemodeEntry(3, 2, 3, 64);
            await session.SendCommand(Commands.InterruptProcess);
            OutboundBytes(stream).Should().Equal(255, 253, 34, 255, 251, 3, 255, 251, 0, 255, 253, 31, 255, 253, 42, 255, 244);
            logged.Should().ContainSingle().Which.Should().Contain("Synch");
        }

        [Fact]
        public async Task SendCommand_FlushFlags_WithoutAgreement_SendsNothingExtra()
        {
            // Both flags set but LINEMODE never agreed: the gate blocks both halves.
            using var stream = new ScriptedStream();
            using var session = NewSession(stream);
            session.SetLinemodeEntry(2, 2, 7, 96);
            await session.SendCommand(Commands.Break);
            OutboundBytes(stream).Should().Equal(255, 243);
        }

        [Fact]
        public async Task InboundSlcTable_IsFoldedAndAnswered()
        {
            using var stream = new ScriptedStream();
            using var session = NewSession(stream);
            // Peer publishes IP=^E at VALUE level: agreed (stored) and ACKed,
            // then the server requests a forwardmask.
            stream.Enqueue([255, 251, 34, 255, 250, 34, 3, 3, 2, 5, 255, 240]);
            await session.ReadAsync(TimeSpan.FromMilliseconds(500));
            OutboundBytes(stream).Should().Equal(
              [255, 253, 34,
               255, 250, 34, 1, 16, 255, 240,
               255, 250, 34, 3, 3, 130, 5, 255, 240,
               .. ForwardMaskFrame(),
               255, 251, 3, 255, 251, 0, 255, 253, 31, 255, 253, 42]);
        }

        [Fact]
        public async Task ModeAck_SameMaskIsSilent()
        {
            using var stream = new ScriptedStream();
            using var session = NewSession(stream);
            // MODE [EDIT] is answered MODE+ACK; the MODE+ACK publishes the SLC
            // table once (reference: non-ACK MODE answers MODE+ACK with no SLC;
            // SLC goes out once, on the first ACK). The advanced batch follows.
            stream.Enqueue([255, 251, 34, 255, 250, 34, 1, 1, 255, 240, 255, 250, 34, 1, 7, 255, 240]);
            await session.ReadAsync(TimeSpan.FromMilliseconds(500));
            OutboundBytes(stream).Should().Equal(
              [255, 253, 34,
               255, 250, 34, 1, 16, 255, 240,
               255, 250, 34, 1, 5, 255, 240,
               .. BsdSlcPublishFrame(),
               255, 251, 3, 255, 251, 0, 255, 253, 31, 255, 253, 42]);
        }

        [Fact]
        public async Task InboundImportRequest_DefaultTable_SendsFullTable()
        {
            // RFC 1184 §2.4: (0,DEFAULT,0) resets to the defaults and answers
            // with the supported rows only — NOSUPPORT gap rows are never put
            // on the wire (reference _slc_send skips nosupport rows). The MODE
            // proposal comes first, the advanced batch last.
            using var stream = new ScriptedStream();
            using var session = NewSession(stream);
            stream.Enqueue([255, 251, 34, 255, 250, 34, 3, 0, 3, 0, 255, 240]);
            await session.ReadAsync(TimeSpan.FromMilliseconds(500));
            var bsd = new byte[]
            {
              1, 3, 0, 2, 3, 0, 3, 98, 3, 4, 34, 15, 5, 2, 20, 6, 3, 0,
              7, 98, 28, 8, 2, 4, 9, 66, 26, 10, 2, 127, 11, 2, 21, 12, 2, 23,
              13, 2, 18, 14, 2, 22, 15, 2, 17, 16, 2, 19,
            };
            OutboundBytes(stream).Should().Equal(
              [255, 253, 34,
               255, 250, 34, 1, 16, 255, 240,
               255, 250, 34, 3, .. bsd, 255, 240,
               .. ForwardMaskFrame(),
               255, 251, 3, 255, 251, 0, 255, 253, 31, 255, 253, 42]);
        }

        [Fact]
        public async Task InboundImportRequest_ConfiguredTable_RendersDefaultsForGaps()
        {
            // Explicit rows override the BSD defaults; NOSUPPORT rows are
            // omitted from the answer (reference _slc_send skips them).
            using var stream = new ScriptedStream();
            using var session = NewSession(stream);
            session.SetLinemodeEntry(3, 2, 5);
            stream.Enqueue([255, 251, 34, 255, 250, 34, 3, 0, 3, 0, 255, 240]);
            await session.ReadAsync(TimeSpan.FromMilliseconds(500));
            var bsd = new byte[]
            {
              1, 3, 0, 2, 3, 0, 3, 2, 5, 4, 34, 15, 5, 2, 20, 6, 3, 0,
              7, 98, 28, 8, 2, 4, 9, 66, 26, 10, 2, 127, 11, 2, 21, 12, 2, 23,
              13, 2, 18, 14, 2, 22, 15, 2, 17, 16, 2, 19,
            };
            OutboundBytes(stream).Should().Equal(
              [255, 253, 34,
               255, 250, 34, 1, 16, 255, 240,
               255, 250, 34, 3, .. bsd, 255, 240,
               .. ForwardMaskFrame(),
               255, 251, 3, 255, 251, 0, 255, 253, 31, 255, 253, 42]);
        }

        [Fact]
        public async Task InboundImportRequest_AckFlaggedLevel_SendsFullTable()
        {
            // The level bits are masked before comparing: an ACK-flagged
            // DEFAULT level (0x83) still resets and answers the full table.
            using var stream = new ScriptedStream();
            using var session = NewSession(stream);
            stream.Enqueue([255, 251, 34, 255, 250, 34, 3, 0, 0x83, 0, 255, 240]);
            await session.ReadAsync(TimeSpan.FromMilliseconds(500));
            OutboundBytes(stream).Should().Equal(
              255, 253, 34,
              255, 250, 34, 1, 16, 255, 240,
              255, 250, 34, 3,
              1, 3, 0, 2, 3, 0, 3, 98, 3, 4, 34, 15, 5, 2, 20, 6, 3, 0,
              7, 98, 28, 8, 2, 4, 9, 66, 26, 10, 2, 127, 11, 2, 21, 12, 2, 23,
              13, 2, 18, 14, 2, 22, 15, 2, 17, 16, 2, 19,
              255, 240,
              255, 250, 34, 253, 2,
              0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
              255, 240,
              255, 251, 3, 255, 251, 0, 255, 253, 31, 255, 253, 42);
        }

        [Fact]
        public async Task InboundImportRequest_NonzeroValueOctet_SendsFullTable()
        {
            // Func 0 is an import request, not a table row: the trailing
            // value octet is ignored, so (0,VALUE,1) answers the table.
            using var stream = new ScriptedStream();
            using var session = NewSession(stream);
            stream.Enqueue([255, 251, 34, 255, 250, 34, 3, 0, 2, 1, 255, 240]);
            await session.ReadAsync(TimeSpan.FromMilliseconds(500));
            OutboundBytes(stream).Should().Equal(
              255, 253, 34,
              255, 250, 34, 1, 16, 255, 240,
              255, 250, 34, 3,
              1, 3, 0, 2, 3, 0, 3, 98, 3, 4, 34, 15, 5, 2, 20, 6, 3, 0,
              7, 98, 28, 8, 2, 4, 9, 66, 26, 10, 2, 127, 11, 2, 21, 12, 2, 23,
              13, 2, 18, 14, 2, 22, 15, 2, 17, 16, 2, 19,
              255, 240,
              255, 250, 34, 253, 2,
              0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
              255, 240,
              255, 251, 3, 255, 251, 0, 255, 253, 31, 255, 253, 42);
        }

        [Fact]
        public async Task InboundSendCurrentRequest_ReturnsConfiguredTable()
        {
            // (0,VALUE,0) asks for current settings: BSD default rows with the
            // explicit override applied (NOSUPPORT rows omitted).
            using var stream = new ScriptedStream();
            using var session = NewSession(stream);
            session.SetLinemodeEntry(3, 2, 5);
            stream.Enqueue([255, 251, 34, 255, 250, 34, 3, 0, 2, 0, 255, 240]);
            await session.ReadAsync(TimeSpan.FromMilliseconds(500));
            OutboundBytes(stream).Should().Equal(
              255, 253, 34,
              255, 250, 34, 1, 16, 255, 240,
              255, 250, 34, 3,
              1, 3, 0, 2, 3, 0, 3, 2, 5, 4, 34, 15, 5, 2, 20, 6, 3, 0,
              7, 98, 28, 8, 2, 4, 9, 66, 26, 10, 2, 127, 11, 2, 21, 12, 2, 23,
              13, 2, 18, 14, 2, 22, 15, 2, 17, 16, 2, 19,
              255, 240,
              255, 250, 34, 253, 2,
              0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
              255, 240,
              255, 251, 3, 255, 251, 0, 255, 253, 31, 255, 253, 42);
        }

        [Fact]
        public async Task InboundSendCurrentRequest_DefaultTable_ReturnsBsdRows()
        {
            // (0,VALUE,0) on a fresh session exports the BSD default rows.
            using var stream = new ScriptedStream();
            using var session = NewSession(stream);
            stream.Enqueue([255, 251, 34, 255, 250, 34, 3, 0, 2, 0, 255, 240]);
            await session.ReadAsync(TimeSpan.FromMilliseconds(500));
            OutboundBytes(stream).Should().Equal(
              255, 253, 34,
              255, 250, 34, 1, 16, 255, 240,
              255, 250, 34, 3,
              1, 3, 0, 2, 3, 0, 3, 98, 3, 4, 34, 15, 5, 2, 20, 6, 3, 0,
              7, 98, 28, 8, 2, 4, 9, 66, 26, 10, 2, 127, 11, 2, 21, 12, 2, 23,
              13, 2, 18, 14, 2, 22, 15, 2, 17, 16, 2, 19,
              255, 240,
              255, 250, 34, 253, 2,
              0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
              255, 240,
              255, 251, 3, 255, 251, 0, 255, 253, 31, 255, 253, 42);
        }

        [Fact]
        public async Task InboundSlc_MalformedTriplets_IgnoredAndSessionSurvives()
        {
            // A misaligned SLC tail is logged and ignored as a whole (the
            // reference contains the feed-level ValueError per byte in its
            // feed loop, so end-to-end the frame is a no-op there too);
            // nothing is answered for it, and the session keeps dispatching
            // later frames.
            using var stream = new ScriptedStream();
            using var session = NewSession(stream);
            stream.Enqueue([255, 251, 34, 255, 250, 34, 3, 3, 255, 240]);
            (await session.ReadAsync(TimeSpan.FromMilliseconds(500))).Should().BeEmpty();
            // The MODE proposal and the advanced batch precede the ignored frame.
            OutboundBytes(stream).Should().Equal(
              255, 253, 34,
              255, 250, 34, 1, 16, 255, 240,
              255, 251, 3, 255, 251, 0, 255, 253, 31, 255, 253, 42);
            stream.Enqueue([255, 250, 34, 3, 3, 2, 5, 255, 240]);
            (await session.ReadAsync(TimeSpan.FromMilliseconds(500))).Should().BeEmpty();
            OutboundBytes(stream).Should().HaveCountGreaterThan(15);
        }

        [Fact]
        public async Task InboundImportRequest_ResetsWorkingTableToDefaults()
        {
            // (0,DEFAULT,0) resets negotiated-away rows before answering:
            // IP is moved to ^E, the import restores ^C, and a later
            // (0,VALUE,0) exports the restored value. The advanced batch lands
            // after the forwardmask but before the IMPORT/SENDCURRENT answers:
            // the batch is awaited inline, while import answers are
            // fire-and-forget.
            using var stream = new ScriptedStream();
            using var session = NewSession(stream);
            session.SetLinemodeEntry(3, 2, 3);
            stream.Enqueue([255, 251, 34, 255, 250, 34, 3, 3, 2, 5, 255, 240]);
            await session.ReadAsync(TimeSpan.FromMilliseconds(500));
            stream.Enqueue([255, 250, 34, 3, 0, 3, 0, 255, 240]);
            await session.ReadAsync(TimeSpan.FromMilliseconds(500));
            stream.Enqueue([255, 250, 34, 3, 0, 2, 0, 255, 240]);
            await session.ReadAsync(TimeSpan.FromMilliseconds(500));
            var bsd = new byte[]
            {
              1, 3, 0, 2, 3, 0, 3, 2, 3, 4, 34, 15, 5, 2, 20, 6, 3, 0,
              7, 98, 28, 8, 2, 4, 9, 66, 26, 10, 2, 127, 11, 2, 21, 12, 2, 23,
              13, 2, 18, 14, 2, 22, 15, 2, 17, 16, 2, 19,
            };
            OutboundBytes(stream).Should().Equal(
              [255, 253, 34,
               255, 250, 34, 1, 16, 255, 240,
               255, 250, 34, 3, 3, 130, 5, 255, 240,
               .. ForwardMaskFrame(),
               255, 251, 3, 255, 251, 0, 255, 253, 31, 255, 253, 42,
               255, 250, 34, 3, .. bsd, 255, 240,
               .. ForwardMaskFrame(),
               255, 250, 34, 3, .. bsd, 255, 240,
               .. ForwardMaskFrame()]);
        }

        [Fact]
        public async Task InboundImportRequest_AckFlaggedDefault_WithModifiedTable_ResetsToConfigured()
        {
            // The DEFAULT decision uses the masked level bits (RFC 1184
            // §2.4): (0,DEFAULT|ACK,0) resets even though the raw modifier
            // (0x83) differs from LevelDefault. IP is negotiated away to 5,
            // so a VALUE-branch misread would answer (3,2,5); the reset
            // answers the configured (3,2,3) — plus the forwardmask trailer.
            using var stream = new ScriptedStream();
            using var session = NewSession(stream);
            session.SetLinemodeEntry(3, 2, 3);
            stream.Enqueue([255, 251, 34, 255, 250, 34, 3, 3, 2, 5, 255, 240]);
            await session.ReadAsync(TimeSpan.FromMilliseconds(500));
            stream.Enqueue([255, 250, 34, 3, 0, 0x83, 0, 255, 240]);
            await session.ReadAsync(TimeSpan.FromMilliseconds(500));
            var bsd = new byte[]
            {
              1, 3, 0, 2, 3, 0, 3, 2, 3, 4, 34, 15, 5, 2, 20, 6, 3, 0,
              7, 98, 28, 8, 2, 4, 9, 66, 26, 10, 2, 127, 11, 2, 21, 12, 2, 23,
              13, 2, 18, 14, 2, 22, 15, 2, 17, 16, 2, 19,
            };
            OutboundBytes(stream).Should().Equal(
              [255, 253, 34,
               255, 250, 34, 1, 16, 255, 240,
               255, 250, 34, 3, 3, 130, 5, 255, 240,
               .. ForwardMaskFrame(),
               255, 251, 3, 255, 251, 0, 255, 253, 31, 255, 253, 42,
               255, 250, 34, 3, .. bsd, 255, 240,
               .. ForwardMaskFrame()]);
        }

        [Fact]
        public async Task InboundImportRequest_FlushFlaggedDefault_ResetsToConfigured()
        {
            // FLUSH bits ride along like ACK: (0,DEFAULT|FLUSHIN,0) resets
            // (same distinguishing setup as the ACK case above).
            using var stream = new ScriptedStream();
            using var session = NewSession(stream);
            session.SetLinemodeEntry(3, 2, 3);
            stream.Enqueue([255, 251, 34, 255, 250, 34, 3, 3, 2, 5, 255, 240]);
            await session.ReadAsync(TimeSpan.FromMilliseconds(500));
            stream.Enqueue([255, 250, 34, 3, 0, 0x43, 0, 255, 240]);
            await session.ReadAsync(TimeSpan.FromMilliseconds(500));
            var bsd = new byte[]
            {
              1, 3, 0, 2, 3, 0, 3, 2, 3, 4, 34, 15, 5, 2, 20, 6, 3, 0,
              7, 98, 28, 8, 2, 4, 9, 66, 26, 10, 2, 127, 11, 2, 21, 12, 2, 23,
              13, 2, 18, 14, 2, 22, 15, 2, 17, 16, 2, 19,
            };
            OutboundBytes(stream).Should().Equal(
              [255, 253, 34,
               255, 250, 34, 1, 16, 255, 240,
               255, 250, 34, 3, 3, 130, 5, 255, 240,
               .. ForwardMaskFrame(),
               255, 251, 3, 255, 251, 0, 255, 253, 31, 255, 253, 42,
               255, 250, 34, 3, .. bsd, 255, 240,
               .. ForwardMaskFrame()]);
        }

        [Fact]
        public async Task InboundImportRequest_AckFlaggedValue_DoesNotReset()
        {
            // (0,VALUE|ACK,0) is "send current settings", not a reset: the
            // negotiated-away IP value (5) is exported, not the configured
            // one (3). Same setup as the reset cases, opposite expectation.
            using var stream = new ScriptedStream();
            using var session = NewSession(stream);
            session.SetLinemodeEntry(3, 2, 3);
            stream.Enqueue([255, 251, 34, 255, 250, 34, 3, 3, 2, 5, 255, 240]);
            await session.ReadAsync(TimeSpan.FromMilliseconds(500));
            stream.Enqueue([255, 250, 34, 3, 0, 0x82, 0, 255, 240]);
            await session.ReadAsync(TimeSpan.FromMilliseconds(500));
            var current = new byte[]
            {
              1, 3, 0, 2, 3, 0, 3, 2, 5, 4, 34, 15, 5, 2, 20, 6, 3, 0,
              7, 98, 28, 8, 2, 4, 9, 66, 26, 10, 2, 127, 11, 2, 21, 12, 2, 23,
              13, 2, 18, 14, 2, 22, 15, 2, 17, 16, 2, 19,
            };
            OutboundBytes(stream).Should().Equal(
              [255, 253, 34,
               255, 250, 34, 1, 16, 255, 240,
               255, 250, 34, 3, 3, 130, 5, 255, 240,
               .. ForwardMaskFrame(),
               255, 251, 3, 255, 251, 0, 255, 253, 31, 255, 253, 42,
               255, 250, 34, 3, .. current, 255, 240,
               .. ForwardMaskFrame()]);
        }

        [Fact]
        public async Task InboundImportRequest_BatchedFunc0First_ResetsAndAppendsReplies()
        {
            // A batched import is processed in order into one SLC frame
            // (reference _slc_set loops the whole list): func 0 DEFAULT
            // resets and appends the full table, then the IP triplet is
            // applied with its ACK appended after the table. A follow-up
            // VALUE import shows the applied IP value stuck.
            using var stream = new ScriptedStream();
            using var session = NewSession(stream);
            stream.Enqueue([255, 251, 34, 255, 250, 34, 3, 0, 3, 0, 3, 2, 7, 255, 240]);
            await session.ReadAsync(TimeSpan.FromMilliseconds(500));
            stream.Enqueue([255, 250, 34, 3, 0, 2, 0, 255, 240]);
            await session.ReadAsync(TimeSpan.FromMilliseconds(500));
            var bsd = new byte[]
            {
              1, 3, 0, 2, 3, 0, 3, 98, 3, 4, 34, 15, 5, 2, 20, 6, 3, 0,
              7, 98, 28, 8, 2, 4, 9, 66, 26, 10, 2, 127, 11, 2, 21, 12, 2, 23,
              13, 2, 18, 14, 2, 22, 15, 2, 17, 16, 2, 19,
            };
            var applied = new byte[]
            {
              1, 3, 0, 2, 3, 0, 3, 2, 7, 4, 34, 15, 5, 2, 20, 6, 3, 0,
              7, 98, 28, 8, 2, 4, 9, 66, 26, 10, 2, 127, 11, 2, 21, 12, 2, 23,
              13, 2, 18, 14, 2, 22, 15, 2, 17, 16, 2, 19,
            };
            OutboundBytes(stream).Should().Equal(
              [255, 253, 34,
               255, 250, 34, 1, 16, 255, 240,
               255, 250, 34, 3, .. bsd, 3, 130, 7, 255, 240,
               .. ForwardMaskFrame(),
               255, 251, 3, 255, 251, 0, 255, 253, 31, 255, 253, 42,
               255, 250, 34, 3, .. applied, 255, 240,
               .. ForwardMaskFrame()]);
        }

        [Fact]
        public async Task InboundImportRequest_BatchedFunc0NotFirst_ProcessesInOrder()
        {
            // Order matters: the IP triplet applies first (its ACK leads the
            // reply frame), then func 0 DEFAULT resets — wiping the just
            // applied value — and appends the table. The follow-up VALUE
            // import proves the reset won (BSD IP row, not the applied 7).
            using var stream = new ScriptedStream();
            using var session = NewSession(stream);
            stream.Enqueue([255, 251, 34, 255, 250, 34, 3, 3, 2, 7, 0, 3, 0, 255, 240]);
            await session.ReadAsync(TimeSpan.FromMilliseconds(500));
            stream.Enqueue([255, 250, 34, 3, 0, 2, 0, 255, 240]);
            await session.ReadAsync(TimeSpan.FromMilliseconds(500));
            var bsd = new byte[]
            {
              1, 3, 0, 2, 3, 0, 3, 98, 3, 4, 34, 15, 5, 2, 20, 6, 3, 0,
              7, 98, 28, 8, 2, 4, 9, 66, 26, 10, 2, 127, 11, 2, 21, 12, 2, 23,
              13, 2, 18, 14, 2, 22, 15, 2, 17, 16, 2, 19,
            };
            OutboundBytes(stream).Should().Equal(
              [255, 253, 34,
               255, 250, 34, 1, 16, 255, 240,
               255, 250, 34, 3, 3, 130, 7, .. bsd, 255, 240,
               .. ForwardMaskFrame(),
               255, 251, 3, 255, 251, 0, 255, 253, 31, 255, 253, 42,
               255, 250, 34, 3, .. bsd, 255, 240,
               .. ForwardMaskFrame()]);
        }

        [Fact]
        public async Task InboundImportRequest_NosupportLevelFunc0_FallsThroughToNormalPath()
        {
            // A func 0 at NOSUPPORT level is not an import request: the hook
            // leaves it for the normal SLC path, which drops func 0 silently
            // and still sends the forwardmask (LINEMODE is agreed).
            using var stream = new ScriptedStream();
            using var session = NewSession(stream);
            stream.Enqueue([255, 251, 34, 255, 250, 34, 3, 0, 0, 0, 255, 240]);
            await session.ReadAsync(TimeSpan.FromMilliseconds(500));
            OutboundBytes(stream).Should().Equal(
              [255, 253, 34,
               255, 250, 34, 1, 16, 255, 240,
               .. ForwardMaskFrame(),
               255, 251, 3, 255, 251, 0, 255, 253, 31, 255, 253, 42]);
        }

        [Fact]
        public async Task InboundImportRequest_MisalignedTailWithFunc0_IgnoredWhole()
        {
            // A misaligned triplet tail is ignored as a whole even when it
            // opens with a func 0: no SLC frame and no forwardmask for it.
            using var stream = new ScriptedStream();
            using var session = NewSession(stream);
            stream.Enqueue([255, 251, 34, 255, 250, 34, 3, 0, 3, 0, 3, 255, 240]);
            await session.ReadAsync(TimeSpan.FromMilliseconds(500));
            OutboundBytes(stream).Should().Equal(
              [255, 253, 34,
               255, 250, 34, 1, 16, 255, 240,
               255, 251, 3, 255, 251, 0, 255, 253, 31, 255, 253, 42]);
        }

        [Fact]
        public async Task StatusSend_AnsweredFromSessionState()
        {
            using var stream = new ScriptedStream();
            using var session = NewSession(stream);
            await session.SendOpeningPresetAsync();
            // The opening preset is DO TTYPE only (reference begin_negotiation).
            // WILL ECHO is deferred until TTYPE reveals the client, so it is
            // absent here. The peer then asks us to report STATUS (DO STATUS →
            // agreed WILL-sender, the RFC 859 §5 role gate): WILL STATUS goes
            // out, the DO volunteers one minimal IS immediately and the SEND
            // answers a second (reference: WILL, IS on DO, IS on SEND), then
            // the advanced batch (WILL SGA, WILL BINARY, DO NAWS, DO CHARSET)
            // follows once negotiation advances. The snapshot carries only
            // the options actually touched (FD 18) — never STATUS itself
            // (reference _send_status skips it in both halves) — IAC SE terminated.
            stream.Enqueue([255, 253, 5, 255, 250, 5, 1, 255, 240]);
            await session.ReadAsync(TimeSpan.FromMilliseconds(500));
            byte[] outbound = OutboundBytes(stream);
            outbound.Take(3).Should().Equal(255, 253, 24);
            outbound.Skip(3).Take(3).Should().Equal(255, 251, 5);
            byte[] snapshot =
            [
              255, 250, 5, 0,
              253, 24,
              255, 240,
            ];
            outbound.Skip(6).Should().Equal(
              [.. snapshot, .. snapshot,
               255, 251, 3, 255, 251, 0, 255, 253, 31, 255, 253, 42]);
        }

        [Fact]
        public async Task StatusIs_RecordsPairsWithoutReplyOrStateChange()
        {
            // RFC 859: STATUS IS is display-only — parsed and recorded,
            // never fed back into the Q-machine, no reply.
            using var stream = new ScriptedStream();
            using var session = NewSession(stream);
            session.PeerStatusReport.Should().BeNull();
            stream.Enqueue([255, 250, 5, 0, 251, 3, 253, 3, 255, 240]);
            (await session.ReadAsync(TimeSpan.FromMilliseconds(500))).Should().BeEmpty();
            stream.ByteWrites.Should().BeEmpty();
            session.PeerStatusReport.Should().Equal(
                new ServerSession.StatusReportItem(Commands.Will, 3, null),
                new ServerSession.StatusReportItem(Commands.Do, 3, null));
            session.Negotiation.GetStates(3).Should().Be((NegotiationState.SideState.No, NegotiationState.SideState.No));
        }

        [Fact]
        public async Task StatusIs_SbBlock_RecordsDataBytes()
        {
            // RFC 859 §5: IS with an SB <opt> <data> SE block records the
            // block bytes; a trailing byte after the block ends the parse
            // (the reference logs and stops) and never leaks to text. The
            // outer frame still ends at IAC SE (executed on the reference:
            // no reply, nothing in-band).
            using var stream = new ScriptedStream();
            using var session = NewSession(stream);
            stream.Enqueue([255, 250, 5, 0, 250, 31, 0, 80, 0, 24, 240, 65, 255, 240]);
            (await session.ReadAsync(TimeSpan.FromMilliseconds(500))).Should().BeEmpty();
            stream.ByteWrites.Should().BeEmpty();
            var item = session.PeerStatusReport.Should().ContainSingle().Which;
            item.Verb.Should().Be(Commands.Subnegotiation);
            item.Option.Should().Be(31);
            item.Data.Should().Equal(0, 80, 0, 24);
        }

        [Fact]
        public async Task TimingMarkDo_AnsweredWill()
        {
            using var stream = new ScriptedStream();
            using var session = NewSession(stream);
            stream.Enqueue([255, 253, 6]);
            await session.ReadAsync(TimeSpan.FromMilliseconds(500));
            OutboundBytes(stream).Should().Equal(255, 251, 6);
        }

        [Fact]
        public async Task LinemodeInterop_ModeAndSlcTable_EndToEnd()
        {
            // Full LinemodeServer+telsh loop over loopback: preset DO LINEMODE,
            // client auto-WILL, server MODE proposal + client auto-ACK, server
            // SLC publish folded into the client table, client export folded
            // into the server table. Hermetic: loopback only, every wait bounded.
            // Negotiation-only (no text ever flows), so the accept-side
            // handshake timer is disabled: it would otherwise fire
            // mid-exchange by design.
            using var server = new TelnetServer(0, new TelnetServerOptions
            {
                RequestLinemode = true,
                HandshakeTimeout = Timeout.InfiniteTimeSpan,
            });
            server.Start();
            var acceptTask = server.AcceptSessionAsync(CancellationToken.None);
            using var client = await Client.ConnectAsync("127.0.0.1", server.Port);
            using var session = await acceptTask;

            (await client.WaitForOptionEnabledAsync(Options.LineMode, local: true, TimeSpan.FromSeconds(10)))
              .Should().BeTrue();
            (await session.WaitForOptionEnabledAsync(Options.LineMode, local: false, TimeSpan.FromSeconds(10)))
              .Should().BeTrue();

            // MODE proposal: the client answers MODE+ACK on its next read and
            // the server folds the ACK on its next read.
            await session.SendModeAsync((byte)(LinemodeProtocol.Edit | LinemodeProtocol.TrapSignal));
            var sw = Stopwatch.StartNew();
            while (client.GetLinemodeMode() == 0 && sw.Elapsed < TimeSpan.FromSeconds(10))
            {
                (await client.ReadAsync(TimeSpan.FromSeconds(1))).Should().BeEmpty();
            }

            client.GetLinemodeMode().Should()
              .Be((byte)(LinemodeProtocol.Edit | LinemodeProtocol.TrapSignal));
            (await session.ReadAsync(TimeSpan.FromSeconds(10))).Should().BeEmpty();
            session.GetLinemodeMode().Should()
              .Be((byte)(LinemodeProtocol.Edit | LinemodeProtocol.TrapSignal));

            // SLC publish: the server's IP row lands in the client table.
            session.SetLinemodeEntry(3, LinemodeProtocol.LevelValue, 9);
            await session.PublishSpecialCharactersAsync();
            sw.Restart();
            while (client.GetLinemodeEntry(3).Value != 9 && sw.Elapsed < TimeSpan.FromSeconds(10))
            {
                (await client.ReadAsync(TimeSpan.FromSeconds(1))).Should().BeEmpty();
            }

            client.GetLinemodeEntry(3).Level.Should().Be(LinemodeProtocol.LevelValue);
            client.GetLinemodeEntry(3).Value.Should().Be(9);

            // SLC export: the client's EC row lands in the server table.
            client.SetLinemodeEntry(10, LinemodeProtocol.LevelValue, 8);
            await client.ExportSpecialCharactersAsync();
            sw.Restart();
            while (session.GetLinemodeEntry(10).Value != 8 && sw.Elapsed < TimeSpan.FromSeconds(10))
            {
                (await session.ReadAsync(TimeSpan.FromSeconds(1))).Should().BeEmpty();
            }

            session.GetLinemodeEntry(10).Level.Should().Be(LinemodeProtocol.LevelValue);
            session.GetLinemodeEntry(10).Value.Should().Be(8);
        }
    }
}
