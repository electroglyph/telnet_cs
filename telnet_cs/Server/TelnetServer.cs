namespace telnet_cs.Server
{
    using System;
    using System.Net;
    using System.Net.Security;
    using System.Security.Authentication;
    using System.Threading;
    using System.Threading.Tasks;
    using telnet_cs.Transport;

    /// <summary>
    /// Listens for inbound Telnet connections and accepts them as
    /// <see cref="ServerSession"/> instances. All protocol behavior lives in
    /// the session; this class owns only the listen socket lifecycle.
    /// </summary>
    public sealed class TelnetServer : IDisposable
    {
        private readonly System.Net.Sockets.TcpListener listener;
        private readonly TelnetServerOptions options;
        private int boundPort;
        private bool disposed;

        /// <summary>
        /// Initialises a new instance of the <see cref="TelnetServer"/> class
        /// with default <see cref="TelnetServerOptions"/>.
        /// </summary>
        /// <param name="port">The port to listen on. Use 0 for an OS-assigned
        /// (ephemeral) port, then read <see cref="Port"/> after <see cref="Start"/>.</param>
        public TelnetServer(int port)
          : this(port, new TelnetServerOptions())
        {
        }

        /// <summary>
        /// Initialises a new instance of the <see cref="TelnetServer"/> class.
        /// </summary>
        /// <param name="port">The port to listen on. Use 0 for an OS-assigned
        /// (ephemeral) port, then read <see cref="Port"/> after <see cref="Start"/>.</param>
        /// <param name="options">The server settings. The reference is kept: later
        /// mutations apply to subsequently accepted sessions.</param>
        public TelnetServer(int port, TelnetServerOptions options)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(port);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(options.ListenAddress);
            this.options = options;
            listener = new System.Net.Sockets.TcpListener(options.ListenAddress, port);
        }

        /// <summary>
        /// Gets the server settings.
        /// </summary>
        public TelnetServerOptions Settings => options;

        /// <summary>
        /// Gets the bound port. Valid once <see cref="Start"/> has run (this is
        /// how callers discover the OS-assigned port when constructed with 0);
        /// 0 before that.
        /// </summary>
        public int Port => boundPort;

        /// <summary>
        /// Starts listening with <see cref="TelnetServerOptions.Backlog"/>.
        /// </summary>
        public void Start()
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.Backlog);
            listener.Start(options.Backlog);
            boundPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        }

        /// <summary>
        /// Stops listening. Accepted sessions are unaffected.
        /// </summary>
        public void Stop()
        {
            listener.Stop();
        }

        /// <summary>
        /// Accepts one inbound connection and wraps it in a
        /// <see cref="ServerSession"/> that owns its stream: disposing the
        /// session releases the accepted socket. The session emits the server
        /// opening preset (see <see cref="ServerSession.SendOpeningPresetAsync"/>)
        /// before this returns; a fully toggled-off preset sends nothing.
        /// Throws <see cref="InvalidOperationException"/> if the listener was
        /// never started.
        /// </summary>
        /// <param name="cancellationToken">A token to cancel the accept.</param>
        /// <returns>The accepted session.</returns>
        public async Task<ServerSession> AcceptSessionAsync(CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var accepted = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
#pragma warning disable CA2000 // Ownership of the socket transfers to the session on success; the catch releases it otherwise (including the half-built TLS wrapper: its factory releases the SslStream on handshake failure, and the raw accept is always disposed below).
            ServerSession? session = null;
            try
            {
                TcpClient tcpSocket = new TcpClient(accepted);
                ISocket socket = tcpSocket;
                if (options.ServerCertificate is not null)
                {
                    socket = await TlsSocket.AuthenticateAsServerAsync(
                        tcpSocket,
                        new SslServerAuthenticationOptions
                        {
                            ServerCertificate = options.ServerCertificate,
                            ClientCertificateRequired = false,
                            EnabledSslProtocols = options.TlsProtocols,
                        },
                        cancellationToken).ConfigureAwait(false);
                }

                session = new ServerSession(new TcpByteStream(socket, takeOwnership: true), options, CancellationToken.None);
                // The session emits the server opening preset before the accept
                // completes; a fully toggled-off preset sends nothing.
                await session.SendOpeningPresetAsync(cancellationToken).ConfigureAwait(false);
                return session;
            }
            catch
            {
                session?.Dispose();
                accepted.Dispose();
                throw;
            }
#pragma warning restore CA2000
        }

        /// <summary>
        /// Stops listening.
        /// </summary>
        public void Dispose()
        {
            if (!disposed)
            {
                disposed = true;
                listener.Stop();
            }
        }
    }
}
