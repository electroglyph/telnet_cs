// F1 stable log-code contract: every prefix fires once per trigger
// through Settings.Log. Seven pins run on ScriptedStream (or a bare
// ByteStreamHandler for the storm guard); over-capacity and the status
// aggregate are server-owned, so those two pins use loopback — memory
// cannot produce a server-owned line.
namespace telnet_cs.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.IO.Compression;
    using System.Linq;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using FluentAssertions;
    using Xunit;
    using telnet_cs.IO;
    using telnet_cs.Protocol;
    using telnet_cs.Server;
    using telnet_cs.Transport;

    public class LogCodeContractTests
    {
        private static TelnetServerOptions LoggedOptions(List<string> logs)
        {
            return new TelnetServerOptions
            {
                MaxConnectionsPerIp = 0,
                LoginAttemptDelay = TimeSpan.Zero,
                HandshakeTimeout = TimeSpan.FromSeconds(30),
                Log = m => { lock (logs) { logs.Add(m); } },
            };
        }

        private static async Task WaitForLogAsync(List<string> logs, string prefix, TimeSpan budget)
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < budget)
            {
                lock (logs)
                {
                    foreach (var m in logs)
                    {
                        if (m.StartsWith(prefix, StringComparison.Ordinal))
                        {
                            return;
                        }
                    }
                }

                await Task.Delay(50);
            }

            throw new TimeoutException($"No log line starting with '{prefix}' within {budget}.");
        }

        private static byte[] ZlibCompress(string text)
        {
            using var ms = new MemoryStream();
            // Optimal: the 1 KiB payload compresses to a few bytes whose
            // output arrives as one burst past the cap. NoCompression would
            // stream out gradually while the session drains a byte per pass,
            // so the outstanding-bytes cap would legitimately never trip.
            using (var zlib = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            {
                zlib.Write(Encoding.ASCII.GetBytes(text));
            }

            return ms.ToArray();
        }

        [Fact]
        public async Task OverCapacity_FiresOnRefuse()
        {
            var logs = new List<string>();
            var options = LoggedOptions(logs);
            options.MaxConcurrentSessions = 1;
            using var server = new TelnetServer(0, options);
            server.Start();

            using var first = new System.Net.Sockets.TcpClient();
            var accept = server.AcceptSessionAsync(CancellationToken.None);
            await first.ConnectAsync("127.0.0.1", server.Port);
            using var held = await accept;

            using var second = new System.Net.Sockets.TcpClient();
            var refused = server.AcceptSessionAsync(CancellationToken.None);
            await second.ConnectAsync("127.0.0.1", server.Port);
            (await Record.ExceptionAsync(() => refused)).Should().BeOfType<SessionCapacityException>();

            await WaitForLogAsync(logs, "over-capacity:", TimeSpan.FromSeconds(3));
        }

        [Fact]
        public async Task HandshakeTimeout_FiresOnBlownDeadline()
        {
            var logs = new List<string>();
            var options = LoggedOptions(logs);
            options.HandshakeTimeout = TimeSpan.FromMilliseconds(300);
            using var server = new TelnetServer(0, options);
            server.Start();

            using var raw = new System.Net.Sockets.TcpClient();
            var accept = server.AcceptSessionAsync(CancellationToken.None);
            await raw.ConnectAsync("127.0.0.1", server.Port);
            using var session = await accept;

            await WaitForLogAsync(logs, "handshake-timeout:", TimeSpan.FromSeconds(4));
            var sw = Stopwatch.StartNew();
            while (session.IsConnected && sw.Elapsed < TimeSpan.FromSeconds(4))
            {
                await Task.Delay(50);
            }

            session.IsConnected.Should().BeFalse();
        }

        [Fact]
        public async Task BufferCap_FiresOnFlood()
        {
            var logs = new List<string>();
            var options = LoggedOptions(logs);
            options.MaxBufferedTextChars = 8;
            using var session = new ServerSession(new ScriptedStream(new string('A', 64)), options, CancellationToken.None);

            await WaitForLogAsync(logs, "buffer-cap:", TimeSpan.FromSeconds(5));
            session.IsConnected.Should().BeFalse("the cap still closes fail-closed");
        }

        [Fact]
        public async Task StormGuard_FiresPastThreshold()
        {
            // Alternating DO/DONT for unimplemented option 7: the guard
            // trips past 100 frames/s, so 110 back-to-back frames exceed it.
            var frames = new List<int>();
            for (int i = 0; i < 110; i++)
            {
                frames.AddRange([255, i % 2 == 0 ? 253 : 254, 7]);
            }

            using var stream = new ScriptedStream([.. frames]);
            using var cts = new CancellationTokenSource();
            using var handler = new ByteStreamHandler(stream, cts, 1);
            handler.StormGuard = new NegotiationStormGuard();
            var logs = new List<string>();
            handler.Log = m => logs.Add(m);

            int spins = 0;
            while (stream.Available > 0 && spins++ < 500)
            {
                await handler.ReadAsync(TimeSpan.FromMilliseconds(50));
            }

            stream.Available.Should().Be(0);
            logs.Should().Contain(m => m.StartsWith("storm-guard:", StringComparison.Ordinal));
        }

        [Fact]
        public async Task AuthExhausted_FiresOnBadCredentialsWithoutPassword()
        {
            var logs = new List<string>();
            var options = LoggedOptions(logs);
            options.MaxLoginAttempts = 1;
            using var session = new ServerSession(new ScriptedStream("alice\ns3cret-pw\n"), options, CancellationToken.None);
            (await session.AuthenticateAsync((u, p) => Task.FromResult(false), TimeSpan.FromSeconds(5))).Should().BeFalse();

            logs.Should().Contain(m => m.StartsWith("auth-exhausted:", StringComparison.Ordinal));
            string all = string.Concat(logs);
            all.Should().Contain("bad-credentials");
            all.Should().NotContain("s3cret-pw");
        }

        [Fact]
        public async Task EnvironCap_FiresOnOversizedAnswer()
        {
            var logs = new List<string>();
            var options = LoggedOptions(logs);
            options.MaxEnvironValueChars = 4;
            var response = new List<int> { 255, 250, 32, 0 };
            response.AddRange(Encoding.Latin1.GetBytes("1234567890").Select(b => (int)b));
            response.AddRange([255, 240]);
            using var session = new ServerSession(new ScriptedStream([.. response]), options, CancellationToken.None);
            // Explicit TSPEED requests are WILL-gated (no SEND without the
            // peer's WILL), so establish the peer agreement first — the
            // over-cap IS is then consumed and capped.
            session.Negotiation.ReceivedWill((int)Options.TerminalSpeed, agree: true);
            (await session.RequestTerminalSpeedAsync(TimeSpan.FromSeconds(2))).Should().BeNull();

            logs.Should().Contain(m => m.StartsWith("environ-cap:", StringComparison.Ordinal));
        }

        [Fact]
        public async Task MudCap_FiresOnOverKeyCap()
        {
            var logs = new List<string>();
            var options = LoggedOptions(logs);
            options.MaxMudKeys = 2;
            var reads = new List<int> { 255, 251, 93, 255, 250, 93 };
            reads.AddRange(Encoding.Latin1.GetBytes("c\0a\0b\0c\0").Select(b => (int)b));
            reads.AddRange([255, 240]);
            using var session = new ServerSession(new ScriptedStream([.. reads]), options, CancellationToken.None);
            (await session.ReadAsync(TimeSpan.FromMilliseconds(500))).Should().BeEmpty();

            logs.Should().Contain(
                m => m.StartsWith("mud-cap:", StringComparison.Ordinal)
                    && m.Contains("option=zmp")
                    && m.Contains("cap=2"));
        }

        [Fact]
        public async Task MccpOutputCap_FiresOnOverDecompressedCap()
        {
            // Server-side MCCP3 polarity: peer DO 87 earns WILL, empty SB 87
            // arms inflation, and a 1 KiB payload past a 64-byte cap fails
            // the stream with the cap log through session Log.
            var logs = new List<string>();
            var options = LoggedOptions(logs);
            options.EnableMccp = true;
            options.MaxDecompressedBytes = 64;
            using var stream = new ScriptedStream();
            stream.Enqueue([255, 253, 87, 255, 250, 87, 255, 240]);
            stream.Enqueue(ZlibCompress(new string('a', 1024)).Select(b => (int)b).ToArray());
            using var session = new ServerSession(stream, options, CancellationToken.None);
            for (int i = 0; i < 5; i++)
            {
                await session.ReadAsync(TimeSpan.FromMilliseconds(300));
            }

            logs.Should().Contain(m => m.StartsWith("mccp-output-cap:", StringComparison.Ordinal));
        }

        [Fact]
        public async Task StatusAggregate_EveryTick_DetailOnChangeOnly()
        {
            // Server-owned cadence: the aggregate fires every tick while a
            // per-session detail line fires only when its counters changed.
            // A tracked session needs a real accept, so this pin is
            // loopback: one idle session, short interval, count shapes.
            var logs = new List<string>();
            var options = LoggedOptions(logs);
            options.StatusInterval = TimeSpan.FromMilliseconds(200);
            using var server = new TelnetServer(0, options);
            server.Start();

            using var raw = new System.Net.Sockets.TcpClient();
            var accept = server.AcceptSessionAsync(CancellationToken.None);
            await raw.ConnectAsync("127.0.0.1", server.Port);
            using var session = await accept;

            await Task.Delay(900);
            int aggregates;
            int details;
            lock (logs)
            {
                aggregates = logs.Count(m => m.StartsWith("sessions=", StringComparison.Ordinal));
                details = logs.Count(m => m.Contains("(rx=", StringComparison.Ordinal));
            }

            aggregates.Should().BeGreaterThanOrEqualTo(2, "the aggregate logs every tick");
            details.Should().BeLessThanOrEqualTo(1, "idle detail fires at most once, on the preset-bytes change");
        }
    }
}
