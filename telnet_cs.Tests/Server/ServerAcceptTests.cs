// S2 server tests: listener lifecycle, multi-accept, and the crown-jewel
// loopback interop (real Client against real TelnetServer/ServerSession:
// full negotiation, login round-trip). Hermetic: loopback only,
// OS-assigned ports, every wait bounded.
namespace telnet_cs.Tests
{
    using System;
    using System.Diagnostics;
    using System.Net;
    using System.Net.Sockets;
    using System.Security.Authentication;
    using System.Threading;
    using System.Threading.Tasks;
    using FluentAssertions;
    using Xunit;
    using telnet_cs.Client;
    using telnet_cs.Protocol;
    using telnet_cs.Server;

    public class ServerAcceptTests
    {
        [Fact]
        public void TelnetServerOptions_HasDocumentedDefaults()
        {
            var options = new TelnetServerOptions();
            options.Backlog.Should().Be(32);
            options.OfferEcho.Should().BeTrue();
            options.OfferSuppressGoAhead.Should().BeTrue();
            options.RequestTerminalType.Should().BeTrue();
            options.RequestTerminalSpeed.Should().BeTrue();
            options.RequestWindowSize.Should().BeTrue();
            options.RequestEnvironment.Should().BeTrue();
            options.RequestLinemode.Should().BeTrue();
            options.LoginUserPrompt.Should().Be("login: ");
            options.LoginPasswordPrompt.Should().Be("Password: ");
            options.MaxLoginAttempts.Should().Be(3);
            options.TextEncoding.Should().BeNull();
            options.IsWriteConsole.Should().BeNull();
            options.Log.Should().BeNull();
            options.TlsProtocols.Should().Be(SslProtocols.None);
            options.ListenAddress.Should().Be(IPAddress.Any);
        }

        [Fact]
        public void Port_IsZeroBeforeStart()
        {
            using var server = new TelnetServer(0);
            server.Port.Should().Be(0);
        }

        [Fact]
        public async Task AcceptSessionAsync_ReturnsConnectedSession()
        {
            using var server = new TelnetServer(0);
            server.Start();
            server.Port.Should().BeInRange(1, 65535);
            var acceptTask = server.AcceptSessionAsync(CancellationToken.None);
            using var client = await Client.ConnectAsync("127.0.0.1", server.Port);
            using var session = await acceptTask;
            session.IsConnected.Should().BeTrue();
        }

        [Fact]
        public async Task AcceptSessionAsync_LoopbackListenAddress_Accepts()
        {
            using var server = new TelnetServer(0, new TelnetServerOptions { ListenAddress = IPAddress.Loopback });
            server.Start();
            var acceptTask = server.AcceptSessionAsync(CancellationToken.None);
            using var client = await Client.ConnectAsync("127.0.0.1", server.Port);
            using var session = await acceptTask;
            session.IsConnected.Should().BeTrue();
            client.IsConnected.Should().BeTrue();
        }

        [Fact]
        public async Task AcceptSessionAsync_IPv6ListenAddress_BindsAndAccepts()
        {
            if (!Socket.OSSupportsIPv6)
            {
                return;
            }

            using var server = new TelnetServer(0, new TelnetServerOptions { ListenAddress = IPAddress.IPv6Loopback });
            server.Start();
            var acceptTask = server.AcceptSessionAsync(CancellationToken.None);
            using var client = await Client.ConnectAsync("::1", server.Port);
            using var session = await acceptTask;
            session.IsConnected.Should().BeTrue();
            client.IsConnected.Should().BeTrue();
        }

