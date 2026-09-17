// B2 refuse-reason pins: every refused accept maps to its exact subtype
// with zero wire bytes on a raw socket, counters bump once, and both
// log-line shapes stay byte-stable (success lines keep key=value form,
// reason lines keep the exception space form). Hermetic: loopback only,
// OS-assigned ports, every wait bounded.
namespace telnet_cs.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Net;
    using System.Net.Sockets;
    using System.Threading;
    using System.Threading.Tasks;
    using FluentAssertions;
    using Xunit;
    using telnet_cs.Server;

    public class RefuseReasonIntegrationTests
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

        [Fact]
        public void RefuseExceptions_CarryReasonAndEndpoint()
        {
            var endpoint = new IPEndPoint(IPAddress.Loopback, 1234);
            var capacity = new SessionCapacityException(endpoint);
            capacity.Reason.Should().Be("capacity");
            capacity.RemoteEndPoint.Should().Be(endpoint);
            capacity.Message.Should().Be("over-capacity: capacity endpoint 127.0.0.1:1234.");
            capacity.Should().BeAssignableTo<InvalidOperationException>();

            var perIp = new PerIpCapacityException(endpoint);
            perIp.Reason.Should().Be("per-ip");
            perIp.RemoteEndPoint.Should().Be(endpoint);
            perIp.Message.Should().Be("over-capacity: per-ip endpoint 127.0.0.1:1234.");
            perIp.Should().BeAssignableTo<InvalidOperationException>();

            var filter = new ConnectionRefusedByFilterException(endpoint);
            filter.Reason.Should().Be("filter-reject");
            filter.RemoteEndPoint.Should().Be(endpoint);
            filter.InnerException.Should().BeNull();
            filter.Should().BeAssignableTo<InvalidOperationException>();

            var inner = new InvalidOperationException("boom");
            var threw = new ConnectionRefusedByFilterException(endpoint, inner);
            threw.Reason.Should().Be("filter-threw");
            threw.InnerException.Should().BeSameAs(inner);

            var custom = new ConnectionRefusedByFilterException(endpoint, "banned-asn", null);
            custom.Reason.Should().Be("banned-asn");
        }

        [Fact]
        public async Task AcceptSessionAsync_FilterRefuse_ThrowsFilterSubtypeWithNoBytes()
        {
            var logs = new List<string>();
            var options = LowFrictionOptions();
            options.AcceptFilter = _ => new AcceptDecision(false);
            options.Log = m => { lock (logs) { logs.Add(m); } };
            using var server = new TelnetServer(0, options);
            server.Start();

            using var raw = new TcpClient();
            var accept = server.AcceptSessionAsync(CancellationToken.None);
            await raw.ConnectAsync("127.0.0.1", server.Port);
            var ex = await Assert.ThrowsAsync<ConnectionRefusedByFilterException>(() => accept);
            ex.Reason.Should().Be("filter-reject");
            ex.RemoteEndPoint.Should().NotBeNull();
            ex.InnerException.Should().BeNull();
            ex.Should().BeAssignableTo<InvalidOperationException>();
            server.RejectedFilterCount.Should().Be(1);
            server.RejectedCapacityCount.Should().Be(0);
            AssertClosedWithNoBytes(raw);
            lock (logs)
            {
                logs.Should().Contain(m => m.StartsWith("over-capacity: filter-reject endpoint=", StringComparison.Ordinal));
            }
        }

        [Fact]
        public async Task AcceptSessionAsync_FilterThrows_ThrowsFilterSubtypeWithInner()
        {
            var options = LowFrictionOptions();
            options.AcceptFilter = _ => throw new InvalidOperationException("boom");
            using var server = new TelnetServer(0, options);
            server.Start();

            using var raw = new TcpClient();
            var accept = server.AcceptSessionAsync(CancellationToken.None);
            await raw.ConnectAsync("127.0.0.1", server.Port);
            var ex = await Assert.ThrowsAsync<ConnectionRefusedByFilterException>(() => accept);
            ex.Reason.Should().Be("filter-threw");
            ex.InnerException.Should().NotBeNull();
            ex.InnerException!.Message.Should().Be("boom");
            server.RejectedFilterCount.Should().Be(1);
            AssertClosedWithNoBytes(raw);
        }

        [Fact]
        public async Task AcceptSessionAsync_OverCapacity_ThrowsCapacitySubtypeWithNoBytes()
        {
            var options = LowFrictionOptions();
            options.MaxConcurrentSessions = 1;
            using var server = new TelnetServer(0, options);
            server.Start();

            using var raw1 = new TcpClient();
            var accept1 = server.AcceptSessionAsync(CancellationToken.None);
            await raw1.ConnectAsync("127.0.0.1", server.Port);
            using var held = await accept1;

            using var raw2 = new TcpClient();
            var accept2 = server.AcceptSessionAsync(CancellationToken.None);
            await raw2.ConnectAsync("127.0.0.1", server.Port);
            var ex = await Assert.ThrowsAsync<SessionCapacityException>(() => accept2);
            ex.Reason.Should().Be("capacity");
            ex.RemoteEndPoint.Should().NotBeNull();
            ex.Should().BeAssignableTo<InvalidOperationException>();
            server.RejectedCapacityCount.Should().Be(1);
            server.RejectedPerIpCount.Should().Be(0);
            held.IsConnected.Should().BeTrue();
            AssertClosedWithNoBytes(raw2);
        }

        [Fact]
        public async Task AcceptSessionAsync_OverPerIp_ThrowsPerIpSubtypeWithNoBytes()
        {
            var options = LowFrictionOptions();
            options.MaxConcurrentSessions = 0;
            options.MaxConnectionsPerIp = 1;
            using var server = new TelnetServer(0, options);
            server.Start();

            using var raw1 = new TcpClient();
            var accept1 = server.AcceptSessionAsync(CancellationToken.None);
            await raw1.ConnectAsync("127.0.0.1", server.Port);
            using var held = await accept1;

            using var raw2 = new TcpClient();
            var accept2 = server.AcceptSessionAsync(CancellationToken.None);
            await raw2.ConnectAsync("127.0.0.1", server.Port);
            var ex = await Assert.ThrowsAsync<PerIpCapacityException>(() => accept2);
            ex.Reason.Should().Be("per-ip");
            ex.RemoteEndPoint.Should().NotBeNull();
            server.RejectedPerIpCount.Should().Be(1);
            server.RejectedCapacityCount.Should().Be(0);
            held.IsConnected.Should().BeTrue();
            AssertClosedWithNoBytes(raw2);
        }

        [Fact]
        public async Task AcceptSessionAsync_FilterRefuseCustomReason_ThrowsWithReasonInLog()
        {
            var logs = new List<string>();
            var options = LowFrictionOptions();
            options.AcceptFilter = _ => new AcceptDecision(false, "banned-asn");
            options.Log = m => { lock (logs) { logs.Add(m); } };
            using var server = new TelnetServer(0, options);
            server.Start();

            using var raw = new TcpClient();
            var accept = server.AcceptSessionAsync(CancellationToken.None);
            await raw.ConnectAsync("127.0.0.1", server.Port);
            var ex = await Assert.ThrowsAsync<ConnectionRefusedByFilterException>(() => accept);
            ex.Reason.Should().Be("banned-asn");
            server.RejectedFilterCount.Should().Be(1);
            AssertClosedWithNoBytes(raw);
            lock (logs)
            {
                logs.Should().Contain(
                    m => m.StartsWith("over-capacity: filter-reject endpoint=", StringComparison.Ordinal)
                        && m.EndsWith(" reason=banned-asn", StringComparison.Ordinal));
            }
        }

        [Fact]
        public async Task AcceptSessionAsync_FilterAllow_Accepts()
        {
            var options = LowFrictionOptions();
            options.AcceptFilter = _ => new AcceptDecision(true);
            using var server = new TelnetServer(0, options);
            server.Start();

            using var raw = new TcpClient();
            var accept = server.AcceptSessionAsync(CancellationToken.None);
            await raw.ConnectAsync("127.0.0.1", server.Port);
            using var session = await accept;
            session.IsConnected.Should().BeTrue();
            server.RejectedFilterCount.Should().Be(0);
        }

        [Fact]
        public async Task AcceptSessionAsync_FilterEmptyReason_NormalizesToFilterReject()
        {
            var logs = new List<string>();
            var options = LowFrictionOptions();
            options.AcceptFilter = _ => new AcceptDecision(false, string.Empty);
            options.Log = m => { lock (logs) { logs.Add(m); } };
            using var server = new TelnetServer(0, options);
            server.Start();

            using var raw = new TcpClient();
            var accept = server.AcceptSessionAsync(CancellationToken.None);
            await raw.ConnectAsync("127.0.0.1", server.Port);
            var ex = await Assert.ThrowsAsync<ConnectionRefusedByFilterException>(() => accept);
            ex.Reason.Should().Be("filter-reject");
            lock (logs)
            {
                logs.Should().Contain(
                    m => m.EndsWith(" reason=filter-reject", StringComparison.Ordinal));
            }
        }

        [Fact]
        public async Task AcceptSessionAsync_FilterReceivesPeerEndpoint()
        {
            // The split-accept correlation contract: the filter is the only
            // public endpoint source, so it must observe the peer's address.
            var options = LowFrictionOptions();
            EndPoint? seen = null;
            options.AcceptFilter = endPoint => { seen = endPoint; return new AcceptDecision(true); };
            using var server = new TelnetServer(0, options);
            server.Start();

            using var raw = new TcpClient();
            var accept = server.AcceptSessionAsync(CancellationToken.None);
            await raw.ConnectAsync("127.0.0.1", server.Port);
            using var session = await accept;
            session.IsConnected.Should().BeTrue();
            seen.Should().BeOfType<IPEndPoint>()
                .Which.Address.Should().Be(IPAddress.Loopback);
        }

        [Fact]
        public async Task StatusAggregate_UsesKeyValueShape()
        {
            // Success lines keep key=value form while reason lines keep the
            // exception space form: downstream log parsing keys on both.
            var logs = new List<string>();
            var options = LowFrictionOptions();
            options.Log = m => { lock (logs) { logs.Add(m); } };
            options.StatusInterval = TimeSpan.FromMilliseconds(50);
            using var server = new TelnetServer(0, options);
            server.Start();

            using var raw = new TcpClient();
            var accept = server.AcceptSessionAsync(CancellationToken.None);
            await raw.ConnectAsync("127.0.0.1", server.Port);
            using var session = await accept;

            string? aggregate = null;
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < TimeSpan.FromSeconds(5))
            {
                lock (logs)
                {
                    aggregate = logs.Find(m => m.StartsWith("sessions=", StringComparison.Ordinal));
                }

                if (aggregate is not null)
                {
                    break;
                }

                await Task.Delay(50);
            }

            aggregate.Should().Be("sessions=1 rejected(capacity=0,per-ip=0,filter=0,queue-drop=0)");
        }
    }
}
