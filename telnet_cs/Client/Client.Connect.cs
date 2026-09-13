namespace telnet_cs.Client
{
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
        /// per-instance constructor flag on new code; this static remains for
        /// backward compatibility.
        /// </summary>
        public static bool SkipProactiveOptionNegotiation
        {
            get => _skipProactiveFlow.CurrentOr(_skipProactiveDefault) == true;
            set => _skipProactiveDefault = value;
        }

        internal static FlowLocal<bool> SkipProactiveOverride => _skipProactiveFlow;

        private static bool _skipProactiveDefault = true;

        private static readonly FlowLocal<bool> _skipProactiveFlow = new();

        /// <summary>
        /// Gets or sets the process-wide log hook. Falls back to
        /// <see cref="System.Diagnostics.Debug"/> when unset.
        /// </summary>
        public static Action<string>? Trace
        {
            get => _traceFlow.CurrentOr(_traceDefault);
            set => _traceDefault = value;
        }

        internal static FlowLocal<Action<string>?> TraceOverride => _traceFlow;

        private static Action<string>? _traceDefault;

        private static readonly FlowLocal<Action<string>?> _traceFlow = new();

        /// <summary>
        /// Initialises a new instance of the <see cref="Client"/> class.
        /// </summary>
        /// <param name="byteStream">The stream served by the host connected to.</param>
        /// <param name="token">The cancellation token.</param>
        public Client(IByteStream byteStream, CancellationToken token)
          : this(byteStream, TimeSpan.FromSeconds(30), token)
        {
        }

        /// <summary>
        /// Initialises a new instance of the <see cref="Client"/> class.
        /// </summary>
        /// <param name="byteStream">The stream served by the host connected to.</param>
        /// <param name="timeout">The timeout to wait for initial successful connection to <paramref name="byteStream"/>. Other overloads default to 30 seconds.</param>
        /// <param name="token">The cancellation token.</param>
        public Client(IByteStream byteStream, TimeSpan timeout, CancellationToken token)
          : this(byteStream, timeout, token, Array.Empty<(Commands Command, Options Option)>())
        { }

        /// <summary>
        /// Initialises a new instance of the <see cref="Client"/> class.
        /// </summary>
        /// <param name="byteStream">The byte stream served by the host connected to.</param>
        /// <param name="timeout">The timeout to wait for initial successful connection to <paramref name="byteStream"/>.</param>
        /// <param name="token">The cancellation token.</param>
        /// <param name="options">Additional options to send during negotiation.</param>
        /// <param name="skipProactiveNegotiation">When <c>true</c> (the default),
        /// suppresses the opening <c>IAC DO SuppressGoAhead</c> (and the
        /// <paramref name="options"/> sends). Per-instance alternative to the
        /// process-global <see cref="SkipProactiveOptionNegotiation"/>: a client
        /// and a server session can coexist in one process with different choices.</param>
        public Client(IByteStream byteStream, TimeSpan timeout, CancellationToken token, (Commands Command, Options Option)[] options, bool skipProactiveNegotiation = true)
          : base(byteStream, token)
        {
            // NOTE: byteStream is validated by the base constructor; options cannot
            // be validated before the base call, so a null options array still
            // constructs (and connects) the client before throwing below.
            ArgumentNullException.ThrowIfNull(options);
            var timeoutEnd = DateTime.UtcNow.Add(timeout);
            using var are = new AutoResetEvent(false);
            while (!ByteStream.Connected && timeoutEnd > DateTime.UtcNow)
            {
                are.WaitOne(2);
            }

            if (!ByteStream.Connected)
            {
                throw new InvalidOperationException("Unable to connect to the host.");
            }
            else
            {
#pragma warning disable VSTHRD002 // Avoid problematic synchronous waits
                // https://stackoverflow.com/questions/70964917/optimising-an-asynchronous-call-in-a-constructor-using-joinabletaskfactory-run
                // GetAwaiter().GetResult() surfaces the real failure directly (an
                // IOException), unlike Task.Wait() which wraps it in AggregateException.
                if (!SkipProactiveOptionNegotiation && !skipProactiveNegotiation)
                {
                    Task.Run(async () => await ProactiveOptionNegotiation().ConfigureAwait(false)).GetAwaiter().GetResult();
                }

                if (!skipProactiveNegotiation)
                {
                    foreach (var option in options)
                    {
                        Task.Run(async () => await NegotiateOption(option.Command, option.Option).ConfigureAwait(false)).GetAwaiter().GetResult();
                    }
                }
#pragma warning restore VSTHRD002 // Avoid problematic synchronous waits
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

                var client = new Client(new TcpByteStream(socket), CancellationToken.None);
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
                // handshake, or Client constructor must still release the socket itself
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
            get => _terminalTypeFlow.CurrentOr(_terminalTypeDefault) ?? _terminalTypeDefault;
            set => _terminalTypeDefault = value;
        }

        internal static FlowLocal<string> TerminalTypeOverride => _terminalTypeFlow;

        private static string _terminalTypeDefault = "vt100";

        private static readonly FlowLocal<string> _terminalTypeFlow = new();

        /// <summary>
        /// Gets and sets the TerminalSpeed to negotiate.
        /// </summary>
        public static string TerminalSpeed
        {
            get => _terminalSpeedFlow.CurrentOr(_terminalSpeedDefault) ?? _terminalSpeedDefault;
            set => _terminalSpeedDefault = value;
        }

        internal static FlowLocal<string> TerminalSpeedOverride => _terminalSpeedFlow;

        private static string _terminalSpeedDefault = "19200,19200";

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

            var buffer = new byte[] { (byte)Commands.InterpretAsCommand, (byte)verb, (byte)option };
            return ByteStream.WriteAsync(buffer, 0, buffer.Length, InternalCancellation.Token);
        }
    }
}