        [Fact]
        public async Task AcceptSessionAsync_EmitsOpeningPreset()
        {
            // The accepted session negotiates as a server: the connecting client
            // must observe our WILL ECHO (and answer DO), proving the preset went
            // out over the real socket.
            using var server = new TelnetServer(0);
            server.Start();
            var acceptTask = server.AcceptSessionAsync(CancellationToken.None);
            using var client = await Client.ConnectAsync("127.0.0.1", server.Port);
            using var session = await acceptTask;

            // Pump the client until it has processed our WILL ECHO: its DO reply
            // confirms our offer, flipping our us-side to YES.
            var sw = Stopwatch.StartNew();
            while (!session.Negotiation.IsEnabledByUs((int)Options.Echo) && sw.Elapsed < TimeSpan.FromSeconds(10))
            {
                await client.ReadAsync(TimeSpan.FromMilliseconds(50));
                await session.ReadAsync(TimeSpan.FromMilliseconds(50));
            }

            session.Negotiation.IsEnabledByUs((int)Options.Echo).Should().BeTrue();
        }

        [Fact]
        public async Task Stop_DoesNotKillAcceptedSessions()
        {
            using var server = new TelnetServer(0);
            server.Start();
            var acceptTask = server.AcceptSessionAsync(CancellationToken.None);
            using var client = await Client.ConnectAsync("127.0.0.1", server.Port);
            using var session = await acceptTask;
            server.Stop();

            await client.WriteAsync("still-here");
            var received = string.Empty;
            var sw = Stopwatch.StartNew();
            while (!received.Contains("still-here", StringComparison.Ordinal) && sw.Elapsed < TimeSpan.FromSeconds(10))
            {
                received += await session.ReadAsync();
            }

            received.Should().Contain("still-here");
        }

        [Fact]
        public async Task AcceptSessionAsync_AcceptsSequentialClients()
        {
            using var server = new TelnetServer(0);
            server.Start();
            for (int i = 0; i < 2; i++)
            {
                var acceptTask = server.AcceptSessionAsync(CancellationToken.None);
                using var client = await Client.ConnectAsync("127.0.0.1", server.Port);
                using var session = await acceptTask;
                session.IsConnected.Should().BeTrue();
            }
        }

        [Fact]
        public async Task AcceptSessionAsync_AfterDispose_Throws()
        {
            var server = new TelnetServer(0);
            server.Start();
            server.Dispose();
            Func<Task> act = () => server.AcceptSessionAsync(CancellationToken.None);
            await act.Should().ThrowAsync<ObjectDisposedException>();
        }

        [Fact]
        public async Task LoginInterop_ClientTryLoginAgainstSessionAuth()
        {
            using var server = new TelnetServer(0);
            server.Start();
            var acceptTask = server.AcceptSessionAsync(CancellationToken.None);
            using var client = await Client.ConnectAsync("127.0.0.1", server.Port);
            using var session = await acceptTask;

            var authTask = session.AuthenticateAsync(
              (u, p) => Task.FromResult(u == "bob" && p == "s3cret"),
              TimeSpan.FromSeconds(10));
            var loginTask = client.TryLoginAsync("bob", "s3cret", 10000);
            (await authTask).Should().BeTrue();

            // TryLogin waits for a ">" terminator after the password: the server
            // side of a login sends the first shell prompt.
            await session.WriteLineAsync("welcome>");
            (await loginTask).Should().BeTrue();
        }

        [Fact]
        public async Task LoginInterop_WrongPassword_FailsBothSides()
        {
            using var server = new TelnetServer(0);
            server.Start();
            var acceptTask = server.AcceptSessionAsync(CancellationToken.None);
            using var client = await Client.ConnectAsync("127.0.0.1", server.Port);
            using var session = await acceptTask;
            session.Settings.MaxLoginAttempts = 1;

            var authTask = session.AuthenticateAsync(
              (u, p) => Task.FromResult(p == "s3cret"),
              TimeSpan.FromSeconds(10));
            // No ">" prompt ever arrives: short client timeout keeps this bounded.
            var loginTask = client.TryLoginAsync("bob", "wrong", 1500);
            (await authTask).Should().BeFalse();
            (await loginTask).Should().BeFalse();
        }

