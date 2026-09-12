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

        [Fact]
        public async Task GaUnsuppressed_SurfacesThroughSession()
        {
            using var stream = new ScriptedStream(255, 249);
            using var session = NewSession(stream);
            var fired = 0;
            session.GoAheadReceived += (_, _) => fired++;
            (await session.ReadAsync(TimeSpan.FromMilliseconds(100))).Should().BeEmpty();
            fired.Should().Be(1);
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
              255, 251, 1,
              255, 251, 3,
              255, 253, 24,
              255, 253, 32,
              255, 253, 31,
              255, 253, 36,
              255, 253, 34);
        }

        [Fact]
        public async Task SendOpeningPresetAsync_AllTogglesOff_SendsNothing()
        {
            var options = new TelnetServerOptions
            {
                OfferEcho = false,
                OfferSuppressGoAhead = false,
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
        public async Task RequestDisableAsync_AfterPeerAgrees_SendsDont()
        {
            using var stream = new ScriptedStream();
            using var session = NewSession(stream);
            await session.RequestEnableAsync(Options.TerminalType);
            // Peer WILLs TTYPE over the wire (agreeing with our DO): him-side YES.
            stream.Enqueue(255, 251, 24);
            await session.ReadAsync(TimeSpan.FromSeconds(5));
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
            // Termination stops further polling; it does not truncate what the
            // read already delivered (same semantics as Client).
            using var stream = new ScriptedStream("hi\nrest");
            using var session = NewSession(stream);
            var line = await session.TerminatedReadAsync("\n", TimeSpan.FromSeconds(5));
            line.Should().Be("hi\nrest");
        }

        // NOTE: multi-line auth conversations cannot run on ScriptedStream: one
        // read drains all queued bytes, so the first credential read would
        // swallow the second line (same reason Client.TryLoginAsync is only
        // covered over loopback). Accept/reject paths are covered by
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

        [Fact]
        public async Task RequestTerminalTypesAsync_CollectsChainUntilRepeat()
        {
            using var stream = new ScriptedStream();
            stream.Enqueue([.. TtypeIsFrame("aaa"), .. TtypeIsFrame("bbb"), .. TtypeIsFrame("bbb")]);
            using var session = NewSession(stream);
            var types = await session.RequestTerminalTypesAsync(TimeSpan.FromSeconds(5));
            types.Should().Equal("aaa", "bbb");
            OutboundBytes(stream).Should().Equal(255, 250, 24, 1, 255, 240);
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
            OutboundBytes(stream).Should().Equal(255, 250, 24, 1, 255, 240);
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
            OutboundBytes(stream).Should().Equal(255, 250, 24, 1, 255, 240);
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
            (await session.ReadAsync(TimeSpan.FromSeconds(5))).Should().Be("é");
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
            await session.ReadAsync(TimeSpan.FromSeconds(5));
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
            await session.ReadAsync(TimeSpan.FromSeconds(5));
            session.ClientEnvironment.Should().NotContainKey("X");
            OutboundBytes(stream).Should().Equal(255, 252, 36);
        }

        [Fact]
        public async Task StrayIs_WithoutOutstandingRequest_AnswersWont()
        {
            using var stream = new ScriptedStream();
            stream.Enqueue([.. TtypeIsFrame("x")]);
            using var session = NewSession(stream);
            await session.ReadAsync(TimeSpan.FromSeconds(5));
            session.ClientTerminalTypes.Should().BeEmpty();
            OutboundBytes(stream).Should().Equal(255, 252, 24);
        }

        [Fact]
        public async Task StraySpeedIs_WithoutOutstandingRequest_AnswersWont()
        {
            using var stream = new ScriptedStream();
            stream.Enqueue([255, 250, 32, 0, (byte)'9', (byte)'6', (byte)'0', (byte)'0', (byte)',', (byte)'9', (byte)'6', (byte)'0', (byte)'0', 255, 240]);
            using var session = NewSession(stream);
            await session.ReadAsync(TimeSpan.FromSeconds(5));
            session.ClientTerminalSpeed.Should().BeNull();
            OutboundBytes(stream).Should().Equal(255, 252, 32);
        }

        [Fact]
        public async Task InboundNaws_VerbFirst_SetsClientWindowSize()
        {
            using var stream = new ScriptedStream();
            stream.Enqueue([255, 250, 31, 0, 0, 80, 0, 24, 255, 240]);
            using var session = NewSession(stream);
            await session.ReadAsync(TimeSpan.FromSeconds(5));
            session.ClientWindowSize.Should().Be(((ushort)80, (ushort)24));
            OutboundBytes(stream).Should().BeEmpty();
        }

        [Fact]
        public async Task InboundNaws_BareShape_SetsClientWindowSize()
        {
            using var stream = new ScriptedStream();
            stream.Enqueue([255, 250, 31, 0, 100, 0, 30, 255, 240]);
            using var session = NewSession(stream);
            await session.ReadAsync(TimeSpan.FromSeconds(5));
            session.ClientWindowSize.Should().Be(((ushort)100, (ushort)30));
        }

        [Fact]
        public async Task InboundNaws_Malformed_Ignored()
        {
            using var stream = new ScriptedStream();
            stream.Enqueue([255, 250, 31, 0, 80, 255, 240]);
            using var session = NewSession(stream);
            await session.ReadAsync(TimeSpan.FromSeconds(5));
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
            await session.ReadAsync(TimeSpan.FromSeconds(5));
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
            await session.ReadAsync(TimeSpan.FromSeconds(5));
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
            await session.ReadAsync(TimeSpan.FromSeconds(5));
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
            await session.ReadAsync(TimeSpan.FromSeconds(5));
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
            await session.ReadAsync(TimeSpan.FromSeconds(5));
            OutboundBytes(stream).Should().Equal(255, 251, 34);
        }

        [Fact]
        public async Task ModeRequest_MalformedPayloads_AreSilent()
        {
            using var stream = new ScriptedStream();
            using var session = NewSession(stream);
            stream.Enqueue([255, 253, 34, 255, 250, 34, 255, 240, 255, 250, 34, 1, 7, 9, 255, 240]);
            await session.ReadAsync(TimeSpan.FromSeconds(5));
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
            await session.ReadAsync(TimeSpan.FromSeconds(5));
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
            await session.ReadAsync(TimeSpan.FromSeconds(5));
            OutboundBytes(stream).Should().Equal(
              255, 251, 34,
              255, 250, 34, 252, 2, 255, 240);
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
            await session.ReadAsync(TimeSpan.FromSeconds(5));
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
            await session.ReadAsync(TimeSpan.FromSeconds(5));
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
            await session.ReadAsync(TimeSpan.FromSeconds(5));
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
            await session.ReadAsync(TimeSpan.FromSeconds(5));
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
            await session.ReadAsync(TimeSpan.FromSeconds(5));
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
            await session.ReadAsync(TimeSpan.FromSeconds(5));
            OutboundBytes(stream).Should().Equal(255, 251, 34);
        }

        [Fact]
        public async Task InboundSlc_MalformedTriplets_AreSilent()
        {
            using var stream = new ScriptedStream();
            using var session = NewSession(stream);
            stream.Enqueue([255, 253, 34, 255, 250, 34, 3, 3, 255, 240]);
            await session.ReadAsync(TimeSpan.FromSeconds(5));
            OutboundBytes(stream).Should().Equal(255, 251, 34);
        }

        [Fact]
        public async Task StatusSend_AnsweredFromSessionState()
        {
            using var stream = new ScriptedStream();
            using var session = NewSession(stream);
            await session.SendOpeningPresetAsync();
            // After the preset we are WILL-sender for ECHO and SGA (WANTYES) and
            // have outstanding DOs for TTYPE/TSPEED/NAWS/ENVIRON/LINEMODE; the peer
            // then asks us to report STATUS (DO STATUS → agreed WILL-sender, the
            // RFC 859 §5 role gate), so the snapshot reports the three WILLs plus
            // the five DOs, bare-SE terminated.
            stream.Enqueue([255, 253, 5, 255, 250, 5, 1, 255, 240]);
            await session.ReadAsync(TimeSpan.FromSeconds(5));
            byte[] outbound = OutboundBytes(stream);
            outbound.Take(21).Should().Equal(
              255, 251, 1,
              255, 251, 3,
              255, 253, 24,
              255, 253, 32,
              255, 253, 31,
              255, 253, 36,
              255, 253, 34);
            outbound.Skip(21).Take(3).Should().Equal(255, 251, 5);
            outbound.Skip(24).Should().Equal(
              255, 250, 5, 0,
              251, 1, 251, 3, 251, 5,
              253, 24, 253, 31, 253, 32, 253, 34, 253, 36,
              240);
        }

        [Fact]
        public async Task TimingMarkDo_AnsweredWill()
        {
            using var stream = new ScriptedStream();
            using var session = NewSession(stream);
            stream.Enqueue([255, 253, 6]);
            await session.ReadAsync(TimeSpan.FromSeconds(5));
            OutboundBytes(stream).Should().Equal(255, 251, 6);
        }
    }
}
