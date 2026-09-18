namespace telnet_cs.Client;

using System;
using System.Net.Security;
using System.Threading;
using System.Threading.Tasks;
using telnet_cs.IO;
using telnet_cs.Protocol;
using telnet_cs.Transport;

/// <summary>
/// Basic Telnet client.
/// Terminal type and speed can be configured via static properties on the <see cref="Client"/> class.
/// <see cref="Client"/>.IsWriteConsole can be used to configure whether to write output to the console; often useful for debugging purposes.
/// Per-instance settings (including TLS) flow through <c>Settings</c>, configured via the <c>ConnectAsync</c> overload that takes <c>TelnetClientOptions</c>.
/// </summary>
/// <remarks>
/// Automation scope (intentional differences from a full interactive
/// stack): a client is single-use — connect, script the exchange, dispose;
/// there is no reconnect, no interactive shell, and no blocking
/// "wait until connected": <see cref="CreateAsync"/> waits with
/// <c>Task.Delay</c> and throws when the stream is not connected in time.
/// Size changes have no SIGWINCH equivalent: poll
/// <see cref="RefreshWindowSizeAsync"/> after updating
/// <c>Settings.WindowWidth</c>/<c>WindowHeight</c> before calling;
/// unchanged sizes send nothing (a 0 dimension is sent as-is, RFC 1073
/// "unspecified").
/// One deliberate extension differs from the reference here: a TCP-urgent
/// Synch enters a discard scan that drops data until in-band <c>IAC
/// DM</c> — the reference delivers that data. It is pinned by tests and
/// kept by design; there is no opt-out.
/// </remarks>
public partial class Client
{
    /// <summary>
    /// Legacy <c>"\n"</c> line feed, kept for explicit opt-in (e.g. binary-mode
    /// peers). It is no longer any write-path default.
    /// </summary>
    public const string LegacyLineFeed = LineFeed.Legacy;

    /// <summary>
    /// RFC 854 compliant <c>"\r\n"</c> line feed. This is the default for
    /// <c>WriteLineAsync</c>.
    /// </summary>
    public const string Rfc854LineFeed = LineFeed.Rfc854;

    /// <summary>
    /// Skips the proactive option negotiation on connect. Prefer the
    /// per-instance <see cref="CreateAsync"/> flag on new code; this static
    /// remains for backward compatibility.
    /// </summary>
    public static bool SkipProactiveOptionNegotiation
    {
        get => _skipProactiveFlow.CurrentOr(field) == true;
        set => field = value;
    } = true;

    internal static FlowLocal<bool> SkipProactiveOverride => _skipProactiveFlow;

    private static readonly FlowLocal<bool> _skipProactiveFlow = new();

    /// <summary>
    /// Gets or sets the process-wide log hook. Falls back to
    /// <see cref="System.Diagnostics.Debug"/> when unset.
    /// </summary>
    public static Action<string>? Trace
    {
        get => _traceFlow.CurrentOr(field);
        set => field = value;
    }

    internal static FlowLocal<Action<string>?> TraceOverride => _traceFlow;

    private static readonly FlowLocal<Action<string>?> _traceFlow = new();

    private Client(IByteStream byteStream, CancellationToken token, bool deferConnect)
      : base(byteStream, token)
    {
        _ = deferConnect;
    }

