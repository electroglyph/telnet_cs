// Hardening tests: admission control, handshake timeout, auth throttle,
// bounded inbound buffering, ENVIRON caps, and option validation.
namespace telnet_cs.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net.Sockets;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using FluentAssertions;
    using Xunit;
    using telnet_cs.Server;

    public class ServerHardeningTests
    {
        private static ServerSession NewSession(ScriptedStream stream, TelnetServerOptions? options = null)
        {
            return new ServerSession(stream, options ?? new TelnetServerOptions(), CancellationToken.None);
        }

        private static string OutboundText(ScriptedStream stream)
        {
            return string.Concat(stream.StringWrites);
        }

        private static void WriteLineRaw(NetworkStream peer, string line)
        {
            byte[] bytes = Encoding.Latin1.GetBytes(line + "\n");
            peer.Write(bytes, 0, bytes.Length);
        }

        private static string ReadUntil(NetworkStream peer, string marker)
        {
            var sb = new StringBuilder();
            var one = new byte[1];
            while (!sb.ToString().Contains(marker, StringComparison.Ordinal))
            {
                int n = peer.Read(one, 0, 1);
                if (n == 0)
                {
                    break;
                }

                sb.Append((char)one[0]);
            }

            return sb.ToString();
        }

        private static TelnetServerOptions LowFrictionOptions()
        {
            return new TelnetServerOptions
            {
                MaxConnectionsPerIp = 0,
                LoginAttemptDelay = TimeSpan.Zero,
                HandshakeTimeout = TimeSpan.FromSeconds(30),
            };
        }

        [Fact]
        public async Task Start_MaxConcurrentSessionsOne_SecondSessionRefused()
        {
            var options = LowFrictionOptions();
            options.MaxConcurrentSessions = 1;
            using var server = new TelnetServer(0, options);
            server.Start();

            using var raw1 = new TcpClient();
            var held1 = await ConnectAndHoldAsync(server, raw1);
            using (held1.Item2)
            {
                using var raw2 = new TcpClient();
                var accept2 = server.AcceptSessionAsync(CancellationToken.None);
                await raw2.ConnectAsync("127.0.0.1", server.Port);
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => accept2);
                ex.Message.Should().StartWith("over-capacity:");
                server.RejectedCapacityCount.Should().Be(1);
                server.RejectedPerIpCount.Should().Be(0);
                held1.Item2.IsConnected.Should().BeTrue();
            }
        }

        [Fact]
        public async Task Start_MaxConnectionsPerIpOne_SecondRefusedCountsPerIp()
        {
            var options = LowFrictionOptions();
            options.MaxConcurrentSessions = 0;
            options.MaxConnectionsPerIp = 1;
            using var server = new TelnetServer(0, options);
            server.Start();

            using var raw1 = new TcpClient();
            var held1 = await ConnectAndHoldAsync(server, raw1);
            using (held1.Item2)
            {
                using var raw2 = new TcpClient();
                var accept2 = server.AcceptSessionAsync(CancellationToken.None);
                await raw2.ConnectAsync("127.0.0.1", server.Port);
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => accept2);
                ex.Message.Should().StartWith("over-capacity:");
                server.RejectedPerIpCount.Should().Be(1);
                server.RejectedCapacityCount.Should().Be(0);
                held1.Item2.IsConnected.Should().BeTrue();
            }
        }

        private static async Task<Tuple<NetworkStream, ServerSession>> ConnectAndHoldAsync(
            TelnetServer server, TcpClient raw)
        {
            var accept = server.AcceptSessionAsync(CancellationToken.None);
            await raw.ConnectAsync("127.0.0.1", server.Port);
            var session = await accept;
            var peer = raw.GetStream();
            peer.ReadTimeout = 5000;
            var authTask = session.AuthenticateAsync(
                (u, p) => Task.FromResult(true), TimeSpan.FromSeconds(10));
            ReadUntil(peer, "login: ");
            WriteLineRaw(peer, "holder");
            ReadUntil(peer, "Password: ");
            WriteLineRaw(peer, "pw");
            (await authTask).Should().BeTrue();
            return Tuple.Create(peer, session);
        }

        [Fact]
        public async Task Start_AcceptFilterFalse_RejectsWithFilterCount()
        {
            var logs = new List<string>();
            var options = LowFrictionOptions();
            options.AcceptFilter = _ => false;
            options.Log = m => { lock (logs) { logs.Add(m); } };
            using var server = new TelnetServer(0, options);
            server.Start();

            using var raw = new TcpClient();
            var accept = server.AcceptSessionAsync(CancellationToken.None);
            await raw.ConnectAsync("127.0.0.1", server.Port);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => accept);
            ex.Message.Should().StartWith("over-capacity:");
            server.RejectedFilterCount.Should().Be(1);
            server.RejectedCapacityCount.Should().Be(0);
            lock (logs)
            {
                logs.Should().Contain(m => m.StartsWith("over-capacity:", StringComparison.Ordinal));
            }
        }

        [Fact]
        public async Task Start_HandshakeTimeout_IdleClient_SessionClosedWithNotice()
        {
            var logs = new List<string>();
            var options = LowFrictionOptions();
            options.HandshakeTimeout = TimeSpan.FromMilliseconds(300);
            options.Log = m => { lock (logs) { logs.Add(m); } };
            using var server = new TelnetServer(0, options);
            server.Start();

            using var raw = new TcpClient();
            var accept = server.AcceptSessionAsync(CancellationToken.None);
            await raw.ConnectAsync("127.0.0.1", server.Port);
            // The client never sends a byte: the TLS sniff resolves plain
            // immediately, so accept still returns a session, and the
            // session-owned handshake timer fires at the deadline instead.
            using var session = await accept;
            using var peer = raw.GetStream();
            peer.ReadTimeout = 5000;
            string seen = ReadUntil(peer, "Handshake timeout.");
            seen.Should().Contain("Handshake timeout.");
            // Drain the notice remainder and GA bytes until the close lands.
            int n = -1;
            for (int i = 0; i < 20 && n != 0; i++)
            {
                n = peer.Read(new byte[64], 0, 64);
            }

            n.Should().Be(0);
            session.IsConnected.Should().BeFalse();
            lock (logs)
            {
                logs.Should().Contain(m => m.StartsWith("handshake-timeout:", StringComparison.Ordinal));
            }
        }

        [Fact]
        public async Task Authenticate_DisconnectOnExhaustion_SendsNoticeAndCloses()
        {
            var options = new TelnetServerOptions
            {
                MaxLoginAttempts = 1,
                LoginAttemptDelay = TimeSpan.Zero,
                DisconnectOnExhaustion = true,
            };
            using var stream = new ScriptedStream("bad\nwrong\n");
            using var session = NewSession(stream, options);
            (await session.AuthenticateAsync(
                (u, p) => Task.FromResult(false), TimeSpan.FromSeconds(5))).Should().BeFalse();
            OutboundText(stream).Should().Contain("Login failed.");
            stream.Connected.Should().BeFalse();
        }

        [Fact]
        public async Task Authenticate_Failure_LogsAuthExhaustedWithoutPassword()
        {
            var logs = new List<string>();
            var options = new TelnetServerOptions
            {
                MaxLoginAttempts = 1,
                LoginAttemptDelay = TimeSpan.Zero,
                Log = m => logs.Add(m),
            };
            using var stream = new ScriptedStream("alice\ns3cret-pw\n");
            using var session = NewSession(stream, options);
            (await session.AuthenticateAsync(
                (u, p) => Task.FromResult(false), TimeSpan.FromSeconds(5))).Should().BeFalse();
            logs.Should().Contain(m => m.StartsWith("auth-exhausted:", StringComparison.Ordinal));
            string all = string.Concat(logs);
            all.Should().Contain("bad-credentials");
            all.Should().NotContain("s3cret-pw");
        }

        [Fact]
        public async Task Authenticate_ValidateThrows_PropagatesWithoutDelay()
        {
            var options = new TelnetServerOptions
            {
                MaxLoginAttempts = 3,
                LoginAttemptDelay = TimeSpan.FromSeconds(30),
            };
            using var stream = new ScriptedStream("u\np\n");
            using var session = NewSession(stream, options);
            var start = Environment.TickCount64;
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => session.AuthenticateAsync(
                (u, p) => Task.FromException<bool>(new InvalidOperationException("boom")),
                TimeSpan.FromSeconds(5)));
            ex.Message.Should().Be("boom");
            (Environment.TickCount64 - start).Should().BeLessThan(4000);
        }

        [Fact]
        public async Task Repl_LineTooLong_ClosesWithNotice()
        {
            var logs = new List<string>();
            var options = new TelnetServerOptions
            {
                MaxReplLineLength = 8,
                Log = m => { lock (logs) { logs.Add(m); } },
            };
            using var stream = new ScriptedStream("123456789\n");
            using var session = NewSession(stream, options);
            await ServerShells.RunReplAsync(session, CancellationToken.None);
            OutboundText(stream).Should().Contain("Line too long.");
            stream.Connected.Should().BeFalse();
            lock (logs)
            {
                logs.Should().Contain(m => m.StartsWith("buffer-cap:", StringComparison.Ordinal));
            }
        }

        [Fact]
        public async Task Pump_BufferedTextOverCap_ClosesSession()
        {
            var logs = new List<string>();
            var options = new TelnetServerOptions
            {
                MaxBufferedTextChars = 16,
                Log = m => { lock (logs) { logs.Add(m); } },
            };
            using var stream = new ScriptedStream(new string('x', 64));
            using var session = NewSession(stream, options);
            for (int i = 0; i < 30 && stream.Connected; i++)
            {
                await Task.Delay(100);
            }

            stream.Connected.Should().BeFalse();
            lock (logs)
            {
                logs.Should().Contain(m => m.StartsWith("buffer-cap:", StringComparison.Ordinal));
            }
        }

        [Fact]
        public async Task RequestTerminalSpeed_OversizedAnswer_DroppedToNull()
        {
            var logs = new List<string>();
            var options = new TelnetServerOptions
            {
                MaxEnvironValueChars = 4,
                Log = m => { lock (logs) { logs.Add(m); } },
            };
            var response = new List<int> { 255, 250, 32, 0 };
            response.AddRange(Encoding.Latin1.GetBytes("1234567890").Select(b => (int)b));
            response.AddRange([255, 240]);
            using var stream = new ScriptedStream([.. response]);
            using var session = NewSession(stream, options);
            (await session.RequestTerminalSpeedAsync(TimeSpan.FromSeconds(2))).Should().BeNull();
            lock (logs)
            {
                logs.Should().Contain(m => m.StartsWith("environ-cap:", StringComparison.Ordinal));
            }
        }

        [Fact]
        public void Start_NegativeMaxConcurrentSessions_Throws()
        {
            var options = new TelnetServerOptions { MaxConcurrentSessions = -1 };
            using var server = new TelnetServer(0, options);
            Assert.Throws<ArgumentOutOfRangeException>(() => server.Start());
        }
    }
}
