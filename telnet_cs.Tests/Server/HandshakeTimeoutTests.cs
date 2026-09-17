// Handshake-deadline mapping pins: a deadline expiry surfaces as
// TimeoutException (never raw OperationCanceledException), a caller cancel
// stays cancelled, and the listener survives a stalled peer. All loopback
// with in-test self-signed certs, every wait bounded.
namespace telnet_cs.Tests
{
    using System;
    using System.Collections.Concurrent;
    using System.Net.Sockets;
    using System.Security.Cryptography;
    using System.Security.Cryptography.X509Certificates;
    using System.Threading;
    using System.Threading.Tasks;
    using FluentAssertions;
    using Xunit;
    using telnet_cs.Server;

    public class HandshakeTimeoutTests
    {
        private static X509Certificate2 CreateSelfSignedCert()
        {
            using var key = ECDsa.Create();
            return new CertificateRequest(
                "CN=localhost", key, HashAlgorithmName.SHA256).CreateSelfSigned(
                DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        }

        [Fact]
        public void IsHandshakeTimeout_FiredDeadline_MapsToTimeout()
        {
            // The losing side of the timer race, frozen: the deadline source
            // fired but the wall clock has not reached the deadline. A
            // wall-clock predicate answers false here (raw OCE escapes); the
            // latched token answers true (TimeoutException, deterministically).
            using var deadlineCts = new CancellationTokenSource();
            deadlineCts.Cancel();
            TelnetServer.IsHandshakeTimeout(CancellationToken.None, deadlineCts.Token).Should().BeTrue();
        }

        [Fact]
        public void IsHandshakeTimeout_LiveDeadline_DoesNotMap()
        {
            using var deadlineCts = new CancellationTokenSource();
            TelnetServer.IsHandshakeTimeout(CancellationToken.None, deadlineCts.Token).Should().BeFalse();
        }

        [Fact]
        public void IsHandshakeTimeout_CallerCancel_StaysCancelled()
        {
            // A caller cancel wins over a fired deadline: shutdown must stay
            // an OperationCanceledException, never a TimeoutException.
            using var callerCts = new CancellationTokenSource();
            using var deadlineCts = new CancellationTokenSource();
            callerCts.Cancel();
            deadlineCts.Cancel();
            TelnetServer.IsHandshakeTimeout(callerCts.Token, deadlineCts.Token).Should().BeFalse();
        }

        [Fact]
        public async Task AcceptTcpAsync_StalledTlsPeer_ThrowsTimeoutAndSurvives()
        {
            // A peer that starts TLS (0x16) then stalls past the handshake
            // deadline is dropped with TimeoutException — not raw
            // OperationCanceledException — and the listener serves the next
            // peer afterwards.
            using var cert = CreateSelfSignedCert();
            var log = new ConcurrentQueue<string>();
            var options = new TelnetServerOptions
            {
                MaxConnectionsPerIp = 0,
                LoginAttemptDelay = TimeSpan.Zero,
                HandshakeTimeout = TimeSpan.FromSeconds(1),
                ServerCertificate = cert,
                Log = log.Enqueue,
            };
            using var server = new TelnetServer(0, options);
            server.Start();

            static async Task StallOneAsync(int port)
            {
                using var stalled = new TcpClient();
                await stalled.ConnectAsync("127.0.0.1", port);
                using var stream = stalled.GetStream();
                stream.ReadTimeout = 15000;
                await stream.WriteAsync(new byte[] { 0x16 });
                // The server must close the stalled handshake itself.
                stream.ReadByte().Should().Be(-1);
            }

            var firstAccept = server.AcceptTcpAsync(CancellationToken.None);
            await StallOneAsync(server.Port);
            Func<Task> first = () => firstAccept.WaitAsync(TimeSpan.FromSeconds(15));
            (await first.Should().ThrowAsync<TimeoutException>())
                .WithMessage("*handshake-timeout*");

            var secondAccept = server.AcceptTcpAsync(CancellationToken.None);
            await StallOneAsync(server.Port);
            Func<Task> second = () => secondAccept.WaitAsync(TimeSpan.FromSeconds(15));
            (await second.Should().ThrowAsync<TimeoutException>())
                .WithMessage("*handshake-timeout*");

            log.Should().Contain(m => m.Contains("handshake-timeout", StringComparison.Ordinal));
        }

        [Fact]
        public async Task AcceptTcpAsync_CallerCancel_StaysCancelled()
        {
            using var server = new TelnetServer(0);
            server.Start();
            using var cts = new CancellationTokenSource();
            var accept = server.AcceptTcpAsync(cts.Token);
            cts.Cancel();
            Func<Task> act = () => accept;
            await act.Should().ThrowAsync<OperationCanceledException>();
        }

        [Fact]
        public async Task NegotiateAsync_PastDeadline_ThrowsTimeout()
        {
            // The preset path's pre-check is exact: once the deadline has
            // passed, negotiation fails closed with TimeoutException.
            var options = new TelnetServerOptions
            {
                MaxConnectionsPerIp = 0,
                LoginAttemptDelay = TimeSpan.Zero,
                HandshakeTimeout = TimeSpan.FromMilliseconds(200),
            };
            using var server = new TelnetServer(0, options);
            server.Start();
            using var peer = new TcpClient();
            await peer.ConnectAsync("127.0.0.1", server.Port);
            using var pending = await server.AcceptTcpAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
            await Task.Delay(TimeSpan.FromSeconds(1));
            Func<Task> negotiate = () => server.NegotiateAsync(pending, CancellationToken.None);
            (await negotiate.Should().ThrowAsync<TimeoutException>()).WithMessage("*handshake-timeout*");
        }
    }
}
