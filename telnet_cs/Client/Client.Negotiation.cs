namespace telnet_cs.Client
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using telnet_cs.IO;
    using telnet_cs.Protocol;

    public partial class Client
    {
        /// <summary>
        /// Gets the per-instance settings. Initialised empty (follow the statics);
        /// mutate its members to override behaviour for this client only.
        /// </summary>
        public TelnetClientOptions Settings { get; } = new();

        /// <summary>
        /// Copies every member of <paramref name="options"/> into
        /// <see cref="Settings"/>. Single place owning the field list: the
        /// <c>ConnectAsync</c> overload taking options must honor the whole
        /// object, not just the TLS members it consumes during the handshake.
        /// </summary>
        /// <param name="options">The options to apply.</param>
        internal void ApplyOptions(TelnetClientOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            Settings.TerminalType = options.TerminalType;
            Settings.TerminalTypes.Clear();
            foreach (string entry in options.TerminalTypes)
            {
                Settings.TerminalTypes.Add(entry);
            }

            Settings.TerminalSpeed = options.TerminalSpeed;
            Settings.XDisplayLocation = options.XDisplayLocation;
            Settings.IsWriteConsole = options.IsWriteConsole;
            Settings.AllowRemoteEcho = options.AllowRemoteEcho;
            Settings.EnableBell = options.EnableBell;
            Settings.TextEncoding = options.TextEncoding;
            Settings.WindowWidth = options.WindowWidth;
            Settings.WindowHeight = options.WindowHeight;
            Settings.Log = options.Log;
            Settings.EnvironmentUser = options.EnvironmentUser;
            Settings.EnvironmentDisplay = options.EnvironmentDisplay;
            Settings.EnvironmentUserVars.Clear();
            foreach (var pair in options.EnvironmentUserVars)
            {
                Settings.EnvironmentUserVars.Add(pair.Key, pair.Value);
            }

            Settings.UseTls = options.UseTls;
            Settings.TlsHost = options.TlsHost;
            Settings.TlsValidationCallback = options.TlsValidationCallback;
            Settings.TlsClientCertificates = options.TlsClientCertificates;
            Settings.TlsProtocols = options.TlsProtocols;
        }

        /// <summary>
        /// Gets the persistent RFC 1143 negotiation state for this connection.
        /// It is fed to every per-read <see cref="ByteStreamHandler"/> (see
        /// <see cref="Client.ReadAsync(TimeSpan, CancellationToken)"/>), so
        /// repeated commands are not re-answered and refusals are remembered
        /// for the life of the client. Render snapshots from it (P4 STATUS).
        /// </summary>
        public NegotiationState Negotiation { get; } = new();

        /// <summary>
        /// Asks the peer to enable <paramref name="telnetOption"/> (sends
        /// <c>IAC DO</c>), unless already enabled, already negotiating, or
        /// refused without new stimulus (see <see cref="Negotiation"/>).
        /// An explicit call is new stimulus and clears a remembered refusal.
        /// </summary>
        /// <param name="telnetOption">The option to request.</param>
        /// <param name="cancellationToken">A token to cancel the send.</param>
        public Task RequestEnableAsync(Options telnetOption, CancellationToken cancellationToken = default)
        {
            return SendRequestAsync(Negotiation.RequestEnable((int)telnetOption), telnetOption, cancellationToken);
        }

        /// <summary>
        /// Asks the peer to disable <paramref name="telnetOption"/> (sends
        /// <c>IAC DONT</c>), unless already disabled or already negotiating
        /// (see <see cref="Negotiation"/>).
        /// </summary>
        /// <param name="telnetOption">The option to refuse.</param>
        /// <param name="cancellationToken">A token to cancel the send.</param>
        public Task RequestDisableAsync(Options telnetOption, CancellationToken cancellationToken = default)
        {
            return SendRequestAsync(Negotiation.RequestDisable((int)telnetOption), telnetOption, cancellationToken);
        }

        /// <summary>
        /// Asks the peer to place a timing mark (sends <c>IAC DO
        /// TIMING-MARK</c>, RFC 860). The peer returns <c>IAC WILL
        /// TIMING-MARK</c> once everything sent before the mark has drained,
        /// which implements the round-trip and flush-discard patterns of
        /// RFC 860 §5. Tracked by <see cref="Negotiation"/> like any other
        /// request: a repeat while the request is outstanding sends nothing.
        /// </summary>
        /// <returns>An awaitable Task.</returns>
        /// <param name="cancellationToken">A token to cancel the send.</param>
        public Task SendTimingMarkAsync(CancellationToken cancellationToken = default)
        {
            return RequestEnableAsync(Options.TimingMark, cancellationToken);
        }

        private async Task SendRequestAsync(Commands? verb, Options option, CancellationToken cancellationToken)
        {
            if (verb is null)
            {
                return;
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, InternalCancellation.Token);
            if (ByteStream.Connected && !linked.Token.IsCancellationRequested)
            {
                await SendRateLimit.WaitAsync(linked.Token).ConfigureAwait(false);
                try
                {
                    await SendNegotiationBytesAsync(verb, option).ConfigureAwait(false);
                }
                finally
                {
                    SendRateLimit.Release();
                }
            }
        }
    }
}
