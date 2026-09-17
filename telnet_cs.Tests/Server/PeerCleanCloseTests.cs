// A clean peer close (FIN, TLS close_notify, pipe close) is definitive
// end-of-stream: the next read reports it AND the session drops to
// disconnected, so handlers polling IsConnected terminate instead of
// spinning on empty reads forever.
namespace telnet_cs.Tests
{
    using System;
    using System.IO;
    using System.Net.Sockets;
    using System.Threading;
    using System.Threading.Tasks;
    using FluentAssertions;
    using Xunit;
    using telnet_cs.Server;
    using telnet_cs.Transport;

    public class PeerCleanCloseTests
    {
        [Fact]
        public async Task ReadAsync_PeerPipeClose_ReportsDisconnect()
        {
            // HandshakeTimeout disabled: the peer stays silent by design,
            // so only its close may end the session (no timeout before it).
            var options = new TelnetServerOptions
            {
                HandshakeTimeout = Timeout.InfiniteTimeSpan,
            };
            var (peer, wire) = InMemoryPipe.Create();
            using var session = new ServerSession(wire, options, CancellationToken.None);

            // Clean close with nothing in flight: the duplex peer will
            // never send again, exactly like a socket after FIN.
            peer.Close();

            string read = await session.ReadAsync(TimeSpan.FromSeconds(10));
            read.Should().BeEmpty();
            session.IsConnected.Should().BeFalse();
        }

        [Fact]
        public async Task ReadAsync_TcpPeerFin_ReportsDisconnect()
        {
            // HandshakeTimeout disabled: the peer stays silent by design,
            // so only its FIN may end the session (no timeout before it).
            var options = new TelnetServerOptions
            {
                HandshakeTimeout = Timeout.InfiniteTimeSpan,
            };
            using var server = new TelnetServer(0, options);
            server.Start();
            var acceptTask = server.AcceptSessionAsync(CancellationToken.None);
            using var client = new System.Net.Sockets.TcpClient();
            await client.ConnectAsync("127.0.0.1", server.Port);
            using var session = await acceptTask.WaitAsync(TimeSpan.FromSeconds(10));

            // Arms the deferred opening negotiation; the peer must drain
            // it so its later close stays a clean FIN (unread bytes would
            // turn it into an RST instead).
            var readTask = session.ReadAsync(TimeSpan.FromSeconds(10));
            Drain(client);

            // Clean FIN: nothing was written after the drain, so no RST.
            client.Client.Shutdown(SocketShutdown.Both);
            client.Close();

            string read = await readTask.WaitAsync(TimeSpan.FromSeconds(10));
            read.Should().BeEmpty();
            session.IsConnected.Should().BeFalse();
        }

        private static void Drain(System.Net.Sockets.TcpClient client)
        {
            DrainStream(client.GetStream());
        }

        private static void DrainStream(System.IO.Stream stream)
        {
            // Idle-timed drain: the opening preset length is an
            // implementation detail, so read until quiet instead.
            stream.ReadTimeout = 500;
            var buffer = new byte[4096];
            try
            {
                while (stream.Read(buffer, 0, buffer.Length) > 0)
                {
                }
            }
            catch (IOException)
            {
                // ReadTimeout expired: the preset fully arrived.
            }
        }

        [Fact]
        public async Task ReadAsync_TlsPeerCloseNotify_ReportsDisconnect()
        {
            using var cert = CreateSelfSignedCert();
            var serverOptions = new TelnetServerOptions
            {
                ServerCertificate = cert,
                // The peer stays text-silent by design: only its
                // close_notify may end the session (no timeout before it).
                HandshakeTimeout = Timeout.InfiniteTimeSpan,
            };
            using var server = new TelnetServer(0, serverOptions);
            server.Start();
            var acceptTask = server.AcceptSessionAsync(CancellationToken.None);
            using var tls = new System.Net.Sockets.TcpClient();
            await tls.ConnectAsync("127.0.0.1", server.Port);
            using var ssl = new System.Net.Security.SslStream(
                tls.GetStream(), false, (_, _, _, _) => true);
            await ssl.AuthenticateAsClientAsync("localhost");
            using var session = await acceptTask.WaitAsync(Budget);

            // Arms the deferred opening negotiation; the peer must drain
            // it so only the close_notify below ends the session.
            var readTask = session.ReadAsync(Budget);
            DrainStream(ssl);

            // close_notify with the socket HELD OPEN: no TCP FIN arrives,
            // so the FIN probe stays silent and only the decrypt-layer
            // end-of-stream (SslStream read 0 -> wire -1 -> stream close)
            // can end the session. ShutdownAsync parks waiting for a
            // reciprocal alert the session never sends, so observe (not
            // abandon) it at teardown.
            var shutdown = ssl.ShutdownAsync();
            string read = await readTask.WaitAsync(Budget);
            read.Should().BeEmpty();
            session.IsConnected.Should().BeFalse();

            tls.Close();
            try
            {
                await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception)
            {
                // Expected: the socket died while the reciprocal alert
                // was still pending.
            }
        }

