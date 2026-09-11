// TLS integration tests. All hermetic loopback: certs are generated in-test
// (self-signed ECDSA, no files), servers bind 127.0.0.1 on OS-assigned ports,
// and every wait is bounded. Covers both sides: TLS client (ConnectAsync) and
// TLS server (AcceptSessionAsync with ServerCertificate).
namespace telnet_cs.Tests
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Net;
    using System.Net.Security;
    using System.Net.Sockets;
    using System.Security.Authentication;
    using System.Security.Cryptography;
    using System.Security.Cryptography.X509Certificates;
    using System.Threading;
    using System.Threading.Tasks;
    using FluentAssertions;
    using Xunit;
    using telnet_cs.Client;
    using telnet_cs.Server;
    using telnet_cs.Transport;

    public class TlsTests
    {
        private static X509Certificate2 CreateSelfSignedCert()
        {
            using var key = ECDsa.Create();
            return new CertificateRequest(
                "CN=localhost", key, HashAlgorithmName.SHA256).CreateSelfSigned(
                DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        }

        private sealed class RecordingCallback
        {
            public bool Ran { get; private set; }

            public X509Certificate? Certificate { get; private set; }

            public SslPolicyErrors Errors { get; private set; }

            public bool Accept(
                object sender,
                X509Certificate? certificate,
                X509Chain? chain,
                SslPolicyErrors errors)
            {
                Ran = true;
                Certificate = certificate;
                Errors = errors;
                return true;
            }
        }

        private static TelnetServer StartTlsServer(X509Certificate2 cert)
        {
            var server = new TelnetServer(0, new TelnetServerOptions { ServerCertificate = cert });
            server.Start();
            return server;
        }

        private static TelnetClientOptions TlsOptions(RecordingCallback recorder)
        {
            return new TelnetClientOptions
            {
                UseTls = true,
                TlsValidationCallback = recorder.Accept,
            };
        }

        [Fact]
        public async Task TlsConnect_Handshake_SendsNoPlaintextTelnet()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;

            // The raw listener never answers, so the handshake blocks: short
            // timeout, assert on the server-side bytes only, and expect the
            // client task to throw InvalidOperationException (never await it
            // normally — it would hang to the default).
            using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var acceptTask = listener.AcceptTcpClientAsync(connectCts.Token);
            var clientTask = Client.ConnectAsync(
                "127.0.0.1", port,
                new TelnetClientOptions
                {
                    UseTls = true,
                    TlsValidationCallback = (_, _, _, _) => true,
                },
                CancellationToken.None,
                TimeSpan.FromSeconds(3));

            using var peer = await acceptTask;
            peer.ReceiveTimeout = 5000;
            using var raw = peer.GetStream();
            var first = new byte[2];
            int seen = 0;
            while (seen < 2)
            {
                int n = raw.Read(first, seen, 2 - seen);
                if (n == 0)
                {
                    break;
                }

                seen += n;
            }

            // TLS ClientHello record header (0x16 0x03), never IAC DO SGA.
            seen.Should().Be(2);
            first[0].Should().Be(0x16);
            first[1].Should().Be(0x03);

            Func<Task<Client>> act = () => clientTask;
            await act.Should().ThrowAsync<InvalidOperationException>();
        }

        [Fact]
        public async Task TlsConnect_LoginRoundTrip()
        {
            using var cert = CreateSelfSignedCert();
            var serverOptions = new TelnetServerOptions
            {
                ServerCertificate = cert,
            };
            using var server = new TelnetServer(0, serverOptions);
            server.Start();
            var acceptTask = server.AcceptSessionAsync(CancellationToken.None);
            var recorder = new RecordingCallback();
            var clientOptions = TlsOptions(recorder);
            using var client = await Client.ConnectAsync(
                "127.0.0.1", server.Port, clientOptions,
                CancellationToken.None, TimeSpan.FromSeconds(10));
            using var session = await acceptTask;

            recorder.Ran.Should().BeTrue();
            recorder.Certificate.Should().NotBeNull();

            var authTask = session.AuthenticateAsync(
                (u, p) => Task.FromResult(u == "bob" && p == "s3cret"),
                TimeSpan.FromSeconds(10));
            var loginTask = client.TryLoginAsync("bob", "s3cret", 10000);
            (await authTask).Should().BeTrue();

            await session.WriteLineAsync("welcome>");
            (await loginTask).Should().BeTrue();
        }

        [Fact]
        public async Task TlsValidationCallback_ReceivesChainAndErrors()
        {
            // One connection pins both shapes: dialling 127.0.0.1 against a
            // CN=localhost self-signed cert surfaces ChainErrors (self-signed)
            // AND NameMismatch (IP vs CN) together, as observed on the live
            // probe. Bitwise asserts (not equality): exact-error sets vary by
            // platform. Using "localhost" for an errors-free positive case
            // would need DNS; the IP dial keeps this hermetic.
            using var cert = CreateSelfSignedCert();
            using var server = StartTlsServer(cert);
            var acceptTask = server.AcceptSessionAsync(CancellationToken.None);
            var recorder = new RecordingCallback();
            using var client = await Client.ConnectAsync(
                "127.0.0.1", server.Port, TlsOptions(recorder),
                CancellationToken.None, TimeSpan.FromSeconds(10));
            using var session = await acceptTask;

            recorder.Ran.Should().BeTrue();
            (recorder.Errors & SslPolicyErrors.RemoteCertificateChainErrors)
                .Should().Be(SslPolicyErrors.RemoteCertificateChainErrors);
            (recorder.Errors & SslPolicyErrors.RemoteCertificateNameMismatch)
                .Should().Be(SslPolicyErrors.RemoteCertificateNameMismatch);
        }

        [Fact]
        public async Task TlsServer_PlaintextClient_FailsLoudly()
        {
            using var cert = CreateSelfSignedCert();
            using var server = StartTlsServer(cert);
            var acceptTask = server.AcceptSessionAsync(CancellationToken.None);

            // The plaintext client only WRITES its proactive negotiation, so
            // its ctor succeeds: the failure belongs to the server side,
            // which reads IAC bytes where a ClientHello should be. One line
            // of application data follows so the server holds more than a
            // 5-byte record header and fails the parse fast instead of
            // waiting on a short read.
            using var client = await Client.ConnectAsync("127.0.0.1", server.Port);
            await client.WriteLineAsync("hello");

            var completed = await Task.WhenAny(acceptTask, Task.Delay(TimeSpan.FromSeconds(10)));
            completed.Should().Be(acceptTask);
            Func<Task> act = () => acceptTask;
            await act.Should().ThrowAsync<AuthenticationException>();

            // Client side: no data ever arrives.
            (await client.ReadAsync(TimeSpan.FromSeconds(2))).Should().BeEmpty();
        }

        [Fact]
        public async Task TlsServer_TlsProtocolsMismatch_FailsHandshake()
        {
            // Server pins TLS 1.3 only, client offers 1.2 only: no overlap, so
            // both handshakes must fail. (A hardcoded-None server would accept
            // 1.2 and this would go green-red — the test pins the option flows
            // through.) Bounded waits on both sides; failure surfacing follows
            // the existing loud paths (server AuthenticationException).
            using var cert = CreateSelfSignedCert();
            using var server = new TelnetServer(0, new TelnetServerOptions
            {
                ServerCertificate = cert,
                TlsProtocols = SslProtocols.Tls13,
            });
            server.Start();
            var acceptTask = server.AcceptSessionAsync(CancellationToken.None);
            var clientTask = Client.ConnectAsync(
                "127.0.0.1", server.Port,
                new TelnetClientOptions
                {
                    UseTls = true,
                    TlsProtocols = SslProtocols.Tls12,
                    TlsValidationCallback = (_, _, _, _) => true,
                },
                CancellationToken.None,
                TimeSpan.FromSeconds(10));

            var first = await Task.WhenAny(acceptTask, Task.Delay(TimeSpan.FromSeconds(10)));
            first.Should().Be(acceptTask);
            Func<Task> serverAct = () => acceptTask;
            await serverAct.Should().ThrowAsync<AuthenticationException>();

            var second = await Task.WhenAny(clientTask, Task.Delay(TimeSpan.FromSeconds(10)));
            second.Should().Be(clientTask);
            Func<Task> clientAct = () => clientTask;
            await clientAct.Should().ThrowAsync<Exception>();
        }

        [Fact]
        public async Task TlsConnect_TimeoutCoversHandshake()
        {
            // Accepts but never handshakes (raw listener, no reads): the
            // handshake must die at the connect deadline, not hang.
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var acceptTask = listener.AcceptTcpClientAsync(connectCts.Token);

            Func<Task> act = () => Client.ConnectAsync(
                "127.0.0.1", port,
                new TelnetClientOptions
                {
                    UseTls = true,
                    TlsValidationCallback = (_, _, _, _) => true,
                },
                CancellationToken.None,
                TimeSpan.FromSeconds(1));
            await act.Should().ThrowAsync<InvalidOperationException>();

            using var peer = await acceptTask;
        }

        [Fact]
        public async Task TlsSynch_OverLoopback_PinsBehavior()
        {
            // Urgent delivery is delegated to the inner socket: the OOB byte
            // travels outside the TLS records but still arrives.
            using var cert = CreateSelfSignedCert();
            using var server = StartTlsServer(cert);
            var acceptTask = server.AcceptSessionAsync(CancellationToken.None);
            var recorder = new RecordingCallback();
            using var client = await Client.ConnectAsync(
                "127.0.0.1", server.Port, TlsOptions(recorder),
                CancellationToken.None, TimeSpan.FromSeconds(10));
            using var session = await acceptTask;

            await client.SendSynchAsync();
            var urgentTask = session.ReceiveUrgentAsync(CancellationToken.None);
            var completed = await Task.WhenAny(urgentTask, Task.Delay(TimeSpan.FromSeconds(10)));
            completed.Should().Be(urgentTask);
            (await urgentTask).Should().Be(242); // DM, the Synch data-mark octet.
        }

        [Fact]
        public async Task TlsSocket_ApplicationData_RoundTripsBothDirections()
        {
            // Raw transport bisection (no telnet layer): application bytes
            // must flow both ways through the SslStream decorator.
            using var cert = CreateSelfSignedCert();
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var acceptTask = listener.AcceptTcpClientAsync(connectCts.Token);

            using var rawClient = new System.Net.Sockets.TcpClient();
            await rawClient.ConnectAsync("127.0.0.1", port, connectCts.Token);
            using var accepted = await acceptTask;

            var clientAuthTask = TlsSocket.AuthenticateAsClientAsync(
                new telnet_cs.Transport.TcpClient(rawClient),
                new SslClientAuthenticationOptions
                {
                    TargetHost = "localhost",
                    RemoteCertificateValidationCallback = (_, _, _, _) => true,
                },
                connectCts.Token);
            var serverAuthTask = TlsSocket.AuthenticateAsServerAsync(
                new telnet_cs.Transport.TcpClient(accepted),
                new SslServerAuthenticationOptions { ServerCertificate = cert },
                connectCts.Token);
            using var clientSocket = await clientAuthTask;
            using var serverSocket = await serverAuthTask;

            using var clientStream = clientSocket.GetStream();
            using var serverStream = serverSocket.GetStream();

            byte[] ping = System.Text.Encoding.ASCII.GetBytes("ping-login: ");
            await clientStream.WriteAsync(ping, 0, ping.Length, CancellationToken.None);
            var sb = new System.Text.StringBuilder();
            serverSocket.ReceiveTimeout = 5000;
            for (int i = 0; i < ping.Length; i++)
            {
                sb.Append((char)serverStream.ReadByte());
            }

            sb.ToString().Should().Be("ping-login: ");

            byte[] pong = System.Text.Encoding.ASCII.GetBytes("pong");
            await serverStream.WriteAsync(pong, 0, pong.Length, CancellationToken.None);
            var sb2 = new System.Text.StringBuilder();
            clientSocket.ReceiveTimeout = 5000;
            for (int i = 0; i < pong.Length; i++)
            {
                sb2.Append((char)clientStream.ReadByte());
            }

            sb2.ToString().Should().Be("pong");
        }

        private static async Task<(TlsSocket ClientSocket, TlsSocket ServerSocket)> CreateTlsPairAsync(
            X509Certificate2 cert, CancellationToken cancellationToken)
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var acceptTask = listener.AcceptTcpClientAsync(cancellationToken);
            var rawClient = new System.Net.Sockets.TcpClient();
            await rawClient.ConnectAsync("127.0.0.1", port, cancellationToken);
            System.Net.Sockets.TcpClient accepted = await acceptTask;

            // Ownership of both raw sockets transfers to the TlsSockets;
            // the catch releases them when the handshake never completes.
            try
            {
                var clientAuthTask = TlsSocket.AuthenticateAsClientAsync(
                    new telnet_cs.Transport.TcpClient(rawClient),
                    new SslClientAuthenticationOptions
                    {
                        TargetHost = "localhost",
                        RemoteCertificateValidationCallback = (_, _, _, _) => true,
                    },
                    cancellationToken);
                var serverAuthTask = TlsSocket.AuthenticateAsServerAsync(
                    new telnet_cs.Transport.TcpClient(accepted),
                    new SslServerAuthenticationOptions { ServerCertificate = cert },
                    cancellationToken);
                return (await clientAuthTask, await serverAuthTask);
            }
            catch
            {
                rawClient.Dispose();
                accepted.Dispose();
                throw;
            }
        }

        private static async Task<byte[]> GateDrainAsync(TlsSocket socket, int expected, TimeSpan budget)
        {
            // Reads the way the protocol layer does: only when Available > 0.
            // Asserts the transport contract the availability gate relies on —
            // every sent byte eventually surfaces, in order — rather than the
            // wrapper's internals.
            var stream = socket.GetStream();
            var received = new List<byte>();
            var deadline = DateTime.UtcNow + budget;
            while (received.Count < expected && DateTime.UtcNow < deadline)
            {
                if (socket.Available > 0)
                {
                    try
                    {
                        int b = stream.ReadByte();
                        if (b < 0)
                        {
                            break;
                        }

                        received.Add((byte)b);
                    }
                    catch (IOException)
                    {
                        // Dry bulk read inside the drain (exact drain-size
                        // multiples end on one): the staged bytes keep the
                        // gate open, so keep polling instead of failing.
                        await Task.Delay(1);
                    }
                }
                else
                {
                    await Task.Delay(1);
                }
            }

            return received.ToArray();
        }

        [Fact]
        public async Task TlsSocket_BurstBeyondDrainSize_DeliversAllBytesInOrder()
        {
            // A single write larger than the drain size (32 KiB): without the
            // drain loop the record tail past 32 KiB would strand invisibly in
            // SslStream once the socket runs dry, and the gate would never
            // reopen for it.
            using var cert = CreateSelfSignedCert();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            (TlsSocket clientSocket, TlsSocket serverSocket) = await CreateTlsPairAsync(cert, cts.Token);
            using (clientSocket)
            using (serverSocket)
            {
                byte[] burst = new byte[40960];
                for (int i = 0; i < burst.Length; i++)
                {
                    burst[i] = (byte)(i % 251);
                }

                await serverSocket.GetStream().WriteAsync(burst, 0, burst.Length, CancellationToken.None);
                clientSocket.ReceiveTimeout = 2000;
                byte[] received = await GateDrainAsync(clientSocket, burst.Length, TimeSpan.FromSeconds(15));
                received.Should().Equal(burst);
            }
        }

        [Fact]
        public async Task TlsSocket_ExactDrainMultiple_DeliversAllBytesInOrder()
        {
            // Exactly one drain size: the loop's extra read finds a dry stream
            // and ends on a bounded timeout instead of stranding or hanging.
            using var cert = CreateSelfSignedCert();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            (TlsSocket clientSocket, TlsSocket serverSocket) = await CreateTlsPairAsync(cert, cts.Token);
            using (clientSocket)
            using (serverSocket)
            {
                byte[] burst = new byte[32768];
                for (int i = 0; i < burst.Length; i++)
                {
                    burst[i] = (byte)(i % 251);
                }

                await serverSocket.GetStream().WriteAsync(burst, 0, burst.Length, CancellationToken.None);
                clientSocket.ReceiveTimeout = 2000;
                byte[] received = await GateDrainAsync(clientSocket, burst.Length, TimeSpan.FromSeconds(15));
                received.Should().Equal(burst);
            }
        }

        [Fact]
        public async Task TlsOptions_Defaults_PlaintextUnchanged()
        {
            // A default options object interposes no TLS wrapper: the first
            // wire bytes are the plaintext proactive IAC DO SGA, observable
            // directly (BaseClient.ByteStream is protected, so bytes — not
            // internals — are the honest assertion).
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var acceptTask = listener.AcceptTcpClientAsync(connectCts.Token);
            using var client = await Client.ConnectAsync(
                "127.0.0.1", port, new TelnetClientOptions(),
                CancellationToken.None, TimeSpan.FromSeconds(10));

            using var peer = await acceptTask;
            peer.ReceiveTimeout = 5000;
            using var raw = peer.GetStream();
            var first = new byte[3];
            int seen = 0;
            while (seen < 3)
            {
                int n = raw.Read(first, seen, 3 - seen);
                if (n == 0)
                {
                    break;
                }

                seen += n;
            }

            seen.Should().Be(3);
            first.Should().Equal(255, 253, 3); // IAC DO SuppressGoAhead.
        }
    }
}
