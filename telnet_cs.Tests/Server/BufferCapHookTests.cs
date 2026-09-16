// E1 buffer-cap hook pins: OnBufferCap fires alongside the buffer-cap log
// line on pump appends and collector stash-backs, at most once per
// accumulation, with the endpoint/buffered/cap values. The fail-closed
// close is unchanged and hook exceptions never escape. Hermetic: duplex or
// ScriptedStream, plus one loopback pin for the real endpoint string.
namespace telnet_cs.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System.Net.Sockets;
    using System.Threading;
    using System.Threading.Tasks;
    using FluentAssertions;
    using Xunit;
    using telnet_cs.Server;
    using telnet_cs.Transport;

    public class BufferCapHookTests
    {
        private sealed class HookLog
        {
            private readonly Lock mutex = new();
            private readonly List<BufferCapEvent> events = new();

            public void Handler(BufferCapEvent ev)
            {
                lock (mutex)
                {
                    events.Add(ev);
                }
            }

            public int Count
            {
                get
                {
                    lock (mutex)
                    {
                        return events.Count;
                    }
                }
            }

            public BufferCapEvent First
            {
                get
                {
                    lock (mutex)
                    {
                        return events[0];
                    }
                }
            }
        }

        private static TelnetServerOptions CappedOptions(HookLog log, int cap)
        {
            return new TelnetServerOptions
            {
                MaxConnectionsPerIp = 0,
                LoginAttemptDelay = TimeSpan.Zero,
                HandshakeTimeout = TimeSpan.FromSeconds(30),
                MaxBufferedTextChars = cap,
                OnBufferCap = log.Handler,
            };
        }

        [Fact]
        public void BufferCapEvent_CarriesEndpointBufferedCap()
        {
            var ev = new BufferCapEvent("127.0.0.1:1", 100, 64);
            ev.EndPoint.Should().Be("127.0.0.1:1");
            ev.Buffered.Should().Be(100);
            ev.Cap.Should().Be(64);
            ev.Should().Be(new BufferCapEvent("127.0.0.1:1", 100, 64));
        }

        [Fact]
        public async Task PumpFlood_FiresHookOnceWithLogValuesAndCloses()
        {
            var log = new HookLog();
            var logs = new List<string>();
            var options = CappedOptions(log, 16);
            options.Log = m => { lock (logs) { logs.Add(m); } };
            using var session = new ServerSession(new ScriptedStream(new string('A', 1024)), options, CancellationToken.None);

            var sw = Stopwatch.StartNew();
            while (log.Count == 0 && sw.Elapsed < TimeSpan.FromSeconds(5))
            {
                await Task.Delay(50);
            }

            log.Count.Should().Be(1);
            var ev = log.First;
            ev.Cap.Should().Be(16);
            ev.Buffered.Should().BeGreaterThan(16);
            ev.EndPoint.Should().Be("unknown");
            session.IsConnected.Should().BeFalse("the cap still closes fail-closed");
            lock (logs)
            {
                logs.Should().ContainSingle(m => m.StartsWith("buffer-cap: endpoint=unknown buffered=", StringComparison.Ordinal));
            }

            await Task.Delay(300);
            log.Count.Should().Be(1, "no per-byte refire while still over cap");
        }

        [Fact]
        public async Task PumpFlood_HookException_SwallowedAndSessionStillCloses()
        {
            int calls = 0;
            var options = new TelnetServerOptions
            {
                MaxBufferedTextChars = 16,
                OnBufferCap = _ =>
                {
                    Interlocked.Increment(ref calls);
                    throw new InvalidOperationException("boom");
                },
            };
            using var session = new ServerSession(new ScriptedStream(new string('A', 1024)), options, CancellationToken.None);

            var sw = Stopwatch.StartNew();
            while (Volatile.Read(ref calls) == 0 && sw.Elapsed < TimeSpan.FromSeconds(5))
            {
                await Task.Delay(50);
            }

            Volatile.Read(ref calls).Should().Be(1);
            session.IsConnected.Should().BeFalse("a throwing hook must not prevent the fail-closed close");
        }

        [Fact]
        public async Task UnderCap_NeverFires()
        {
            var log = new HookLog();
            using var session = new ServerSession(new ScriptedStream("small"), CappedOptions(log, 65536), CancellationToken.None);
            (await session.ReadAsync(TimeSpan.FromSeconds(2))).Should().Be("small");
            await Task.Delay(300);
            log.Count.Should().Be(0);
        }

        [Fact]
        public async Task CollectorStashBack_FiresHook()
        {
            // The text arrives after the request starts polling, while the
            // pump stands down for active reads, so the request's stash-back
            // path (not the pump append) trips the cap.
            var log = new HookLog();
            using var stream = new ScriptedStream();
            using var session = new ServerSession(stream, CappedOptions(log, 16), CancellationToken.None);
            var request = session.RequestTerminalSpeedAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(200);
            stream.Enqueue(Enumerable.Repeat((int)'A', 100).ToArray());
            await request;

            var sw = Stopwatch.StartNew();
            while (log.Count == 0 && sw.Elapsed < TimeSpan.FromSeconds(5))
            {
                await Task.Delay(50);
            }

            log.Count.Should().Be(1);
            log.First.Cap.Should().Be(16);
            log.First.Buffered.Should().BeGreaterThan(16);
        }

        [Fact]
        public async Task Duplex_Flood_FiresHookAndCloses()
        {
            var log = new HookLog();
            var (clientStream, serverStream) = DuplexPipe.Create();
            using var session = new ServerSession(serverStream, CappedOptions(log, 64), CancellationToken.None);
            using var client = new telnet_cs.Client.Client(clientStream, CancellationToken.None);

            for (int i = 0; i < 4; i++)
            {
                await client.WriteAsync(new string('A', 512), CancellationToken.None);
                await Task.Delay(100);
            }

            var sw = Stopwatch.StartNew();
            while (log.Count == 0 && sw.Elapsed < TimeSpan.FromSeconds(5))
            {
                await Task.Delay(50);
            }

            log.Count.Should().Be(1, "one fire per accumulation, never per byte over");
            log.First.Cap.Should().Be(64);
            log.First.Buffered.Should().BeGreaterThan(64);
            sw.Restart();
            while (session.IsConnected && sw.Elapsed < TimeSpan.FromSeconds(5))
            {
                await Task.Delay(50);
            }

            session.IsConnected.Should().BeFalse("flooded data closes the session instead of being silently dropped");
        }

        [Fact]
        public async Task Loopback_Flood_FiresHookWithRealEndpoint()
        {
            var log = new HookLog();
            var options = CappedOptions(log, 32);
            using var server = new TelnetServer(0, options);
            server.Start();

            using var raw = new System.Net.Sockets.TcpClient();
            var accept = server.AcceptSessionAsync(CancellationToken.None);
            await raw.ConnectAsync("127.0.0.1", server.Port);
            using var session = await accept;

            var peer = raw.GetStream();
            var flood = new byte[2048];
            for (int i = 0; i < flood.Length; i++)
            {
                flood[i] = (byte)'A';
            }

            await peer.WriteAsync(flood, 0, flood.Length);
            var sw = Stopwatch.StartNew();
            while (log.Count == 0 && sw.Elapsed < TimeSpan.FromSeconds(5))
            {
                await Task.Delay(50);
            }

            log.Count.Should().Be(1);
            log.First.Cap.Should().Be(32);
            log.First.Buffered.Should().BeGreaterThan(32);
            log.First.EndPoint.Should().StartWith("127.0.0.1:");
            sw.Restart();
            while (session.IsConnected && sw.Elapsed < TimeSpan.FromSeconds(5))
            {
                await Task.Delay(50);
            }

            session.IsConnected.Should().BeFalse();
        }
    }
}
