namespace telnet_cs.Server;

using System;
using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using telnet_cs.Transport;

/// <summary>
/// Inbound accept pipeline: admission control, TLS handshake and opening negotiation for one accepted socket. Split from <see cref="TelnetServer"/>; wire behavior is unchanged.
/// </summary>
public partial class TelnetServer
{
    /// <summary>
    /// Accepts one inbound TCP connection and wraps it in an unnegotiated
    /// <see cref="ServerSession"/> that owns its stream. Admission control
    /// (<c>MaxConcurrentSessions</c>, <c>MaxConnectionsPerIp</c>,
    /// <c>AcceptFilter</c>) runs before any bytes
    /// are sent — refused accepts dispose the socket with no bytes sent
    /// and throw <see cref="SessionCapacityException"/>,
    /// <see cref="PerIpCapacityException"/>, or
    /// <see cref="ConnectionRefusedByFilterException"/> (all deriving
    /// from <see cref="InvalidOperationException"/>) — then the TLS
    /// handshake runs where a certificate is configured (<see
    /// cref="TelnetServerOptions.GetServerCertificate"/> consulted first,
    /// falling back to <c>ServerCertificate</c>, resolved once per
    /// handshake). No
    /// negotiation or preset bytes are sent; call
    /// <see cref="NegotiateAsync"/> to send the opening preset, or
    /// dispose the session to abandon it (disposal releases the admission
    /// reservation, so capacity is not leaked).
    /// Throws <see cref="TimeoutException"/> when the TLS handshake
    /// exceeds <c>HandshakeTimeout</c> (logged as
    /// <c>handshake-timeout:</c>; the socket is disposed).
    /// Throws <see cref="InvalidOperationException"/> if the listener was
    /// never started.
    /// </summary>
    /// <example>
    /// <code>
    /// // Sessions carry no public endpoint: snapshot it in the filter
    /// // (which runs per accept, before any bytes) and correlate after.
    /// using var server = new TelnetServer(0, new TelnetServerOptions
    /// {
    ///     AcceptFilter = endPoint => { lastEndpoint = endPoint; return new AcceptDecision(true); },
    /// });
    /// var pending = await server.AcceptTcpAsync(ct);
    /// var session = await server.NegotiateAsync(pending, ct);
    /// </code>
    /// </example>
    /// <param name="cancellationToken">A token to cancel the accept.</param>
    /// <returns>The accepted but unnegotiated session.</returns>
#pragma warning disable CA2000 // Ownership of the socket transfers to the session on success; the catch releases it otherwise (including the half-built TLS wrapper: its factory releases the SslStream on handshake failure, and the raw accept is always disposed below).
    public async Task<ServerSession> AcceptTcpAsync(CancellationToken cancellationToken = default)
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

        if (options.AcceptFilter is { } filter)
        {
            AcceptDecision decision;
            try
            {
                decision = filter(remoteEndPoint);
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
            // Either certificate source arms the TLS path; the handshake
            // below resolves exactly once per handshake (see there), so a
            // rotation racing the accept cannot skew the gate.
            if (options.ServerCertificate is not null || options.GetServerCertificate is not null)
            {
                TimeSpan remaining = handshakeDeadlineUtc - DateTime.UtcNow;
                using var deadlineCts = handshakeEnabled ? new CancellationTokenSource(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero) : null;
                using var handshakeLinked = deadlineCts is null
                    ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                    : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadlineCts.Token);
                // Latch the deadline token itself: the struct stays
                // readable after disposal and reports a fired deadline
                // even when observed before the wall-clock deadline.
                CancellationToken deadlineToken = deadlineCts?.Token ?? CancellationToken.None;
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
                            reservationIpKey = null;
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
                    catch (OperationCanceledException) when (IsHandshakeTimeout(cancellationToken, deadlineToken))
                    {
                        ReleaseReservation(reservationIpKey!);
                        reservationIpKey = null;
                        LogOutsideLock($"handshake-timeout: endpoint={endpoint}");
                        accepted.Dispose();
                        throw new TimeoutException($"handshake-timeout: endpoint {endpoint}.");
                    }
                }

                if (handshake)
                {
                    // Resolved once per handshake into a local: the
                    // callback wins, null falls back to ServerCertificate
                    // read fresh here, and neither yielding a certificate
                    // keeps the missing-cert plaintext behavior below.
                    X509Certificate2? certificate;
                    try
                    {
                        certificate = options.GetServerCertificate?.Invoke() ?? options.ServerCertificate;
                    }
                    catch
                    {
                        // A throwing callback is a handshake failure:
                        // same release + dispose as any failed handshake.
                        ReleaseReservation(reservationIpKey!);
                        reservationIpKey = null;
                        accepted.Dispose();
                        throw;
                    }

                    if (certificate is not null)
                    {
                        try
                        {
                            socket = await TlsSocket.AuthenticateAsServerAsync(
                                tcpSocket,
                                new SslServerAuthenticationOptions
                                {
                                    ServerCertificate = certificate,
                                    ClientCertificateRequired = false,
                                    EnabledSslProtocols = options.TlsProtocols,
                                },
                                handshakeLinked.Token).ConfigureAwait(false);
                            isTls = true;
                        }
                        catch (OperationCanceledException) when (IsHandshakeTimeout(cancellationToken, deadlineToken))
                        {
                            ReleaseReservation(reservationIpKey!);
                            reservationIpKey = null;
                            LogOutsideLock($"handshake-timeout: endpoint={endpoint}");
                            accepted.Dispose();
                            throw new TimeoutException($"handshake-timeout: endpoint {endpoint}.");
                        }
                    }
                }
            }