        private static System.Security.Cryptography.X509Certificates.X509Certificate2 CreateSelfSignedCert()
        {
            using var key = System.Security.Cryptography.ECDsa.Create();
            return new System.Security.Cryptography.X509Certificates.CertificateRequest(
                "CN=localhost", key, System.Security.Cryptography.HashAlgorithmName.SHA256).CreateSelfSigned(
                DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        }

        private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

        [Fact]
        public async Task ReadAsync_CrLfAtPipeClose_DeliversBothBytes()
        {
            // The CR LF continuation stashes the LF past the peer's final
            // close; the next read must drain it instead of reporting
            // end-of-stream (a lone terminator LF was dropped here).
            var options = new TelnetServerOptions
            {
                HandshakeTimeout = Timeout.InfiniteTimeSpan,
            };
            var (peer, wire) = InMemoryPipe.Create();
            using var session = new ServerSession(wire, options, CancellationToken.None);
            byte[] payload = [0x68, 0x65, 0x6C, 0x6C, 0x6F, 0x0D, 0x0A];
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await peer.WriteAsync(payload, 0, payload.Length, cts.Token);
            peer.Close();

            (await DrainAsync(session)).Should().Be("hello\r\n");
            peer.Dispose();
        }

        [Fact]
        public async Task ReadAsync_BareLfAtPipeClose_StaysIntact()
        {
            var options = new TelnetServerOptions
            {
                HandshakeTimeout = Timeout.InfiniteTimeSpan,
            };
            var (peer, wire) = InMemoryPipe.Create();
            using var session = new ServerSession(wire, options, CancellationToken.None);
            byte[] payload = System.Text.Encoding.UTF8.GetBytes("hello\n");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await peer.WriteAsync(payload, 0, payload.Length, cts.Token);
            peer.Close();

            (await DrainAsync(session)).Should().Be("hello\n");
            peer.Dispose();
        }

        [Fact]
        public async Task ReadAsync_MidStreamCrLfAtPipeClose_StaysIntact()
        {
            var options = new TelnetServerOptions
            {
                HandshakeTimeout = Timeout.InfiniteTimeSpan,
            };
            var (peer, wire) = InMemoryPipe.Create();
            using var session = new ServerSession(wire, options, CancellationToken.None);
            byte[] payload = System.Text.Encoding.UTF8.GetBytes("A\r\nB");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await peer.WriteAsync(payload, 0, payload.Length, cts.Token);
            peer.Close();

            (await DrainAsync(session)).Should().Be("A\r\nB");
            peer.Dispose();
        }

        [Fact]
        public async Task ReadAsync_CrNulAtPipeClose_StaysIntact()
        {
            var options = new TelnetServerOptions
            {
                HandshakeTimeout = Timeout.InfiniteTimeSpan,
            };
            var (peer, wire) = InMemoryPipe.Create();
            using var session = new ServerSession(wire, options, CancellationToken.None);
            byte[] payload = [0x41, 0x0D, 0x00];
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await peer.WriteAsync(payload, 0, payload.Length, cts.Token);
            peer.Close();

            (await DrainAsync(session)).Should().Be("A\r\0");
            peer.Dispose();
        }

        [Fact]
        public async Task ReadAsync_BareCrAtPipeClose_DeliversCr()
        {
            var options = new TelnetServerOptions
            {
                HandshakeTimeout = Timeout.InfiniteTimeSpan,
            };
            var (peer, wire) = InMemoryPipe.Create();
            using var session = new ServerSession(wire, options, CancellationToken.None);
            byte[] payload = System.Text.Encoding.UTF8.GetBytes("A\r");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await peer.WriteAsync(payload, 0, payload.Length, cts.Token);
            peer.Close();

            (await DrainAsync(session)).Should().Be("A\r");
            peer.Dispose();
        }

        private static async Task<string> DrainAsync(ServerSession session)
        {
            // Slice boundaries on a closing peer are an implementation
            // detail (the CR continuation may split the LF across reads),
            // so concatenate until the session reports end-of-stream. Every
            // pass ends at once on a closed peer, so this cannot hang: at
            // most one empty live wait, bounded below.
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < 10; i++)
            {
                string slice = await session.ReadAsync(TimeSpan.FromSeconds(5)).WaitAsync(Budget);
                if (slice.Length == 0)
                {
                    break;
                }

                sb.Append(slice);
            }

            return sb.ToString();
        }
    }
}
