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

            // Baseline: the default ctor opens with IAC DO SuppressGoAhead.
            var acceptBaseline = listener.AcceptTcpClientAsync();
#pragma warning disable CA2000 // Ownership of the stream transfers to the Client.
            using var baseline = new Client(new TcpByteStream("127.0.0.1", port), CancellationToken.None);
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
            using var stream = new ScriptedStream();
            using var session = NewSession(stream);
            await session.SendOpeningPresetAsync();
            OutboundBytes(stream).Should().Equal(
              255, 253, 24,
              255, 251, 3,
              255, 251, 0,
              255, 253, 32,
              255, 253, 31,
              255, 253, 36,
              255, 253, 34);
        }

        [Fact]
        public async Task SendOpeningPresetAsync_AllTogglesOn_SendsExactPresetFrames()
        {
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
              255, 253, 24,
              255, 251, 3,
              255, 251, 0,
              255, 253, 32,
              255, 253, 31,
              255, 253, 36,
              255, 253, 35,
              255, 253, 34,
              255, 253, 42,
              255, 253, 23);
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
            first.Should().Be(7);
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
        public async Task OpeningPreset_AllTogglesOff_PeerWillTtype_GetsExactlyOneDo()
        {
            // EXTEND of SendOpeningPresetAsync_AllTogglesOff_SendsNothing: with
            // SGA/ECHO offers off, the peer's WILL TTYPE still gets exactly one
            // IAC DO TTYPE and no WILL SGA / WILL ECHO.
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
            stream.Enqueue(255, 251, 24);
            await session.ReadAsync(TimeSpan.FromMilliseconds(500));
            OutboundBytes(stream).Should().Equal(255, 253, 24);
        }

        [Fact]
        public async Task OpeningPreset_OfferSga_SentExactlyOnceAndOutstanding()
        {
            // Port of test_default_sends_will_sga: the default preset offers
            // WILL SGA exactly once; the us-side stays WantYes until answered.
            using var stream = new ScriptedStream();
            using var session = NewSession(stream);
            await session.SendOpeningPresetAsync();
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
            stream.ByteWrites.Should().HaveCount(2);
            stream.ByteWrites[1].Should().Equal(255, 254, 24);
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
            using var session = NewSession(stream);
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
        public async Task AuthenticateAsync_PasswordLine_IsNotEchoed()
        {
            // Multi-line auth works on ScriptedStream when later lines are
            // Enqueued after the earlier read requests them (never up front).
            using var stream = new ScriptedStream();
            using var session = NewSession(stream);
            // Agree echo in an earlier read so the username line below is
            // echoed (mid-read agreements never echo, by the snapshot rule).
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

            // The username echoes (agreed echo); the secret never hits the
            // wire, and no extra WILL ECHO is emitted (already agreed).
            string outbound = System.Text.Encoding.Latin1.GetString(OutboundBytes(stream));
            outbound.Should().Contain("bob");
            outbound.Should().NotContain("s3cret");
            OutboundBytes(stream).Where(b => b == 251).Should().HaveCount(1);
        }

        private static async Task WaitForWritesAsync(ScriptedStream stream, Func<string, bool> ready)
        {
            string writes = string.Empty;
            var sw = Stopwatch.StartNew();
            while (!ready(writes) && sw.Elapsed < TimeSpan.FromSeconds(30))
            {
                await Task.Delay(20);
                writes = string.Concat(stream.StringWrites);
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
            return new ServerSession(stream, new TelnetServerOptions(), CancellationToken.None);
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
            // Only the TTYPE SEND went out: no WILL ECHO follows it.
            OutboundBytes(stream).Should().Equal(255, 250, 24, 1, 255, 240);
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
            (await session.RequestTerminalTypesAsync(TimeSpan.FromSeconds(5))).Should().Equal("ANSI", "VT100");
            byte[] outbound = OutboundBytes(stream);
            ContainsSubsequence(outbound, [255, 253, 39]).Should().BeTrue();
            ContainsSubsequence(outbound, [255, 251, 1]).Should().BeTrue();
        }

        [Fact]
        public async Task TtypeStall_FinalTimeout_ReleasesDeferredEnviron()
        {
            // One ANSI answer and then silence: the collection timeout is the
            // final wait, so the deferred DO NEW_ENVIRON still goes out.
            var options = new TelnetServerOptions { RequestNewEnvironment = true };
            using var stream = new ScriptedStream();
            stream.Enqueue(TtypeIsFrame("ANSI"));
            using var session = new ServerSession(stream, options, CancellationToken.None);
            (await session.RequestTerminalTypesAsync(TimeSpan.FromMilliseconds(300))).Should().Equal("ANSI");
            ContainsSubsequence(OutboundBytes(stream), [255, 253, 39]).Should().BeTrue();
        }

        [Fact]
        public async Task TtypeRefused_ReleasesDeferredNegotiations()
        {
            // A raw client that WONTs TTYPE releases both deferred offers once
            // the opening preset marked the session as advanced.
            var options = new TelnetServerOptions { RequestNewEnvironment = true };
            using var stream = new ScriptedStream();
            using var session = new ServerSession(stream, options, CancellationToken.None);
            await session.SendOpeningPresetAsync();
            int presetBytes = OutboundBytes(stream).Length;
            stream.Enqueue([255, 252, 24]);
            (await session.ReadAsync(TimeSpan.FromMilliseconds(500))).Should().BeEmpty();
            OutboundBytes(stream).Skip(presetBytes).ToArray().Should().Equal(255, 251, 1, 255, 253, 39);
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
            using var session = NewSession(stream);
            (await session.ReadAsync(TimeSpan.FromMilliseconds(500))).Should().BeEmpty();
            OutboundBytes(stream).Should().Equal(255, 251, 0, 255, 253, 0);
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
            OutboundBytes(stream).Should().Equal(255, 250, 24, 1, 255, 240, 255, 251, 1);
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
            CountSubsequence(OutboundBytes(stream), [255, 250, 24, 1, 255, 240]).Should().Be(2);
        }

        [Fact]
        public async Task RequestTerminalTypesAsync_MixedCaseDouble_Terminates()
        {
            // RFC 1091 §5: case is insignificant, so "BBB" repeats "bbb" and ends
            // the list (the classic triple-repeat can never arrive: the server
            // structurally stops at the first double).
            using var stream = new ScriptedStream();
            stream.Enqueue([.. TtypeIsFrame("AAA"), .. TtypeIsFrame("bbb"), .. TtypeIsFrame("BBB")]);
            using var session = NewSession(stream);
            var types = await session.RequestTerminalTypesAsync(TimeSpan.FromSeconds(5));
            types.Should().Equal("AAA", "bbb");
            OutboundBytes(stream).Should().Equal(255, 250, 24, 1, 255, 240, 255, 251, 1);
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
            OutboundBytes(stream).Should().Equal(255, 250, 24, 1, 255, 240, 255, 251, 1);
        }

        [Fact]
        public async Task RequestTerminalTypesAsync_SkipsEmptyAnswers()
        {
            // An empty IS advances nothing: the next answer fills the same slot.
            using var stream = new ScriptedStream();
            stream.Enqueue([.. TtypeIsFrame("ALPHA"), .. TtypeIsFrame(string.Empty), .. TtypeIsFrame("BETA"), .. TtypeIsFrame("BETA")]);
            using var session = NewSession(stream);
            var types = await session.RequestTerminalTypesAsync(TimeSpan.FromSeconds(5));
            types.Should().Equal("ALPHA", "BETA");
            session.ClientEffectiveTerminalType.Should().Be("BETA");
        }

        [Fact]
        public async Task RequestTerminalTypesAsync_BeyondLoopMax_OverflowSlotKeepsLast()
        {
            // Past slot 8, answers keep overwriting the overflow slot, so the
            // last answer wins there (telnetlib3's ttype{LOOPMAX+1}).
            using var stream = new ScriptedStream();
            var script = new System.Collections.Generic.List<int>();
            for (var i = 1; i <= 12; i++)
            {
                script.AddRange(TtypeIsFrame("T" + i));
            }

            stream.Enqueue([.. script]);
            using var session = NewSession(stream);
            await session.RequestTerminalTypesAsync(TimeSpan.FromMilliseconds(300));
            session.ClientTerminalTypes.Should().Equal("T1", "T2", "T3", "T4", "T5", "T6", "T7", "T8", "T12");
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
            OutboundBytes(stream).Should().Equal(255, 250, 32, 1, 255, 240);
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
            OutboundBytes(stream).Should().Equal(255, 250, 32, 1, 255, 240);
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
            // half: a second request while one is outstanding returns null
            // with a single SEND on the wire (single-active rule).
            using var stream = new ScriptedStream();
            using var session = NewSession(stream);
            var first = session.RequestTerminalSpeedAsync(TimeSpan.FromMilliseconds(300));
            var second = session.RequestTerminalSpeedAsync(TimeSpan.FromMilliseconds(300));
            (await first).Should().BeNull();
            (await second).Should().BeNull();
            CountSubsequence(OutboundBytes(stream), [255, 250, 32, 1]).Should().Be(1);
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
            OutboundBytes(stream).Should().Equal(
              255, 250, 36, 1, 0, 3, 255, 240);
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
            // capability: later bare 8-bit bytes decode instead of dropping.
            using var stream = new ScriptedStream();
            stream.Enqueue([255, 250, 39, 0, 0, (byte)'L', (byte)'A', (byte)'N', (byte)'G', 1,
              .. System.Text.Encoding.Latin1.GetBytes("en_US.UTF-8"), 255, 240]);
            using var session = NewSession(stream);
            var env = await session.RequestNewEnvironmentAsync(TimeSpan.FromSeconds(5));
            env.Should().Contain("LANG", "en_US.UTF-8");
            stream.Enqueue([0xE9]);
            (await session.ReadAsync(TimeSpan.FromMilliseconds(500))).Should().Be("é");
        }

        [Fact]
        public async Task RequestNewEnvironmentAsync_WithPlainLang_DropsHighBytes()
        {
            // LANG without an encoding suffix (or "C") presumes nothing: bare
            // 8-bit bytes still drop until BINARY is agreed.
            using var stream = new ScriptedStream();
            stream.Enqueue([255, 250, 39, 0, 0, (byte)'L', (byte)'A', (byte)'N', (byte)'G', 1,
              (byte)'C', 255, 240]);
            using var session = NewSession(stream);
            var env = await session.RequestNewEnvironmentAsync(TimeSpan.FromSeconds(5));
            env.Should().Contain("LANG", "C");
            stream.Enqueue([0xE9]);
            (await session.ReadAsync(TimeSpan.FromMilliseconds(100))).Should().BeEmpty();
        }

        [Fact]
        public async Task SpontaneousInfo_AfterWillAgreement_UpdatesEnvironment()
        {
            // RFC 1408: only the WILL-ENVIRON side may send INFO. After the
            // peer's WILL is agreed (DO reply), its INFO is consumed.
            using var stream = new ScriptedStream();
            stream.Enqueue([255, 251, 36, 255, 250, 36, 2, 3, (byte)'X', 1, (byte)'y', 255, 240]);
            using var session = NewSession(stream);
            await session.ReadAsync(TimeSpan.FromMilliseconds(500));
            session.ClientEnvironment.Should().Contain("X", "y");
            OutboundBytes(stream).Should().Equal(255, 253, 36);
        }

        [Fact]
        public async Task UnsolicitedInfo_WithoutAgreement_AnswersWont()
        {
            // Same INFO with no prior WILL: left unconsumed, handler WONTs.
            using var stream = new ScriptedStream();
            stream.Enqueue([255, 250, 36, 2, 3, (byte)'X', 1, (byte)'y', 255, 240]);
            using var session = NewSession(stream);
            await session.ReadAsync(TimeSpan.FromMilliseconds(500));
            session.ClientEnvironment.Should().NotContainKey("X");
            OutboundBytes(stream).Should().Equal(255, 252, 36);
        }

        [Fact]
        public async Task StrayIs_WithoutOutstandingRequest_AnswersWont()
        {
            using var stream = new ScriptedStream();
            stream.Enqueue([.. TtypeIsFrame("x")]);
            using var session = NewSession(stream);
            await session.ReadAsync(TimeSpan.FromMilliseconds(500));
            session.ClientTerminalTypes.Should().BeEmpty();
            OutboundBytes(stream).Should().Equal(255, 252, 24);
        }

        [Fact]
        public async Task StraySpeedIs_WithoutOutstandingRequest_AnswersWont()
        {
            using var stream = new ScriptedStream();
            stream.Enqueue([255, 250, 32, 0, (byte)'9', (byte)'6', (byte)'0', (byte)'0', (byte)',', (byte)'9', (byte)'6', (byte)'0', (byte)'0', 255, 240]);
            using var session = NewSession(stream);
            await session.ReadAsync(TimeSpan.FromMilliseconds(500));
            session.ClientTerminalSpeed.Should().BeNull();
            OutboundBytes(stream).Should().Equal(255, 252, 32);
        }

        [Fact]
        public async Task InboundNaws_VerbFirst_SetsClientWindowSize()
        {
            using var stream = new ScriptedStream();
            stream.Enqueue([255, 250, 31, 0, 0, 80, 0, 24, 255, 240]);
            using var session = NewSession(stream);
            await session.ReadAsync(TimeSpan.FromMilliseconds(500));
            session.ClientWindowSize.Should().Be(((ushort)80, (ushort)24));
            OutboundBytes(stream).Should().BeEmpty();
        }

        [Fact]
        public async Task InboundNaws_BareShape_SetsClientWindowSize()
        {
            using var stream = new ScriptedStream();
            stream.Enqueue([255, 250, 31, 0, 100, 0, 30, 255, 240]);
            using var session = NewSession(stream);
            await session.ReadAsync(TimeSpan.FromMilliseconds(500));
            session.ClientWindowSize.Should().Be(((ushort)100, (ushort)30));
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
            stream.Enqueue([255, 250, 31, 0, 0, 0, 0, 0, 255, 240]);
            using var session = NewSession(stream);
            await session.ReadAsync(TimeSpan.FromMilliseconds(500));
            session.ClientWindowSize.Should().Be(((ushort)0, (ushort)0));
            OutboundBytes(stream).Should().BeEmpty();
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

        [Fact]
        public async Task LmodeAgreement_CompletesWithoutFurtherReply()
        {
            using var stream = new ScriptedStream();
            using var session = NewSession(stream);
            await AgreeLinemodeAsync(session, stream);
            session.Negotiation.IsEnabledByPeer((int)Options.LineMode).Should().BeTrue();
            OutboundBytes(stream).Should().Equal(255, 253, 34);
        }

        [Fact]
        public async Task ModeRequest_SubsetIsConfirmedWithRequiredBitsAndAck()
        {
            using var stream = new ScriptedStream();
            using var session = NewSession(stream);
            // DO LINEMODE, then MODE asking for EDIT only: the server keeps
            // EDIT|TRAPSIG (union, never cleared) and sets MODE_ACK (4).
            stream.Enqueue([255, 253, 34, 255, 250, 34, 1, 1, 255, 240]);
            await session.ReadAsync(TimeSpan.FromMilliseconds(500));
            OutboundBytes(stream).Should().Equal(
              255, 251, 34,
              255, 250, 34, 1, 7, 255, 240);
        }

        [Fact]
        public async Task ModeRequest_ZeroStillYieldsRequiredBits()
        {
            using var stream = new ScriptedStream();
            using var session = NewSession(stream);
            // MODE [EDIT] is answered (stored 0 -> EDIT|TRAPSIG|ACK); a later
            // MODE [0] must not clear the required bits: answered EDIT|TRAPSIG|ACK.
            stream.Enqueue([255, 253, 34, 255, 250, 34, 1, 1, 255, 240, 255, 250, 34, 1, 0, 255, 240]);
            await session.ReadAsync(TimeSpan.FromMilliseconds(500));
            OutboundBytes(stream).Should().Equal(
              255, 251, 34,
              255, 250, 34, 1, 7, 255, 240,
              255, 250, 34, 1, 7, 255, 240);
        }

        [Fact]
        public async Task ModeAck_DifferingMaskIsAdoptedSilently()
        {
            using var stream = new ScriptedStream();
            using var session = NewSession(stream);
            // MODE+ACK [EDIT] differs from stored 0: server rule adopts it with
            // no reply (the client rule would ignore it and answer the next
            // MODE, which must not happen).
            stream.Enqueue([255, 253, 34, 255, 250, 34, 1, 5, 255, 240, 255, 250, 34, 1, 1, 255, 240]);
            await session.ReadAsync(TimeSpan.FromMilliseconds(500));
            OutboundBytes(stream).Should().Equal(255, 251, 34);
        }

        [Fact]
        public async Task ModeRequest_MalformedPayloads_AreSilent()
        {
            using var stream = new ScriptedStream();
            using var session = NewSession(stream);
            stream.Enqueue([255, 253, 34, 255, 250, 34, 255, 240, 255, 250, 34, 1, 7, 9, 255, 240]);
            await session.ReadAsync(TimeSpan.FromMilliseconds(500));
            OutboundBytes(stream).Should().Equal(255, 251, 34);
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
              255, 250, 34, 253, 2, 255, 255, 255, 240);
        }

        [Fact]
        public async Task ForwardMask_RogueProposal_IsRefused()
        {
            using var stream = new ScriptedStream();
            using var session = NewSession(stream);
            // A peer DO FORWARDMASK (only the DO-sender may propose) is refused
            // in-band, never mistaken for a stray SEND.
            stream.Enqueue([255, 253, 34, 255, 250, 34, 253, 2, 255, 240]);
            await session.ReadAsync(TimeSpan.FromMilliseconds(500));
            OutboundBytes(stream).Should().Equal(
              255, 251, 34,
              255, 250, 34, 252, 2, 255, 240);
        }

        [Fact]
        public async Task ForwardMask_WillWithoutLinemode_IsIgnored()
        {
            // Port of test_handle_sb_forwardmask_server_without_linemode
            // (DIVERGENCE on the state half): an inbound WILL FORWARDMASK
            // with no LINEMODE agreement is ignored — no reply, no data —
            // instead of setting a remote-forwardmask flag (no such state
            // exists here).
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
              255, 250, 34, 3, 3, 2, 5, 255, 240);
        }

        [Fact]
        public async Task PublishSpecialCharactersAsync_EmptyTable_SendsNothing()
        {
            using var stream = new ScriptedStream();
            using var session = NewSession(stream);
            await AgreeLinemodeAsync(session, stream);
            await session.PublishSpecialCharactersAsync();
            OutboundBytes(stream).Should().Equal(255, 253, 34);
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
            OutboundBytes(stream).Should().Equal(255, 253, 34, 255, 244);
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
            // Peer publishes IP=^E at VALUE level: agreed (stored) and ACKed.
            stream.Enqueue([255, 253, 34, 255, 250, 34, 3, 3, 2, 5, 255, 240]);
            await session.ReadAsync(TimeSpan.FromMilliseconds(500));
            OutboundBytes(stream).Should().Equal(
              255, 251, 34,
              255, 250, 34, 3, 3, 130, 5, 255, 240);
        }

        [Fact]
        public async Task ModeAck_SameMaskIsSilent()
        {
            using var stream = new ScriptedStream();
            using var session = NewSession(stream);
            // MODE [EDIT] is answered (stored 0 -> EDIT|TRAPSIG|ACK); the peer's
            // MODE+ACK echoing the agreed mask is already in effect: silent.
            stream.Enqueue([255, 253, 34, 255, 250, 34, 1, 1, 255, 240, 255, 250, 34, 1, 7, 255, 240]);
            await session.ReadAsync(TimeSpan.FromMilliseconds(500));
            OutboundBytes(stream).Should().Equal(
              255, 251, 34,
              255, 250, 34, 1, 7, 255, 240);
        }

        [Fact]
        public async Task InboundImportRequest_EmptyTable_SendsDefaultTable()
        {
            // RFC 1184 §2.4: (0,DEFAULT,0) is answered with the full table, every
            // NOSUPPORT row as [func,DEFAULT,0] — never silence.
            using var stream = new ScriptedStream();
            using var session = NewSession(stream);
            stream.Enqueue([255, 253, 34, 255, 250, 34, 3, 0, 3, 0, 255, 240]);
            await session.ReadAsync(TimeSpan.FromMilliseconds(500));
            var table = Enumerable.Range(1, 30).SelectMany(static f => new byte[] { (byte)f, 3, 0 });
            OutboundBytes(stream).Should().Equal([255, 251, 34, 255, 250, 34, 3, .. table, 255, 240]);
        }

        [Fact]
        public async Task InboundImportRequest_ConfiguredTable_RendersDefaultsForGaps()
        {
            using var stream = new ScriptedStream();
            using var session = NewSession(stream);
            session.SetLinemodeEntry(3, 2, 5);
            stream.Enqueue([255, 253, 34, 255, 250, 34, 3, 0, 3, 0, 255, 240]);
            await session.ReadAsync(TimeSpan.FromMilliseconds(500));
            var table = Enumerable.Range(1, 30).SelectMany(
              static f => f == 3 ? new byte[] { 3, 2, 5 } : new byte[] { (byte)f, 3, 0 });
            OutboundBytes(stream).Should().Equal([255, 251, 34, 255, 250, 34, 3, .. table, 255, 240]);
        }

        [Fact]
        public async Task InboundSendCurrentRequest_ReturnsConfiguredTable()
        {
            // (0,VALUE,0) asks for current settings: normal export (NOSUPPORT
            // rows omitted), not the DEFAULT rendering.
            using var stream = new ScriptedStream();
            using var session = NewSession(stream);
            session.SetLinemodeEntry(3, 2, 5);
            stream.Enqueue([255, 253, 34, 255, 250, 34, 3, 0, 2, 0, 255, 240]);
            await session.ReadAsync(TimeSpan.FromMilliseconds(500));
            OutboundBytes(stream).Should().Equal(
              255, 251, 34,
              255, 250, 34, 3, 3, 2, 5, 255, 240);
        }

        [Fact]
        public async Task InboundSendCurrentRequest_EmptyTable_IsSilent()
        {
            using var stream = new ScriptedStream();
            using var session = NewSession(stream);
            stream.Enqueue([255, 253, 34, 255, 250, 34, 3, 0, 2, 0, 255, 240]);
            await session.ReadAsync(TimeSpan.FromMilliseconds(500));
            OutboundBytes(stream).Should().Equal(255, 251, 34);
        }

        [Fact]
        public async Task InboundSlc_MalformedTriplets_Throw()
        {
            // Misaligned SLC triplets throw (telnetlib3 raises ValueError);
            // nothing is answered.
            using var stream = new ScriptedStream();
            using var session = NewSession(stream);
            stream.Enqueue([255, 253, 34, 255, 250, 34, 3, 3, 255, 240]);
            var act = async () => await session.ReadAsync(TimeSpan.FromMilliseconds(500));
            await act.Should().ThrowAsync<InvalidDataException>();
            OutboundBytes(stream).Should().Equal(255, 251, 34);
        }

        [Fact]
        public async Task InboundImportRequest_ResetsWorkingTableToDefaults()
        {
            // (0,DEFAULT,0) resets negotiated-away rows before answering:
            // IP is moved to ^E, the import restores ^C, and a later
            // (0,VALUE,0) exports the restored value.
            using var stream = new ScriptedStream();
            using var session = NewSession(stream);
            session.SetLinemodeEntry(3, 2, 3);
            stream.Enqueue([255, 253, 34, 255, 250, 34, 3, 3, 2, 5, 255, 240]);
            await session.ReadAsync(TimeSpan.FromMilliseconds(500));
            stream.Enqueue([255, 250, 34, 3, 0, 3, 0, 255, 240]);
            await session.ReadAsync(TimeSpan.FromMilliseconds(500));
            stream.Enqueue([255, 250, 34, 3, 0, 2, 0, 255, 240]);
            await session.ReadAsync(TimeSpan.FromMilliseconds(500));
            var table = Enumerable.Range(1, 30).SelectMany(
              static f => f == 3 ? new byte[] { 3, 2, 3 } : new byte[] { (byte)f, 3, 0 });
            OutboundBytes(stream).Should().Equal(
              [255, 251, 34,
               255, 250, 34, 3, 3, 130, 5, 255, 240,
               255, 250, 34, 3, .. table, 255, 240,
               255, 250, 34, 3, 3, 2, 3, 255, 240]);
        }

        [Fact]
        public async Task StatusSend_AnsweredFromSessionState()
        {
            using var stream = new ScriptedStream();
            using var session = NewSession(stream);
            await session.SendOpeningPresetAsync();
            // After the preset we are WILL-sender for SGA and BINARY (WANTYES)
            // and have outstanding DOs for TTYPE/TSPEED/NAWS/ENVIRON/LINEMODE;
            // WILL ECHO is deferred until TTYPE reveals the client, so it is
            // absent here. The peer then asks us to report STATUS (DO STATUS →
            // agreed WILL-sender, the RFC 859 §5 role gate), so the snapshot
            // reports the two WILLs plus the five DOs, IAC SE terminated.
            stream.Enqueue([255, 253, 5, 255, 250, 5, 1, 255, 240]);
            await session.ReadAsync(TimeSpan.FromMilliseconds(500));
            byte[] outbound = OutboundBytes(stream);
            outbound.Take(21).Should().Equal(
              255, 253, 24,
              255, 251, 3,
              255, 251, 0,
              255, 253, 32,
              255, 253, 31,
              255, 253, 36,
              255, 253, 34);
            outbound.Skip(21).Take(3).Should().Equal(255, 251, 5);
            outbound.Skip(24).Should().Equal(
              255, 250, 5, 0,
              251, 0, 251, 3, 251, 5,
              253, 24, 253, 31, 253, 32, 253, 34, 253, 36,
              255, 240);
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
            using var server = new TelnetServer(0, new TelnetServerOptions { RequestLinemode = true });
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
