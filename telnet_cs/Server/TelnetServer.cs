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
        private readonly object statusLock = new();
        private readonly List<SessionRecord> sessions = new();
        private System.Threading.Timer? statusTimer;
        private int boundPort;
        private bool disposed;

        /// <summary>
        /// One tracked session for the status logger: a weak reference (the
        /// server must not keep caller-owned sessions alive) plus the accept
        /// snapshot and the last-logged counters.
        /// </summary>
        private sealed class SessionRecord
        {
            public SessionRecord(ServerSession session, string endpoint, bool isTls)
            {
                Session = new WeakReference<ServerSession>(session);
                Endpoint = endpoint;
                IsTls = isTls;
            }

            public WeakReference<ServerSession> Session { get; }

            public string Endpoint { get; }

            public bool IsTls { get; }

            public long LastReceived { get; set; }

            public long LastSent { get; set; }
        }

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
            if (options.StatusInterval is { } interval && interval > TimeSpan.Zero && statusTimer is null)
            {
                statusTimer = new System.Threading.Timer(static state => ((TelnetServer)state!).ReportStatus(), this, interval, interval);
            }
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
                bool isTls = false;
                if (options.ServerCertificate is not null)
                {
                    bool handshake = true;
                    if (options.TlsAutoDetect != System.Threading.Timeout.InfiniteTimeSpan &&
                        options.TlsAutoDetect > TimeSpan.Zero)
                    {
                        // Opt-in TLS sniffing (the reference --tls-auto): peek
                        // at the first inbound byte — 0x16 ClientHello means
                        // TLS, anything else (or a quiet peer) stays plaintext.
                        handshake = await PeekTlsClientHelloAsync(accepted, options.TlsAutoDetect, cancellationToken).ConfigureAwait(false);
                    }

                    if (handshake)
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
                        isTls = true;
                    }
                }

                session = new ServerSession(new TcpByteStream(socket, takeOwnership: true), options, CancellationToken.None);
                session.IsTls = isTls;
                session.RemoteEndPoint = accepted.Client.RemoteEndPoint?.ToString();
                // The session emits the server opening preset before the accept
                // completes; a fully toggled-off preset sends nothing.
                await session.SendOpeningPresetAsync(cancellationToken).ConfigureAwait(false);
                TrackSession(session, accepted.Client.RemoteEndPoint?.ToString() ?? "unknown", isTls);
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
        /// Peeks at the first inbound byte without consuming it: <c>true</c>
        /// only for a TLS ClientHello lead byte (<c>0x16</c>). Timeouts,
        /// cancellations, closed peers, and socket errors all mean plaintext.
        /// </summary>
        private static async Task<bool> PeekTlsClientHelloAsync(System.Net.Sockets.TcpClient accepted, TimeSpan wait, CancellationToken cancellationToken)
        {
            using var timeout = new CancellationTokenSource(wait);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            try
            {
                var probe = new byte[1];
                int received = await accepted.Client.ReceiveAsync(probe, System.Net.Sockets.SocketFlags.Peek, linked.Token).ConfigureAwait(false);
                return received > 0 && probe[0] == 0x16;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (System.Net.Sockets.SocketException)
            {
                return false;
            }
        }

        private void TrackSession(ServerSession session, string endpoint, bool isTls)
        {
            lock (statusLock)
            {
                for (int i = sessions.Count - 1; i >= 0; i--)
                {
                    if (!sessions[i].Session.TryGetTarget(out _))
                    {
                        sessions.RemoveAt(i);
                    }
                }

                sessions.Add(new SessionRecord(session, endpoint, isTls));
            }
        }

        private void ReportStatus()
        {
#pragma warning disable CA1031 // Do not catch general exception types
            try
            {
                var log = options.Log;
                lock (statusLock)
                {
                    for (int i = sessions.Count - 1; i >= 0; i--)
                    {
                        var record = sessions[i];
                        if (!record.Session.TryGetTarget(out var session) || !session.IsConnected)
                        {
                            sessions.RemoveAt(i);
                            continue;
                        }

                        long received = session.Context.CharsReceived;
                        long sent = session.Context.CharsSent;
                        if (received == record.LastReceived && sent == record.LastSent)
                        {
                            continue;
                        }

                        record.LastReceived = received;
                        record.LastSent = sent;
                        // Reference shape ip:port(rx,tx,idle,tls), in words.
                        log?.Invoke($"{record.Endpoint} (rx={received},tx={sent},idle={session.Context.Idle.TotalSeconds:F0}s,tls={record.IsTls})");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex.Message);
            }
#pragma warning restore CA1031 // Do not catch general exception types
        }

        /// <summary>
        /// Stops listening.
        /// </summary>
        public void Dispose()
        {
            if (!disposed)
            {
                disposed = true;
                var timer = System.Threading.Interlocked.Exchange(ref statusTimer, null);
                if (timer is not null)
                {
                    timer.Change(System.Threading.Timeout.InfiniteTimeSpan, System.Threading.Timeout.InfiniteTimeSpan);
                    timer.Dispose();
                }

                listener.Stop();
            }
        }
    }
}