    /// <summary>
    /// Creates a <see cref="Client"/> over an already-provided <see cref="IByteStream"/>
    /// without blocking the calling thread: waits for <c>Connected</c> with
    /// <c>Task.Delay</c> and runs the proactive negotiation asynchronously,
    /// so it never blocks on async work and honours cancellation. This
    /// factory is the only way to build a client over an existing stream.
    /// </summary>
    /// <param name="byteStream">The stream served by the host connected to.</param>
    /// <param name="timeout">The timeout to wait for initial successful connection to <paramref name="byteStream"/>.</param>
    /// <param name="cancellationToken">The token to monitor for cancellation requests.</param>
    /// <param name="options">Additional options to send during negotiation. Null behaves like an empty array.</param>
    /// <param name="skipProactiveNegotiation">When <c>true</c> (the default),
    /// suppresses the opening <c>IAC DO SuppressGoAhead</c> (and the
    /// <paramref name="options"/> sends). Per-instance alternative to the
    /// process-global <see cref="SkipProactiveOptionNegotiation"/>: a client
    /// and a server session can coexist in one process with different choices.</param>
    /// <returns>A connected <see cref="Client"/> owning its stream. Dispose it when done.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="byteStream"/> is <c>null</c>.</exception>
    /// <exception cref="InvalidOperationException">The stream did not connect within <paramref name="timeout"/>.</exception>
    /// <exception cref="OperationCanceledException">Cancelled via <paramref name="cancellationToken"/>.</exception>
    public static async Task<Client> CreateAsync(IByteStream byteStream, TimeSpan timeout, CancellationToken cancellationToken = default, (Commands Command, Options Option)[]? options = null, bool skipProactiveNegotiation = true)
    {
        ArgumentNullException.ThrowIfNull(byteStream);
        var pending = options ?? [];
        var client = new Client(byteStream, cancellationToken, deferConnect: true);
        try
        {
            await client.WaitForConnectionAsync(timeout, cancellationToken).ConfigureAwait(false);
            if (!SkipProactiveOptionNegotiation && !skipProactiveNegotiation)
            {
                await client.ProactiveOptionNegotiation().ConfigureAwait(false);
            }

            if (!skipProactiveNegotiation)
            {
                foreach (var option in pending)
                {
                    await client.NegotiateOption(option.Command, option.Option).ConfigureAwait(false);
                }
            }

            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private async Task WaitForConnectionAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var timeoutEnd = DateTime.UtcNow.Add(timeout);
        while (!ByteStream.Connected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (DateTime.UtcNow >= timeoutEnd)
            {
                throw new InvalidOperationException("Unable to connect to the host.");
            }

            await Task.Delay(2, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Connects to a Telnet server, honouring cancellation and a connect timeout.
    /// </summary>
    /// <param name="hostname">The hostname.</param>
    /// <param name="port">The port.</param>
    /// <param name="cancellationToken">The token to monitor for cancellation requests.</param>
    /// <param name="timeout">The maximum time to wait for the TCP connect. Defaults to 30 seconds.</param>
    /// <returns>A connected <see cref="Client"/> owning its stream. Dispose it when done.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="hostname"/> is <c>null</c>.</exception>
    /// <exception cref="InvalidOperationException">The connection could not be established within <paramref name="timeout"/>.</exception>
    /// <exception cref="System.Net.Sockets.SocketException">The TCP dial failed (DNS, refused, unreachable).</exception>
    public static Task<Client> ConnectAsync(string hostname, int port, CancellationToken cancellationToken = default, TimeSpan? timeout = null)
    {
        return ConnectAsync(hostname, port, null, cancellationToken, timeout);
    }

    /// <summary>
    /// Connects to a Telnet server, honouring cancellation and a connect timeout.
    /// When <paramref name="options"/> enables <c>UseTls</c>, the TLS handshake
    /// completes before the first telnet byte flows (implicit TLS).
    /// </summary>
    /// <param name="hostname">The hostname.</param>
    /// <param name="port">The port.</param>
    /// <param name="options">Per-instance settings, applied to the returned client's <c>Settings</c>. Null behaves like the overload without options.</param>
    /// <param name="cancellationToken">The token to monitor for cancellation requests.</param>
    /// <param name="timeout">The maximum time for the TCP connect plus, when TLS is on, the handshake (cancelled at the connect deadline). Defaults to 30 seconds.</param>
    /// <returns>A connected <see cref="Client"/> owning its stream. Dispose it when done.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="hostname"/> is <c>null</c>.</exception>
    /// <exception cref="InvalidOperationException">The connection (or the TLS handshake) could not be established within <paramref name="timeout"/>.</exception>
    /// <exception cref="System.Net.Sockets.SocketException">The TCP dial failed (DNS, refused, unreachable).</exception>
    public static async Task<Client> ConnectAsync(string hostname, int port, TelnetClientOptions? options, CancellationToken cancellationToken = default, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(hostname);
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(30);
        ArgumentOutOfRangeException.ThrowIfLessThan(effectiveTimeout, TimeSpan.Zero);
        var deadline = DateTime.UtcNow.Add(effectiveTimeout);
        var tcpClient = new System.Net.Sockets.TcpClient();
        try
        {
            var connectTask = tcpClient.ConnectAsync(hostname, port);
            // A timed-out dial keeps running: observe a late fault here so it
            // can never surface as an unobserved task exception.
            _ = connectTask.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            var completed = await Task.WhenAny(connectTask, Task.Delay(effectiveTimeout, cancellationToken)).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (completed != connectTask)
            {
                throw new InvalidOperationException($"Unable to connect to {hostname}:{port} within {effectiveTimeout}.");
            }

            await connectTask.ConfigureAwait(false);
#pragma warning disable CA2000 // Ownership of the stream transfers to the Client on success; the catch below releases it otherwise.
            TcpClient tcpSocket = new TcpClient(tcpClient);
            ISocket socket = tcpSocket;
            if (options?.UseTls is true)
            {
                socket = await HandshakeTlsAsync(tcpSocket, hostname, options, deadline, cancellationToken).ConfigureAwait(false);
            }

            // The socket just connected, so no connect wait remains: build
            // directly on the deferred constructor instead of CreateAsync.
            var client = new Client(new TcpByteStream(socket), CancellationToken.None, deferConnect: true);
            if (options is not null)
            {
                client.ApplyOptions(options);
            }

            return client;
#pragma warning restore CA2000
        }
        catch
        {
            // Ownership transfers to the Client only on success. A failed connect,
            // handshake, or client setup must still release the socket itself
            // (disposing the raw client releases the connection; only an
            // SslStream native context created mid-handshake falls to
            // finalization).
            tcpClient.Dispose();
            throw;
        }
    }

    private static async Task<TlsSocket> HandshakeTlsAsync(TcpClient socket, string hostname, TelnetClientOptions options, DateTime deadline, CancellationToken cancellationToken)
    {
        var auth = new SslClientAuthenticationOptions
        {
            TargetHost = options.TlsHost ?? hostname,
            EnabledSslProtocols = options.TlsProtocols,
        };
        if (options.TlsClientCertificates is { Count: > 0 })
        {
            auth.ClientCertificates = options.TlsClientCertificates;
        }

        if (options.TlsValidationCallback is not null)
        {
            auth.RemoteCertificateValidationCallback = options.TlsValidationCallback;
        }

        var remaining = deadline - DateTime.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            throw new InvalidOperationException($"TLS handshake with {hostname} timed out: the connect consumed the whole timeout.");
        }

        using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        handshakeCts.CancelAfter(remaining);
        try
        {
            return await TlsSocket.AuthenticateAsClientAsync(socket, auth, handshakeCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A user cancel must stay a cancel; anything else is the handshake
            // missing the connect deadline (SslStream has no timeout of its own).
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException($"TLS handshake with {hostname} timed out.");
        }
    }

    /// <summary>
    /// Gets and sets a value indicating whether the <see cref="Client"/> should write responses received via <see cref="ByteStreamHandler"/>.Read to the Console.
    /// </summary>
    public static bool IsWriteConsole { get; set; }

    /// <summary>
    /// Gets and sets the TerminalType to negotiate.
    /// </summary>
    public static string TerminalType
    {
        get => _terminalTypeFlow.CurrentOr(field) ?? field;
        set => field = value;
    } = "unknown";

    internal static FlowLocal<string> TerminalTypeOverride => _terminalTypeFlow;

    private static readonly FlowLocal<string> _terminalTypeFlow = new();

    /// <summary>
    /// Gets and sets the TerminalSpeed to negotiate.
    /// </summary>
    public static string TerminalSpeed
    {
        get => _terminalSpeedFlow.CurrentOr(field) ?? field;
        set => field = value;
    } = "38400,38400";

    internal static FlowLocal<string> TerminalSpeedOverride => _terminalSpeedFlow;

    private static readonly FlowLocal<string> _terminalSpeedFlow = new();

    internal static readonly byte[] SuppressGoAheadBuffer =
    [
      (byte)Commands.InterpretAsCommand,
  (byte)Commands.Do,
  (byte)Options.SuppressGoAhead,
];

    /// <summary>
    /// Sending <see cref="Commands.Do"/> <see cref="Options.SuppressGoAhead"/> up front will get us to the logon prompt faster.
    /// </summary>
    private Task ProactiveOptionNegotiation()
    {
        return SendNegotiationBytesAsync(Negotiation.RequestEnable((int)Options.SuppressGoAhead), Options.SuppressGoAhead);
    }

    /// <summary>
    /// Negotiate Option specified.
    /// </summary>
    private Task NegotiateOption(Commands command, Options option)
    {
        Commands? verb = command switch
        {
            Commands.Do => Negotiation.RequestEnable((int)option),
            Commands.Dont => Negotiation.RequestDisable((int)option),
            Commands.Will => Negotiation.OfferEnable((int)option),
            Commands.Wont => Negotiation.OfferDisable((int)option),
            // Anything else is not a negotiation verb: sending it with an
            // option byte would emit a malformed three-byte frame, so drop it.
            _ => null,
        };
        return SendNegotiationBytesAsync(verb, option);
    }

    private Task SendNegotiationBytesAsync(Commands? verb, Options option)
    {
        if (verb is null)
        {
            return Task.CompletedTask;
        }

        byte[] buffer = [(byte)Commands.InterpretAsCommand, (byte)verb, (byte)option];
        return WriteStream.WriteAsync(buffer, 0, buffer.Length, InternalCancellation.Token);
    }
}
