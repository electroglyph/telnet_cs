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
        private readonly System.Threading.Channels.Channel<ServerSession> newClients =
            System.Threading.Channels.Channel.CreateBounded<ServerSession>(
                new System.Threading.Channels.BoundedChannelOptions(1000)
                {
                    FullMode = System.Threading.Channels.BoundedChannelFullMode.DropWrite,
                });
        private System.Threading.Timer? statusTimer;
        private int boundPort;
        private bool disposed;
        private int reservations;
        private readonly Dictionary<string, int> reservationsPerIp = new(StringComparer.Ordinal);
        private long rejectedCapacity;
        private long rejectedPerIp;
        private long rejectedFilter;
        private long queueDropped;

        /// <summary>
        /// One tracked session for the status logger: a weak reference (the
        /// server must not keep caller-owned sessions alive) plus the accept
        /// snapshot and the last-logged counters.
        /// </summary>
        private sealed class SessionRecord
        {
            public SessionRecord(ServerSession session, string endpoint, bool isTls, string ipKey)
            {
                Session = new WeakReference<ServerSession>(session);
                Endpoint = endpoint;
                IsTls = isTls;
                IpKey = ipKey;
            }

            public WeakReference<ServerSession> Session { get; }

            public string Endpoint { get; }

            public bool IsTls { get; }

            public string IpKey { get; }

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
        /// <param name="options">The server settings. The reference is kept.
        /// Snapshotted options (for example <c>HandshakeTimeout</c>) apply to
        /// subsequently accepted sessions only; explicitly live options are
        /// read per-read. See <c>docs/server.md</c> for the per-option table.</param>
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
        /// Gets the bound port, or 0 when not listening. Valid once <see cref="Start"/>
        /// has run (this is how callers discover the OS-assigned port when
        /// constructed with 0); 0 before <see cref="Start"/> and again after
        /// <see cref="Stop"/> or <see cref="Dispose"/>.
        /// </summary>
        public int Port => boundPort;

        /// <summary>
        /// Gets the count of refused accepts due to
        /// <see cref="TelnetServerOptions.MaxConcurrentSessions"/>.
        /// </summary>
        public long RejectedCapacityCount => Interlocked.Read(ref rejectedCapacity);

        /// <summary>
        /// Gets the count of refused accepts due to
        /// <see cref="TelnetServerOptions.MaxConnectionsPerIp"/>.
        /// </summary>
        public long RejectedPerIpCount => Interlocked.Read(ref rejectedPerIp);

        /// <summary>
        /// Gets the count of refused accepts due to
        /// <see cref="TelnetServerOptions.AcceptFilter"/>.
        /// </summary>
        public long RejectedFilterCount => Interlocked.Read(ref rejectedFilter);

        /// <summary>
        /// Gets the count of dropped <c>WaitForClientAsync</c> notifications
        /// (bounded queue full under <c>DropWrite</c>).
        /// </summary>
        public long QueueDroppedCount => Interlocked.Read(ref queueDropped);

        /// <summary>
        /// Starts listening with <see cref="TelnetServerOptions.Backlog"/>.
        /// </summary>
        public void Start()
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.Backlog);
            ArgumentOutOfRangeException.ThrowIfNegative(options.MaxConcurrentSessions);
            ArgumentOutOfRangeException.ThrowIfNegative(options.MaxConnectionsPerIp);
            ArgumentOutOfRangeException.ThrowIfNegative(options.MaxBufferedTextChars);
            ArgumentOutOfRangeException.ThrowIfNegative(options.MaxReplLineLength);
            ArgumentOutOfRangeException.ThrowIfNegative(options.MaxEnvironVars);
            ArgumentOutOfRangeException.ThrowIfNegative(options.MaxEnvironValueChars);
            ArgumentOutOfRangeException.ThrowIfNegative(options.MaxEnvironKeyChars);
            ArgumentOutOfRangeException.ThrowIfNegative(options.MaxTtypeChars);
            ArgumentOutOfRangeException.ThrowIfNegative(options.MaxMudListItems);
            ArgumentOutOfRangeException.ThrowIfNegative(options.MaxMudListBytes);
            ArgumentOutOfRangeException.ThrowIfNegative(options.MaxMudKeys);
            ArgumentOutOfRangeException.ThrowIfNegative(options.MaxDecompressedBytes);
            ArgumentOutOfRangeException.ThrowIfNegative(options.MaxDecompressionRatio);
            ArgumentOutOfRangeException.ThrowIfNegative(options.MaxCompressedBytes);
            ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxLoginAttempts, 1);
            listener.Start(options.Backlog);
            boundPort = ((IPEndPoint)listener.LocalEndpoint).Port;
            if (options.StatusInterval is { } interval && interval > TimeSpan.Zero && statusTimer is null)
            {
                statusTimer = new System.Threading.Timer(static state => ((TelnetServer)state!).ReportStatus(), this, interval, interval);
            }
        }

        /// <summary>
        /// Stops listening and closes accepted sessions (the reference
        /// <c>Server.close</c> closes the listener plus every protocol
        /// transport). Clears the bound port back to 0 and stops the status
        /// timer; <see cref="Start"/> re-arms both. Sessions stay
        /// caller-owned: dispose them as usual.
        /// </summary>
        public void Stop()
        {
            var timer = System.Threading.Interlocked.Exchange(ref statusTimer, null);
            if (timer is not null)
            {
                timer.Change(System.Threading.Timeout.InfiniteTimeSpan, System.Threading.Timeout.InfiniteTimeSpan);
                timer.Dispose();
            }

            boundPort = 0;
            listener.Stop();
            CloseAcceptedSessions();
        }

        private void CloseAcceptedSessions()
        {
            List<ServerSession> live;
            lock (statusLock)
            {
                live = new List<ServerSession>(sessions.Count);
                for (int i = sessions.Count - 1; i >= 0; i--)
                {
                    if (sessions[i].Session.TryGetTarget(out var session))
                    {
                        live.Add(session);
                    }
                }

                sessions.Clear();
            }

#pragma warning disable CA1031 // Do not catch general exception types
            foreach (var session in live)
            {
                try
                {
                    session.CloseForServerStop();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(ex.Message);
                }
            }
#pragma warning restore CA1031 // Do not catch general exception types
        }

        /// <summary>
        /// Waits for the next accepted client (the reference
        /// <c>wait_for_client</c>): every <see cref="AcceptSessionAsync"/>
        /// enqueues its session on a bounded (1000, newest-dropped) queue
        /// that this drains in accept order (oldest-first FIFO).
        /// Ownership: <see cref="AcceptSessionAsync"/> returns ownership and
        /// additionally offers a notification to this queue. An accept-loop
        /// that discards the return value and transfers ownership via this
        /// method loses the session when its notification is dropped, so the
        /// two patterns must not be mixed on one server. The accept-returned
        /// session stays connected even when its notification is dropped.
        /// </summary>
        /// <param name="cancellationToken">A token to cancel the wait.</param>
        /// <returns>The next accepted session.</returns>
        public Task<ServerSession> WaitForClientAsync(CancellationToken cancellationToken = default)
        {
            return newClients.Reader.ReadAsync(cancellationToken).AsTask();
        }

        /// <summary>
        /// Accepts one inbound connection and wraps it in a
        /// <see cref="ServerSession"/> that owns its stream: disposing the
        /// session releases the accepted socket. The session emits the server
        /// opening preset (see <see cref="ServerSession.SendOpeningPresetAsync"/>)
        /// before this returns; a fully toggled-off preset sends nothing.
        /// Admission control (<c>MaxConcurrentSessions</c>,
        /// <c>MaxConnectionsPerIp</c>, <c>AcceptFilter</c>,
        /// <c>AcceptFilterV2</c>) runs before any
        /// TLS handshake or preset bytes: refused accepts dispose the socket
        /// with no bytes sent and throw <see cref="SessionCapacityException"/>,
        /// <see cref="PerIpCapacityException"/>, or
        /// <see cref="ConnectionRefusedByFilterException"/> (all deriving
        /// from <see cref="InvalidOperationException"/>).
        /// Throws <see cref="TimeoutException"/> when the TLS handshake or
        /// the opening preset exceeds <c>HandshakeTimeout</c> (logged as
        /// <c>handshake-timeout:</c>; the socket is disposed).
        /// Throws <see cref="InvalidOperationException"/> if the listener was
        /// never started.
        /// </summary>
        /// <param name="cancellationToken">A token to cancel the accept.</param>
        /// <returns>The accepted session.</returns>
        public async Task<ServerSession> AcceptSessionAsync(CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var accepted = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            DateTime acceptEntryUtc = DateTime.UtcNow;
            TimeSpan handshakeTimeout = options.HandshakeTimeout;
            bool handshakeEnabled = handshakeTimeout != System.Threading.Timeout.InfiniteTimeSpan && handshakeTimeout > TimeSpan.Zero;
            DateTime handshakeDeadlineUtc = handshakeEnabled ? acceptEntryUtc.Add(handshakeTimeout) : DateTime.MaxValue;
            System.Net.EndPoint? remoteEndPoint = null;
            string endpoint = "unknown";
            string ipKey = "unknown";
            try
            {
                remoteEndPoint = accepted.Client.RemoteEndPoint;
                endpoint = remoteEndPoint?.ToString() ?? "unknown";
                ipKey = NormalizeIpKey(remoteEndPoint);
            }
            catch (Exception ex) when (ex is System.Net.Sockets.SocketException or ObjectDisposedException)
            {
                endpoint = "unknown";
                ipKey = "unknown";
            }

            var filter = options.AcceptFilter;
            if (filter is not null)
            {
                bool allowed;
                try
                {
                    allowed = filter(remoteEndPoint);
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref rejectedFilter);
                    LogOutsideLock($"over-capacity: filter-reject endpoint={endpoint} reason=filter-threw");
                    System.Diagnostics.Debug.WriteLine(ex.Message);
                    accepted.Dispose();
                    throw new ConnectionRefusedByFilterException(remoteEndPoint, ex);
                }

                if (!allowed)
                {
                    Interlocked.Increment(ref rejectedFilter);
                    LogOutsideLock($"over-capacity: filter-reject endpoint={endpoint}");
                    accepted.Dispose();
                    throw new ConnectionRefusedByFilterException(remoteEndPoint);
                }
            }
            else if (options.AcceptFilterV2 is { } filterV2)
            {
                AcceptDecision decision;
                try
                {
                    decision = filterV2(remoteEndPoint);
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref rejectedFilter);
                    LogOutsideLock($"over-capacity: filter-reject endpoint={endpoint} reason=filter-threw");
                    System.Diagnostics.Debug.WriteLine(ex.Message);
                    accepted.Dispose();
                    throw new ConnectionRefusedByFilterException(remoteEndPoint, ex);
                }

                if (!decision.Allowed)
                {
                    string reason = string.IsNullOrEmpty(decision.Reason) ? "filter-reject" : decision.Reason;
                    Interlocked.Increment(ref rejectedFilter);
                    LogOutsideLock($"over-capacity: filter-reject endpoint={endpoint} reason={reason}");
                    accepted.Dispose();
                    throw new ConnectionRefusedByFilterException(remoteEndPoint, reason, null);
                }
            }

            string? reservationIpKey = null;
            string? rejectReason = null;
            bool reserved = false;
            lock (statusLock)
            {
                PruneLocked();
                int maxSessions = options.MaxConcurrentSessions;
                int maxPerIp = options.MaxConnectionsPerIp;
                int liveTotal = sessions.Count + reservations;
                if (maxSessions > 0 && liveTotal >= maxSessions)
                {
                    rejectReason = "capacity";
                }
                else if (maxPerIp > 0)
                {
                    int livePerIp = 0;
                    foreach (var record in sessions)
                    {
                        if (string.Equals(record.IpKey, ipKey, StringComparison.Ordinal))
                        {
                            livePerIp++;
                        }
                    }

                    reservationsPerIp.TryGetValue(ipKey, out int reservedPerIp);
                    if (livePerIp + reservedPerIp >= maxPerIp)
                    {
                        rejectReason = "per-ip";
                    }
                }

                if (rejectReason is null)
                {
                    reservations++;
                    reservationsPerIp[ipKey] = reservationsPerIp.TryGetValue(ipKey, out int current) ? current + 1 : 1;
                    reservationIpKey = ipKey;
                    reserved = true;
                }
            }

            if (!reserved)
            {
                if (rejectReason == "per-ip")
                {
                    Interlocked.Increment(ref rejectedPerIp);
                }
                else
                {
                    Interlocked.Increment(ref rejectedCapacity);
                }

                LogOutsideLock($"over-capacity: {rejectReason} endpoint={endpoint}");
                accepted.Dispose();
                throw rejectReason == "per-ip"
                    ? new PerIpCapacityException(remoteEndPoint)
                    : new SessionCapacityException(remoteEndPoint);
            }

