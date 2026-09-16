// B1 accept-split pins: AcceptTcpAsync admits (filters/caps) and TLS-wraps
// with no negotiation bytes out; NegotiateAsync sends the opening preset on
// the SendOpeningPresetAsync path. Refusals carry zero wire bytes, an
// abandoned pending session releases its reservation on Dispose, and the
// combined AcceptSessionAsync path is unchanged. Hermetic: loopback only,
// OS-assigned ports, every wait bounded.
namespace telnet_cs.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Net.Sockets;
    using System.Threading;
    using System.Threading.Tasks;
    using FluentAssertions;
    using Xunit;
    using telnet_cs.Server;

    public class AcceptSplitTests
    {
        private static TelnetServerOptions LowFrictionOptions()
        {
            return new TelnetServerOptions
            {
                MaxConnectionsPerIp = 0,
                LoginAttemptDelay = TimeSpan.Zero,
                HandshakeTimeout = TimeSpan.FromSeconds(30),
            };
        }

        private static void AssertClosedWithNoBytes(TcpClient raw)
        {
            var peer = raw.GetStream();
            peer.ReadTimeout = 5000;
            var buf = new byte[256];
            peer.Read(buf, 0, buf.Length).Should().Be(0);
        }

        private static byte[] DrainAvailable(TcpClient raw, TimeSpan budget)
        {
            var peer = raw.GetStream();
            peer.ReadTimeout = 500;
            var all = new List<byte>();
            var buf = new byte[256];
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < budget)
            {
                while (raw.Client.Available > 0)
                {
                    int read = peer.Read(buf, 0, Math.Min(buf.Length, raw.Client.Available));
                    if (read <= 0)
                    {
                        break;
                    }

                    for (int i = 0; i < read; i++)
                    {
                        all.Add(buf[i]);
                    }
                }

                Thread.Sleep(50);
            }

            return all.ToArray();
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

        private static byte[] ReadExact(TcpClient raw, int count)
        {
            var peer = raw.GetStream();
            peer.ReadTimeout = 5000;
            var buf = new byte[count];
            int got = 0;
            while (got < count)
            {
                int read = peer.Read(buf, got, count - got);
                read.Should().BeGreaterThan(0, "the server must send the opening preset");
                got += read;
            }

            return buf;
        }

        [Fact]
        public async Task AcceptTcpAsync_FilterFalse_ThrowsFilterSubtypeWithNoBytes()
        {
            var options = LowFrictionOptions();
            options.AcceptFilter = _ => false;
            using var server = new TelnetServer(0, options);
            server.Start();

            using var raw = new TcpClient();
            var accept = server.AcceptTcpAsync(CancellationToken.None);
            await raw.ConnectAsync("127.0.0.1", server.Port);
            var ex = await Assert.ThrowsAsync<ConnectionRefusedByFilterException>(() => accept);
            ex.Reason.Should().Be("filter-reject");
            server.RejectedFilterCount.Should().Be(1);
            AssertClosedWithNoBytes(raw);
        }

        [Fact]
        public async Task AcceptTcpAsync_OverCapacity_RefusedWithNoBytesAndDisposeReleasesReservation()
        {
            var options = LowFrictionOptions();
            options.MaxConcurrentSessions = 1;
            using var server = new TelnetServer(0, options);
            server.Start();

            using var raw1 = new TcpClient();
            var accept1 = server.AcceptTcpAsync(CancellationToken.None);
            await raw1.ConnectAsync("127.0.0.1", server.Port);
            var pending = await accept1;

            using var raw2 = new TcpClient();
            var accept2 = server.AcceptTcpAsync(CancellationToken.None);
            await raw2.ConnectAsync("127.0.0.1", server.Port);
            await Assert.ThrowsAsync<SessionCapacityException>(() => accept2);
            server.RejectedCapacityCount.Should().Be(1);
            AssertClosedWithNoBytes(raw2);

            // Abandoning the pending session frees its reservation: the next
            // accept is admitted instead of refused.
            pending.Dispose();
            using var raw3 = new TcpClient();
            var accept3 = server.AcceptTcpAsync(CancellationToken.None);
            await raw3.ConnectAsync("127.0.0.1", server.Port);
            using var session = await accept3;
            server.RejectedCapacityCount.Should().Be(1);
            using var negotiated = await server.NegotiateAsync(session, CancellationToken.None);
            negotiated.IsConnected.Should().BeTrue();
        }

        [Fact]
        public async Task SplitPath_SendsNothingUntilNegotiateThenPreset()
        {
            using var server = new TelnetServer(0, LowFrictionOptions());
            server.Start();

            using var raw = new TcpClient();
            var accept = server.AcceptTcpAsync(CancellationToken.None);
            await raw.ConnectAsync("127.0.0.1", server.Port);
            using var pending = await accept;
            raw.Client.Available.Should().Be(0, "AcceptTcpAsync must send no negotiation bytes");

            using var session = await server.NegotiateAsync(pending, CancellationToken.None);
            ReadExact(raw, 3).Should().Equal(new byte[] { 255, 253, 24 }, "the opening preset is DO TTYPE");
            session.IsConnected.Should().BeTrue();
        }

        [Fact]
        public async Task AcceptSessionAsync_CombinedPath_StillSendsPreset()
        {
            using var server = new TelnetServer(0, LowFrictionOptions());
            server.Start();

            using var raw = new TcpClient();
            var accept = server.AcceptSessionAsync(CancellationToken.None);
            await raw.ConnectAsync("127.0.0.1", server.Port);
            using var session = await accept;
            ReadExact(raw, 3).Should().Equal(new byte[] { 255, 253, 24 }, "the combined path is unchanged");
            session.IsConnected.Should().BeTrue();
        }

        [Fact]
        public async Task NegotiateAsync_ForeignSession_ThrowsAndOriginalServerStillNegotiates()
        {
            using var serverA = new TelnetServer(0, LowFrictionOptions());
            using var serverB = new TelnetServer(0, LowFrictionOptions());
            serverA.Start();
            serverB.Start();

            using var raw = new TcpClient();
            var accept = serverA.AcceptTcpAsync(CancellationToken.None);
            await raw.ConnectAsync("127.0.0.1", serverA.Port);
            using var pending = await accept;

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => serverB.NegotiateAsync(pending, CancellationToken.None));
            ex.Message.Should().Contain("AcceptTcpAsync");
            serverB.RejectedCapacityCount.Should().Be(0);
            serverB.RejectedFilterCount.Should().Be(0);

            using var session = await serverA.NegotiateAsync(pending, CancellationToken.None);
            ReadExact(raw, 3).Should().Equal(new byte[] { 255, 253, 24 });
        }

        [Fact]
        public async Task NegotiateAsync_Twice_ThrowsInvalidOperation()
        {
            using var server = new TelnetServer(0, LowFrictionOptions());
            server.Start();

            using var raw = new TcpClient();
            var accept = server.AcceptTcpAsync(CancellationToken.None);
            await raw.ConnectAsync("127.0.0.1", server.Port);
            using var pending = await accept;
            using var session = await server.NegotiateAsync(pending, CancellationToken.None);
            var first = DrainAvailable(raw, TimeSpan.FromMilliseconds(800));
            CountFrame(first, new byte[] { 255, 253, 24 }).Should().Be(1, "the opening preset goes out exactly once");

            await Assert.ThrowsAsync<InvalidOperationException>(() => server.NegotiateAsync(session, CancellationToken.None));
            var second = DrainAvailable(raw, TimeSpan.FromMilliseconds(800));
            CountFrame(second, new byte[] { 255, 253, 24 }).Should().Be(0, "the refused second negotiation sends nothing");
        }

        [Fact]
        public async Task NegotiateAsync_NullSession_ThrowsArgumentNull()
        {
            using var server = new TelnetServer(0, LowFrictionOptions());
            server.Start();

            await Assert.ThrowsAsync<ArgumentNullException>(() => server.NegotiateAsync(null!, CancellationToken.None));
        }

        [Fact]
        public async Task NegotiateAsync_DisposedServer_ThrowsObjectDisposed()
        {
            var server = new TelnetServer(0, LowFrictionOptions());
            server.Start();

            using var raw = new TcpClient();
            var accept = server.AcceptTcpAsync(CancellationToken.None);
            await raw.ConnectAsync("127.0.0.1", server.Port);
            using var pending = await accept;
            server.Dispose();

            await Assert.ThrowsAsync<ObjectDisposedException>(() => server.NegotiateAsync(pending, CancellationToken.None));
        }
    }
}
