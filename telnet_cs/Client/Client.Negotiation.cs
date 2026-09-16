namespace telnet_cs.Client
{
    using System;
    using System.Collections.Generic;
    using System.Security.Cryptography.X509Certificates;
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
        public TelnetClientOptions Settings { get; private set; } = new();

        /// <summary>
        /// Gets or sets the per-instance terminated-read length override in
        /// chars. Null (the default) follows
        /// <see cref="TelnetClientOptions.MaxTerminatedReadChars"/>; any
        /// non-negative value wins for this client only (<c>0</c> disables
        /// the limit). Negative values are rejected.
        /// </summary>
        public int? MaxTerminatedReadChars
        {
            get;
            set
            {
                if (value < 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(value), "MaxTerminatedReadChars must be >= 0 (0 disables the limit).");
                }

                field = value;
            }
        }

        /// <summary>
        /// Snapshots <paramref name="options"/> into <see cref="Settings"/> via
        /// a compiler-generated <c>with</c>-clone: every current and future
        /// member flows automatically, so a new member can never be silently
        /// dropped here.
        /// <c>ConnectAsync</c> overload taking options must honor the whole
        /// object, not just the TLS members it consumes during the handshake.
        /// </summary>
        /// <param name="options">The options to apply.</param>
        internal void ApplyOptions(TelnetClientOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            ArgumentOutOfRangeException.ThrowIfNegative(options.MaxTerminatedReadChars);
            // Collections are re-seated, not shared: the client owns its copies,
            // so later mutations on either side stay independent.
            Settings = options with
            {
                TerminalTypes = [.. options.TerminalTypes],
                EnvironmentUserVars = new Dictionary<string, string>(options.EnvironmentUserVars, StringComparer.Ordinal),
                CharsetOffers = [.. options.CharsetOffers],
                ZmpSupportedCommands = [.. options.ZmpSupportedCommands],
                TlsClientCertificates = options.TlsClientCertificates is null ? null : new X509CertificateCollection(options.TlsClientCertificates),
            };
        }

        /// <summary>
        /// Gets the persistent RFC 1143 negotiation state for this connection.
        /// It is fed to every per-read <see cref="ByteStreamHandler"/> (see
        /// <see cref="Client.ReadAsync(TimeSpan, CancellationToken)"/>), so
        /// repeated commands are not re-answered and refusals are remembered
        /// for the life of the client. Render snapshots from it (P4 STATUS).
        /// </summary>
        public NegotiationState Negotiation { get; } = new();

        /// <inheritdoc/>
        protected override NegotiationState SessionNegotiation => Negotiation;

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
        /// RFC 860 §5. Tracked by <see cref="Negotiation"/>: a repeat while
        /// the request is outstanding sends nothing, but a repeat after the
        /// mark was agreed re-pings (every <c>DO TM</c> is answered).
        /// </summary>
        /// <returns>An awaitable Task.</returns>
        /// <param name="cancellationToken">A token to cancel the send.</param>
        public Task SendTimingMarkAsync(CancellationToken cancellationToken = default)
        {
            return SendRequestAsync(Negotiation.RequestTimingMark(), Options.TimingMark, cancellationToken);
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