#pragma warning disable CA2000 // Ownership of the socket transfers to the session on success; the catch releases it otherwise (including the half-built TLS wrapper: its factory releases the SslStream on handshake failure, and the raw accept is always disposed below).
            ServerSession? session = null;
            try
            {
                TcpClient tcpSocket = new TcpClient(accepted);
                ISocket socket = tcpSocket;
                bool isTls = false;
                if (options.ServerCertificate is not null)
                {
                    TimeSpan remaining = handshakeDeadlineUtc - DateTime.UtcNow;
                    using var deadlineCts = handshakeEnabled ? new CancellationTokenSource(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero) : null;
                    using var handshakeLinked = deadlineCts is null
                        ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                        : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadlineCts.Token);
                    bool handshake = true;
                    if (options.TlsAutoDetect != System.Threading.Timeout.InfiniteTimeSpan &&
                        options.TlsAutoDetect > TimeSpan.Zero)
                    {
                        TimeSpan peekWait = options.TlsAutoDetect;
                        if (handshakeEnabled)
                        {
                            TimeSpan left = handshakeDeadlineUtc - DateTime.UtcNow;
                            if (left <= TimeSpan.Zero)
                            {
                                ReleaseReservation(reservationIpKey!);
                                LogOutsideLock($"handshake-timeout: endpoint={endpoint}");
                                accepted.Dispose();
                                throw new TimeoutException($"handshake-timeout: endpoint {endpoint}.");
                            }

                            if (left < peekWait)
                            {
                                peekWait = left;
                            }
                        }

                        try
                        {
                            handshake = await PeekTlsClientHelloAsync(accepted, peekWait, handshakeLinked.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && handshakeEnabled && DateTime.UtcNow >= handshakeDeadlineUtc)
                        {
                            ReleaseReservation(reservationIpKey!);
                            LogOutsideLock($"handshake-timeout: endpoint={endpoint}");
                            accepted.Dispose();
                            throw new TimeoutException($"handshake-timeout: endpoint {endpoint}.");
                        }
                    }

                    if (handshake)
                    {
                        try
                        {
                            socket = await TlsSocket.AuthenticateAsServerAsync(
                                tcpSocket,
                                new SslServerAuthenticationOptions
                                {
                                    ServerCertificate = options.ServerCertificate,
                                    ClientCertificateRequired = false,
                                    EnabledSslProtocols = options.TlsProtocols,
                                },
                                handshakeLinked.Token).ConfigureAwait(false);
                            isTls = true;
                        }
                        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && handshakeEnabled && DateTime.UtcNow >= handshakeDeadlineUtc)
                        {
                            ReleaseReservation(reservationIpKey!);
                            LogOutsideLock($"handshake-timeout: endpoint={endpoint}");
                            accepted.Dispose();
                            throw new TimeoutException($"handshake-timeout: endpoint {endpoint}.");
                        }
                    }
                }

                session = new ServerSession(new TcpByteStream(socket, takeOwnership: true), options, CancellationToken.None);
                session.IsTls = isTls;
                session.RemoteEndPoint = endpoint;
                session.ResetHandshakeDeadline(handshakeDeadlineUtc, endpoint);
                // The session emits the server opening preset before the accept
                // completes; a fully toggled-off preset sends nothing.
                try
                {
                    if (handshakeEnabled)
                    {
                        TimeSpan left = handshakeDeadlineUtc - DateTime.UtcNow;
                        if (left <= TimeSpan.Zero)
                        {
                            LogOutsideLock($"handshake-timeout: endpoint={endpoint}");
                            throw new TimeoutException($"handshake-timeout: endpoint {endpoint}.");
                        }

                        using var presetCts = new CancellationTokenSource(left);
                        using var presetLinked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, presetCts.Token);
                        await session.SendOpeningPresetAsync(presetLinked.Token).ConfigureAwait(false);
                    }
                    else
                    {
                        await session.SendOpeningPresetAsync(cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && handshakeEnabled && DateTime.UtcNow >= handshakeDeadlineUtc)
                {
                    LogOutsideLock($"handshake-timeout: endpoint={endpoint}");
                    throw new TimeoutException($"handshake-timeout: endpoint {endpoint}.");
                }

                ConvertReservation(reservationIpKey!, session, endpoint, isTls);
                if (!newClients.Writer.TryWrite(session))
                {
                    Interlocked.Increment(ref queueDropped);
                    LogOutsideLock($"over-capacity: queue-drop endpoint={endpoint}");
                }

                return session;
            }
            catch
            {
                if (reservationIpKey is not null && session is null)
                {
                    ReleaseReservation(reservationIpKey);
                }
                else if (reservationIpKey is not null && session is not null)
                {
                    // Preset failed after session construction: the session
                    // was never tracked, so release the reservation and
                    // dispose the half-built session (which owns the socket).
                    ReleaseReservation(reservationIpKey);
                }

                session?.Dispose();
                if (session is null)
                {
                    try { accepted.Dispose(); } catch { }
                }

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

        private static string NormalizeIpKey(System.Net.EndPoint? endPoint)
        {
            if (endPoint is IPEndPoint ip)
            {
                try
                {
                    return ip.Address.MapToIPv4().ToString();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(ex.Message);
                    return "unknown";
                }
            }

            return "unknown";
        }

        private void PruneLocked()
        {
            for (int i = sessions.Count - 1; i >= 0; i--)
            {
                if (!sessions[i].Session.TryGetTarget(out var session) || !session.IsConnected)
                {
                    sessions.RemoveAt(i);
                }
            }
        }

        private void ReleaseReservation(string ipKey)
        {
            lock (statusLock)
            {
                if (reservations > 0)
                {
                    reservations--;
                }

                if (reservationsPerIp.TryGetValue(ipKey, out int current))
                {
                    if (current <= 1)
                    {
                        reservationsPerIp.Remove(ipKey);
                    }
                    else
                    {
                        reservationsPerIp[ipKey] = current - 1;
                    }
                }
            }
        }

        private void ConvertReservation(string ipKey, ServerSession session, string endpoint, bool isTls)
        {
            lock (statusLock)
            {
                if (reservations > 0)
                {
                    reservations--;
                }

                if (reservationsPerIp.TryGetValue(ipKey, out int current))
                {
                    if (current <= 1)
                    {
                        reservationsPerIp.Remove(ipKey);
                    }
                    else
                    {
                        reservationsPerIp[ipKey] = current - 1;
                    }
                }

                PruneLocked();
                sessions.Add(new SessionRecord(session, endpoint, isTls, ipKey));
            }
        }

        private void LogOutsideLock(string message)
        {
            try
            {
                options.Log?.Invoke(message);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex.Message);
            }

            System.Diagnostics.Debug.WriteLine(message);
        }

        private void ReportStatus()
        {
#pragma warning disable CA1031 // Do not catch general exception types
            try
            {
                List<(string Endpoint, long Received, long Sent, double IdleSeconds, bool IsTls)> snapshot;
                string aggregate;
                lock (statusLock)
                {
                    PruneLocked();
                    snapshot = new List<(string, long, long, double, bool)>(sessions.Count);
                    foreach (var record in sessions)
                    {
                        if (!record.Session.TryGetTarget(out var session))
                        {
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
                        snapshot.Add((record.Endpoint, received, sent, session.Context.Idle.TotalSeconds, record.IsTls));
                    }

                    long cap = Interlocked.Read(ref rejectedCapacity);
                    long perIp = Interlocked.Read(ref rejectedPerIp);
                    long filter = Interlocked.Read(ref rejectedFilter);
                    long queuedrop = Interlocked.Read(ref queueDropped);
                    aggregate = $"sessions={sessions.Count} rejected(capacity={cap},per-ip={perIp},filter={filter},queue-drop={queuedrop})";
                }

                var log = options.Log;
                if (log is null)
                {
                    return;
                }

                foreach (var (endpoint, received, sent, idleSeconds, isTls) in snapshot)
                {
                    log($"{endpoint} (rx={received},tx={sent},idle={idleSeconds:F0}s,tls={isTls})");
                }

                log(aggregate);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex.Message);
            }
#pragma warning restore CA1031 // Do not catch general exception types
        }

        /// <summary>
        /// Stops listening and closes accepted sessions.
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

                boundPort = 0;
                listener.Stop();
                CloseAcceptedSessions();
                newClients.Writer.TryComplete();
            }
        }
    }
}
