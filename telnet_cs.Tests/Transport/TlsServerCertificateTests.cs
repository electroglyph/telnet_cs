// G1 per-handshake certificate callback pins: rotation without restart,
// null-fallback to ServerCertificate, throwing callbacks failing the
// handshake like any failed handshake (reservation released, socket
// closed), missing-cert plaintext, and the autodetect path consulting the
// callback. All loopback with in-test self-signed certs, every wait
// bounded.
namespace telnet_cs.Tests
{
    using System;
    using System.Net.Security;
    using System.Security.Cryptography;
    using System.Security.Cryptography.X509Certificates;
    using System.Threading;
    using System.Threading.Tasks;
    using FluentAssertions;
    using Xunit;
    using telnet_cs.Client;
    using telnet_cs.Server;

    public class TlsServerCertificateTests
    {
        private static X509Certificate2 CreateSelfSignedCert()
        {
            using var key = ECDsa.Create();
            return new CertificateRequest(
                "CN=localhost", key, HashAlgorithmName.SHA256).CreateSelfSigned(
                DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        }

        private static TelnetClientOptions TlsClientOptions(Action<string?> onCertificate)
        {
            return new TelnetClientOptions
            {
                UseTls = true,
                TlsValidationCallback = (_, certificate, _, _) =>
                {
                    onCertificate(certificate?.GetCertHashString());
                    return true;
                },
            };
        }

        [Fact]
        public async Task GetServerCertificate_RotateBetweenAccepts_UsesCurrentCertPerHandshake()
        {
            using var cert1 = CreateSelfSignedCert();
            using var cert2 = CreateSelfSignedCert();
            X509Certificate2? current = cert1;
            var options = new TelnetServerOptions
            {
                MaxConnectionsPerIp = 0,
                LoginAttemptDelay = TimeSpan.Zero,
                HandshakeTimeout = TimeSpan.FromSeconds(30),
                GetServerCertificate = () => current,
            };
            using var server = new TelnetServer(0, options);
            server.Start();

            var firstAccept = server.AcceptSessionAsync(CancellationToken.None);
            string? firstThumbprint = null;
            using var firstClient = await Client.ConnectAsync(
                "127.0.0.1", server.Port, TlsClientOptions(t => firstThumbprint = t),
                CancellationToken.None, TimeSpan.FromSeconds(10));
            using var first = await firstAccept;
            firstThumbprint.Should().Be(cert1.Thumbprint);

            // Rotate without restart: the next handshake picks up cert2.
            current = cert2;
            var secondAccept = server.AcceptSessionAsync(CancellationToken.None);
            string? secondThumbprint = null;
            using var secondClient = await Client.ConnectAsync(
                "127.0.0.1", server.Port, TlsClientOptions(t => secondThumbprint = t),
                CancellationToken.None, TimeSpan.FromSeconds(10));
            using var second = await secondAccept;
            secondThumbprint.Should().Be(cert2.Thumbprint);
        }

        [Fact]
        public async Task GetServerCertificate_NullResult_FallsBackToServerCertificate()
        {
            using var cert = CreateSelfSignedCert();
            var options = new TelnetServerOptions
            {
                MaxConnectionsPerIp = 0,
                LoginAttemptDelay = TimeSpan.Zero,
                HandshakeTimeout = TimeSpan.FromSeconds(30),
                ServerCertificate = cert,
                GetServerCertificate = () => null,
            };
            using var server = new TelnetServer(0, options);
            server.Start();

            var accept = server.AcceptSessionAsync(CancellationToken.None);
            string? thumbprint = null;
            using var client = await Client.ConnectAsync(
                "127.0.0.1", server.Port, TlsClientOptions(t => thumbprint = t),
                CancellationToken.None, TimeSpan.FromSeconds(10));
            using var session = await accept;
            thumbprint.Should().Be(cert.Thumbprint);
            session.IsConnected.Should().BeTrue();
        }

        [Fact]
        public async Task GetServerCertificate_Throwing_FailsHandshakeReleasesReservationAndCloses()
        {
            using var cert = CreateSelfSignedCert();
            X509Certificate2? current = cert;
            bool shouldThrow = true;
            var options = new TelnetServerOptions
            {
                MaxConnectionsPerIp = 0,
                LoginAttemptDelay = TimeSpan.Zero,
                HandshakeTimeout = TimeSpan.FromSeconds(30),
                MaxConcurrentSessions = 1,
                GetServerCertificate = () => shouldThrow
                    ? throw new InvalidOperationException("boom")
                    : current,
            };
            using var server = new TelnetServer(0, options);
            server.Start();

            var badAccept = server.AcceptSessionAsync(CancellationToken.None);
            var badClientTask = Client.ConnectAsync(
                "127.0.0.1", server.Port, TlsClientOptions(_ => { }),
                CancellationToken.None, TimeSpan.FromSeconds(10));
            Func<Task> acceptAct = () => badAccept;
            await acceptAct.Should().ThrowAsync<InvalidOperationException>().WithMessage("boom");

            var firstDone = await Task.WhenAny(badClientTask, Task.Delay(TimeSpan.FromSeconds(10)));
            firstDone.Should().Be(badClientTask, "the server closes the socket on a failed handshake");
            Func<Task> clientAct = () => badClientTask;
            await clientAct.Should().ThrowAsync<Exception>();

            // Recovery on the same server: the failed accept released its
            // reservation (capacity 1 would refuse otherwise) and the
            // listener still serves.
            shouldThrow = false;
            var goodAccept = server.AcceptSessionAsync(CancellationToken.None);
            string? thumbprint = null;
            using var goodClient = await Client.ConnectAsync(
                "127.0.0.1", server.Port, TlsClientOptions(t => thumbprint = t),
                CancellationToken.None, TimeSpan.FromSeconds(10));
            using var good = await goodAccept;
            thumbprint.Should().Be(cert.Thumbprint);
            good.IsConnected.Should().BeTrue();
        }

        [Fact]
        public async Task GetServerCertificate_NullEverywhere_KeepsPlaintext()
        {
            var options = new TelnetServerOptions
            {
                MaxConnectionsPerIp = 0,
                LoginAttemptDelay = TimeSpan.Zero,
                HandshakeTimeout = TimeSpan.FromSeconds(30),
                GetServerCertificate = () => null,
            };
            using var server = new TelnetServer(0, options);
            server.Start();

            var accept = server.AcceptSessionAsync(CancellationToken.None);
            using var client = await Client.ConnectAsync("127.0.0.1", server.Port);
            using var session = await accept;
            await client.WriteAsync("plain");
            (await session.ReadAsync(TimeSpan.FromSeconds(5))).Should().Be("plain");
        }

        [Fact]
        public async Task GetServerCertificate_AutoDetect_ServesTlsAndPlaintextOnOnePort()
        {
            using var cert = CreateSelfSignedCert();
            var options = new TelnetServerOptions
            {
                MaxConnectionsPerIp = 0,
                LoginAttemptDelay = TimeSpan.Zero,
                HandshakeTimeout = TimeSpan.FromSeconds(30),
                TlsAutoDetect = TimeSpan.FromSeconds(10),
                GetServerCertificate = () => cert,
            };
            using var server = new TelnetServer(0, options);
            server.Start();

            var firstAccept = server.AcceptSessionAsync(CancellationToken.None);
            string? thumbprint = null;
            using var tlsClient = await Client.ConnectAsync(
                "127.0.0.1", server.Port, TlsClientOptions(t => thumbprint = t),
                CancellationToken.None, TimeSpan.FromSeconds(10));
            using var first = await firstAccept;
            thumbprint.Should().Be(cert.Thumbprint);
            await tlsClient.WriteAsync("secure");
            (await first.ReadAsync(TimeSpan.FromSeconds(5))).Should().Be("secure");

            var secondAccept = server.AcceptSessionAsync(CancellationToken.None);
            using var plainClient = await Client.ConnectAsync("127.0.0.1", server.Port);
            using var second = await secondAccept;
            await plainClient.WriteAsync("plain");
            (await second.ReadAsync(TimeSpan.FromSeconds(5))).Should().Be("plain");
        }
    }
}