        // Raw-socket auth: a ScriptedStream delivers all queued bytes in one
        // read, so the first credential read would swallow the second line.
        // A real socket trickles, exercising the true multi-line conversation
        // (prompt → line → prompt → line), including exact credential values.
        private static string ReadUntil(System.Net.Sockets.NetworkStream peer, string marker)
        {
            var buffer = new byte[256];
            var seen = new System.Text.StringBuilder();
            var sw = Stopwatch.StartNew();
            while (!seen.ToString().Contains(marker, StringComparison.Ordinal) && sw.Elapsed < TimeSpan.FromSeconds(10))
            {
                int n;
                try
                {
                    n = peer.Read(buffer, 0, buffer.Length);
                }
                catch (System.IO.IOException)
                {
                    break;
                }

                if (n == 0)
                {
                    break;
                }

                seen.Append(System.Text.Encoding.Latin1.GetString(buffer, 0, n));
            }

            return seen.ToString();
        }

        private static void WriteLineRaw(System.Net.Sockets.NetworkStream peer, string line)
        {
            byte[] bytes = System.Text.Encoding.Latin1.GetBytes(line + "\n");
            peer.Write(bytes, 0, bytes.Length);
        }

        [Fact]
        public async Task AuthenticateAsync_AcceptsValidCredentials()
        {
            using var server = new TelnetServer(0);
            server.Start();
            var acceptTask = server.AcceptSessionAsync(CancellationToken.None);
            using var raw = new System.Net.Sockets.TcpClient();
            await raw.ConnectAsync("127.0.0.1", server.Port);
            using var session = await acceptTask;
            using var peer = raw.GetStream();
            peer.ReadTimeout = 2000;

            (string User, string Pass)? seen = null;
            var authTask = session.AuthenticateAsync(
              (u, p) => { seen = (u, p); return Task.FromResult(true); },
              TimeSpan.FromSeconds(10));
            ReadUntil(peer, "login: ");
            WriteLineRaw(peer, "bob");
            ReadUntil(peer, "Password: ");
            WriteLineRaw(peer, "s3cret");
            (await authTask).Should().BeTrue();
            seen.Should().Be(("bob", "s3cret"));
        }

        [Fact]
        public async Task AuthenticateAsync_RejectsUntilAttemptsExhausted()
        {
            var options = new TelnetServerOptions { MaxLoginAttempts = 2 };
            using var server = new TelnetServer(0, options);
            server.Start();
            var acceptTask = server.AcceptSessionAsync(CancellationToken.None);
            using var raw = new System.Net.Sockets.TcpClient();
            await raw.ConnectAsync("127.0.0.1", server.Port);
            using var session = await acceptTask;
            using var peer = raw.GetStream();
            peer.ReadTimeout = 2000;

            var seen = new System.Collections.Generic.List<(string, string)>();
            var authTask = session.AuthenticateAsync(
              (u, p) => { seen.Add((u, p)); return Task.FromResult(false); },
              TimeSpan.FromSeconds(10));
            for (int i = 0; i < 2; i++)
            {
                ReadUntil(peer, "login: ");
                WriteLineRaw(peer, "user" + i);
                ReadUntil(peer, "Password: ");
                WriteLineRaw(peer, "pass" + i);
            }

            (await authTask).Should().BeFalse();
            seen.Should().Equal(("user0", "pass0"), ("user1", "pass1"));
        }

        [Fact]
        public async Task SynchInterop_ClientSendSynchArrivesOutOfBand()
        {
            using var server = new TelnetServer(0);
            server.Start();
            var acceptTask = server.AcceptSessionAsync(CancellationToken.None);
            using var client = await Client.ConnectAsync("127.0.0.1", server.Port);
            using var session = await acceptTask;

            await client.SendSynchAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            (await session.ReceiveUrgentAsync(cts.Token)).Should().Be(242);
        }

        [Fact]
        public async Task ReceiveUrgentAsync_NonTcpStream_ThrowsNotSupported()
        {
            using var stream = new ScriptedStream();
            using var session = new ServerSession(stream, new TelnetServerOptions(), CancellationToken.None);
            Func<Task> receive = () => session.ReceiveUrgentAsync();
            await receive.Should().ThrowAsync<NotSupportedException>();
        }
    }
}