            session = new ServerSession(new TcpByteStream(socket, takeOwnership: true), options, CancellationToken.None, isTls);
            session.RemoteEndPoint = endpoint;
            session.ResetHandshakeDeadline(handshakeDeadlineUtc, endpoint);
            // The preset goes out in NegotiateAsync, not here: the
            // caller owns the session from this point and may inspect it
            // (or abandon it via Dispose, releasing the reservation).
            string key = reservationIpKey!;
            session.SetPendingNegotiation(
                this,
                new PendingNegotiation(key, endpoint, isTls, handshakeDeadlineUtc, handshakeEnabled),
                () => ReleaseReservation(key));
            return session;
        }
        catch
        {
            if (reservationIpKey is not null)
            {
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
    /// Sends the opening preset on a session from
    /// <see cref="AcceptTcpAsync"/> and hands it to the accept queue.
    /// This is the <see cref="ServerSession.SendOpeningPresetAsync"/>
    /// path, not a second preset variant: the same deadline budget,
    /// <c>TimeoutException</c> mapping, reservation conversion, and
    /// queue offer <see cref="AcceptSessionAsync"/> always ran.
    /// Throws <see cref="InvalidOperationException"/> when
    /// <paramref name="session"/> is not a pending accept from this
    /// server (foreign session, or already negotiated).
    /// </summary>
    /// <example>
    /// <code>
    /// var pending = await server.AcceptTcpAsync(ct);
    /// var session = await server.NegotiateAsync(pending, ct);
    /// </code>
    /// </example>
    /// <param name="session">The unnegotiated session from <see cref="AcceptTcpAsync"/>.</param>
    /// <param name="cancellationToken">A token to cancel the preset send.</param>
    /// <returns>The negotiated session (same instance).</returns>
    public async Task<ServerSession> NegotiateAsync(ServerSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!session.TryClaimPendingNegotiation(this, out PendingNegotiation pending))
        {
            throw new InvalidOperationException("Session is not a pending accept from this server: obtain it from AcceptTcpAsync and negotiate each session once.");
        }

        CancellationToken presetDeadline = CancellationToken.None;
        try
        {
            // The session emits the server opening preset before the
            // accept completes; a fully toggled-off preset sends nothing.
            // Latched like AcceptTcpAsync: the struct outlives its source.
            if (pending.HandshakeEnabled)
            {
                TimeSpan left = pending.HandshakeDeadlineUtc - DateTime.UtcNow;
                if (left <= TimeSpan.Zero)
                {
                    LogOutsideLock($"handshake-timeout: endpoint={pending.Endpoint}");
                    throw new TimeoutException($"handshake-timeout: endpoint {pending.Endpoint}.");
                }

                using var presetCts = new CancellationTokenSource(left);
                presetDeadline = presetCts.Token;
                using var presetLinked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, presetCts.Token);
                await session.SendOpeningPresetAsync(presetLinked.Token).ConfigureAwait(false);
            }
            else
            {
                await session.SendOpeningPresetAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (pending.HandshakeEnabled && IsHandshakeTimeout(cancellationToken, presetDeadline))
        {
            LogOutsideLock($"handshake-timeout: endpoint={pending.Endpoint}");
            ReleaseReservation(pending.ReservationIpKey);
            session.Dispose();
            throw new TimeoutException($"handshake-timeout: endpoint {pending.Endpoint}.");
        }
        catch
        {
            // The claim consumed the dispose hook, so this release is
            // exactly once; disposal cannot re-release.
            ReleaseReservation(pending.ReservationIpKey);
            session.Dispose();
            throw;
        }

        ConvertReservation(pending.ReservationIpKey, session, pending.Endpoint, pending.IsTls);
        if (!newClients.Writer.TryWrite(session))
        {
            Interlocked.Increment(ref queueDropped);
            LogOutsideLock($"over-capacity: queue-drop endpoint={pending.Endpoint}");
        }

        return session;
    }

    /// <summary>
    /// Classifies an <see cref="OperationCanceledException"/> escaping a
    /// deadline-bound accept await: a deadline expiry maps to
    /// <see cref="TimeoutException"/>, a caller cancel stays cancelled.
    /// The deadline is read from its own <see cref="CancellationToken"/>
    /// — never the wall clock — so a deadline-caused cancellation maps
    /// deterministically even when observed before <see
    /// cref="DateTime.UtcNow"/> reaches the deadline (timer/scheduling
    /// granularity fires the source slightly early). The token struct
    /// stays readable after its source is disposed.
    /// </summary>
    /// <param name="callerToken">The caller's cancellation token.</param>
    /// <param name="deadlineToken">The deadline source's token (<see
    /// cref="CancellationToken.None"/> when no deadline is armed).</param>
    /// <returns><c>true</c> when the cancellation came from the deadline.</returns>
    internal static bool IsHandshakeTimeout(CancellationToken callerToken, CancellationToken deadlineToken) =>
        !callerToken.IsCancellationRequested && deadlineToken.IsCancellationRequested;

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
}
