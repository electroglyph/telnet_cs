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
/// Listens for inbound Telnet connections and accepts them as
/// <see cref="ServerSession"/> instances. All protocol behavior lives in
/// the session; this class owns only the listen socket lifecycle.
/// </summary>
public sealed partial class TelnetServer : IDisposable
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
        ArgumentOutOfRangeException.ThrowIfNegative(options.MaxTerminatedReadChars);
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
            statusTimer = new System.Threading.Timer(static state => (state as TelnetServer ?? throw new InvalidOperationException("Status timer state must be the owning TelnetServer.")).ReportStatus(), this, interval, interval);
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
    /// <c>MaxConnectionsPerIp</c>, <c>AcceptFilter</c>)
    /// runs before any
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
    /// This is <see cref="AcceptTcpAsync"/> followed by
    /// <see cref="NegotiateAsync"/>; use the split path to inspect or
    /// configure the session between admission and the first preset byte.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the accept.</param>
    /// <returns>The accepted session.</returns>
    public async Task<ServerSession> AcceptSessionAsync(CancellationToken cancellationToken = default)
    {
        var session = await AcceptTcpAsync(cancellationToken).ConfigureAwait(false);
        return await NegotiateAsync(session, cancellationToken).ConfigureAwait(false);
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
