namespace telnet_cs.Server
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using telnet_cs.Client;
    using telnet_cs.IO;
    using telnet_cs.Protocol;
    using telnet_cs.Transport;

    /// <summary>
    /// One accepted Telnet connection (see <see cref="TelnetServer"/>): the
    /// server-side counterpart to <see cref="Client"/>. Deliberately not a
    /// <c>Client</c> subclass — same wire engine and I/O semantics, opposite
    /// role defaults (offers instead of requests, prompts instead of logging
    /// in). Derives from <see cref="BaseClient"/> for the role-neutral
    /// machinery: stream ownership, send/read rate limits, cancellation, and
    /// terminator helpers.
    /// </summary>
    public partial class ServerSession : BaseClient
    {
        /// <summary>
        /// Gets the per-instance settings. The reference passed to the
        /// constructor is kept (live settings, as with
        /// <see cref="Client.Settings"/>).
        /// </summary>
        public TelnetServerOptions Settings { get; }

        /// <summary>
        /// Gets the persistent RFC 1143 negotiation state for this connection,
        /// fed to every per-read <see cref="ByteStreamHandler"/> so repeats are
        /// not re-answered and refusals are remembered for the life of the
        /// session.
        /// </summary>
        public NegotiationState Negotiation { get; } = new();

        /// <summary>
        /// Gets or sets whether the accepted socket runs over TLS. Set by
        /// <see cref="TelnetServer.AcceptSessionAsync"/>; gates MCCP
        /// (refused over TLS, CRIME/BREACH).
        /// </summary>
        internal bool IsTls { get; set; }

        /// <summary>
        /// Initialises a new instance of the <see cref="ServerSession"/> class
        /// with default <see cref="TelnetServerOptions"/>.
        /// </summary>
        /// <param name="byteStream">The accepted connection's byte stream. Ownership transfers to this session.</param>
        /// <param name="token">The cancellation token.</param>
        public ServerSession(IByteStream byteStream, CancellationToken token)
          : this(byteStream, new TelnetServerOptions(), token)
        {
        }

        /// <summary>
        /// Initialises a new instance of the <see cref="ServerSession"/> class.
        /// Unlike <see cref="Client"/>, no proactive negotiation is sent here:
        /// the session emits the server opening preset (S2) or stays silent when
        /// the preset is fully toggled off, so an accepted socket is never
        /// surprised by client-role bytes.
        /// </summary>
        /// <param name="byteStream">The accepted connection's byte stream. Ownership transfers to this session.</param>
        /// <param name="options">The server settings. The reference is kept.</param>
        /// <param name="token">The cancellation token.</param>
        public ServerSession(IByteStream byteStream, TelnetServerOptions options, CancellationToken token)
          : base(byteStream, token)
        {
            // NOTE: byteStream is validated by the base constructor; options cannot
            // be validated before the base call, so a null options reference still
            // constructs the session before throwing below (same shape as Client).
            ArgumentNullException.ThrowIfNull(options);
            Settings = options;
            Timeout = options.IdleTimeout;
            StartIdleTimer();
        }

        /// <summary>
        /// Reads asynchronously from the session.
        /// </summary>
        /// <returns>Any text read from the session.</returns>
        public Task<string> ReadAsync()
        {
            return ReadAsync(TimeSpan.FromMilliseconds(DefaultTimeoutMs));
        }

        /// <summary>
        /// Reads asynchronously from the session.
        /// </summary>
        /// <param name="timeout">The timeout.</param>
        /// <returns>Any text read from the session.</returns>
        public Task<string> ReadAsync(TimeSpan timeout)
        {
            return ReadAsync(timeout, CancellationToken.None);
        }

        /// <summary>
        /// Reads asynchronously from the session.
        /// </summary>
        /// <param name="timeout">The timeout.</param>
        /// <param name="cancellationToken">Token to cancel the read. Cancellation returns the partial text read so far.</param>
        /// <returns>Any text read from the session.</returns>
        public override async Task<string> ReadAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            // Serialise concurrent reads so interleaved calls cannot split input.
            // A cancelled wait means "no data", not an error.
            try
            {
                await ReadRateLimit.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return string.Empty;
            }

            try
            {
                // Drain text a terminated read stashed past its terminator
                // before touching the wire, so pipelined data is never lost.
                string pending = PendingText;
                PendingText = string.Empty;
                if (pending.Length != 0)
                {
                    return pending;
                }

                // A per-read linked source: an IP command aborts this read without
                // cancelling the session's own InternalCancellation (which must
                // survive for subsequent reads). Safe to dispose: the handler no
                // longer disposes (or cancels) anything it does not own.
                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(InternalCancellation.Token, cancellationToken))
                using (var handler = new ByteStreamHandler(ByteStream, linked, MillisecondReadDelay))
                {
                    FeedSession(handler);
                    try
                    {
                        string result = await handler.ReadAsync(timeout).ConfigureAwait(false);
                        if (!tlsHelloChecked && handler.FirstInboundByte != -1)
                        {
                            // First-data-only TLS sniff (reference data_received):
                            // a 0x16 lead byte on a plaintext listener is a TLS
                            // ClientHello — warn and close instead of parsing it.
                            tlsHelloChecked = true;
                            if (Settings.ServerCertificate is null && handler.FirstInboundByte == 0x16)
                            {
                                WriteLog($"TLS ClientHello from {RemoteEndPoint ?? "unknown"} but server has no SSL context -- closing connection.");
                                ByteStream.Close();
                                return string.Empty;
                            }
                        }

                        if (result.Length != 0)
                        {
                            Context.NoteRead(result);
                        }

                        // Deferred opening negotiation (WILL ECHO, DO
                        // NEW_ENVIRON, encoding check) armed by this read.
                        await FlushDeferredNegotiationAsync(linked.Token).ConfigureAwait(false);
                        return result;
                    }
                    catch (System.Net.Sockets.SocketException)
                    {
                        // Dead peer, like every other read-path death: empty.
                        return string.Empty;
                    }
                    finally
                    {
                        sbResumeState = handler.SbResumeState;
                    }
                }
            }
            finally
            {
                ReadRateLimit.Release();
            }
        }

        /// <summary>
        /// Writes the specified <paramref name="command"/> to the peer.
        /// </summary>
        /// <param name="command">The command.</param>
        /// <param name="cancellationToken">A token to cancel the write.</param>
        /// <returns>An awaitable Task.</returns>
        public async Task WriteAsync(string command, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(command);
            if (Settings.TextEncoding != null)
            {
                // Custom encoding: pre-encode here so the exact bytes hit the stream.
                // Already IAC-escaped by the converter, so send raw.
                await WriteRawAsync(ByteStringConverter.ConvertStringToByteArray(command, Settings.TextEncoding), cancellationToken).ConfigureAwait(false);
                Context.NoteWritten(command);
                return;
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, InternalCancellation.Token);
            if (ByteStream.Connected && !linked.Token.IsCancellationRequested)
            {
                await SendRateLimit.WaitAsync(linked.Token).ConfigureAwait(false);
                try
                {
                    await ByteStream.WriteAsync(command, linked.Token).ConfigureAwait(false);
                    Context.NoteWritten(command);
                }
                finally
                {
                    SendRateLimit.Release();
                }
            }
        }

        /// <summary>
        /// Writes the specified <paramref name="data"/> to the peer.
        /// </summary>
        /// <param name="data">The byte array to send.</param>
        /// <param name="cancellationToken">A token to cancel the write.</param>
        /// <returns>An awaitable Task.</returns>
        public async Task WriteAsync(byte[] data, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(data);
            // RFC 854: a literal IAC byte in user data must be escaped by
            // doubling (telnetlib3 write() parity). Protocol frames bypass
            // this method and write to the byte stream directly.
            var escaped = ByteStringConverter.EscapeIacBytes(data);
            await WriteRawAsync(escaped, cancellationToken).ConfigureAwait(false);
            Context.NoteWritten(data.Length);
        }

        private async Task WriteRawAsync(byte[] data, CancellationToken cancellationToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, InternalCancellation.Token);
            if (ByteStream.Connected && !linked.Token.IsCancellationRequested)
            {
                await SendRateLimit.WaitAsync(linked.Token).ConfigureAwait(false);
                try
                {
                    await ByteStream.WriteAsync(data, 0, data.Length, linked.Token).ConfigureAwait(false);
                }
                finally
                {
                    SendRateLimit.Release();
                }
            }
        }

        /// <summary>
        /// Writes the specified <paramref name="command"/> plus an RFC 854
        /// compliant <c>"\r\n"</c> line feed to the peer.
        /// </summary>
        /// <param name="command">The command.</param>
        /// <param name="cancellationToken">A token to cancel the write.</param>
        /// <returns>An awaitable Task.</returns>
        public Task WriteLineAsync(string command, CancellationToken cancellationToken = default)
        {
            return WriteLineAsync(command, LineFeed.Rfc854, cancellationToken);
        }

        /// <summary>
        /// Writes the specified <paramref name="command"/> plus
        /// <paramref name="lineFeed"/> to the peer.
        /// </summary>
        /// <param name="command">The command.</param>
        /// <param name="lineFeed">The type of lineFeed to use. RFC 854 CR+LF by default; pass <c>LineFeed.Legacy</c> for bare "\n".</param>
        /// <returns>An awaitable Task.</returns>
        public Task WriteLineAsync(string command, string lineFeed)
        {
            return WriteLineAsync(command, lineFeed, CancellationToken.None);
        }

        /// <summary>
        /// Writes the specified <paramref name="command"/> plus
        /// <paramref name="lineFeed"/> to the peer.
        /// </summary>
        /// <param name="command">The command.</param>
        /// <param name="lineFeed">The type of lineFeed to use. RFC 854 CR+LF by default; pass <c>LineFeed.Legacy</c> for bare "\n".</param>
        /// <param name="cancellationToken">A token to cancel the write.</param>
        /// <returns>An awaitable Task.</returns>
        public Task WriteLineAsync(string command, string lineFeed, CancellationToken cancellationToken = default)
        {
            return WriteAsync($"{command}{lineFeed}", cancellationToken);
        }

        private void FeedSession(ByteStreamHandler handler)
        {
            handler.Negotiation = Negotiation;
            handler.TextEncoding = Settings.TextEncoding;
            handler.Log = Settings.Log;
            // The accepted socket's TLS flag gates MCCP (refused over TLS).
            handler.IsTlsActive = IsTls;
            handler.EnableMccp = Settings.EnableMccp;
            // MCCP agreement is session-lived (arming SB, stream end, and
            // corrupt shutdown report back through the hook).
            handler.Mccp2Active = mccp2Agreed;
            handler.Mccp3Active = mccp3Agreed;
            handler.MccpStream = mccpStream;
            handler.MccpStateChanged = (mccp2, mccp3, stream) =>
            {
                mccp2Agreed = mccp2;
                mccp3Agreed = mccp3;
                mccpStream = stream;
            };
            // A received CHARSET (or encoding-suffixed LANG) environment
            // entry presumes BINARY capability: decode 8-bit data even
            // without an agreed inbound BINARY direction. An agreed CHARSET
            // additionally switches the read encoding to the charset.
            handler.ForceBinaryDecoding = forceBinaryDecoding;
            if (charsetEncoding is not null)
            {
                handler.TextEncoding = charsetEncoding;
            }

            // Server role: a simultaneous inbound CHARSET REQUEST (one
            // arriving while our own REQUEST is outstanding) is REJECTED
            // (RFC 2066 §5); the outstanding flag is session-lived because
            // handlers are per-read.
            handler.IsServerRole = true;
            handler.CharsetRequestPending = IsCharsetOutstanding;
            // Server-role subnegotiation answers (TTYPE/TSPEED/ENVIRON IS and
            // INFO, inbound NAWS) land in the S3 collectors; anything unconsumed
            // falls through to the normal responder path.
            handler.SubnegotiationResponse = OnSubnegotiationResponse;
            handler.Linemode = linemodeState;
            handler.ApplyLinemodeAsServer = true;
            // Only a server may send SB LFLOW (RFC 1372): agreeing to a peer
            // WILL LFLOW volunteers the configured restart mode.
            handler.SendLineflowAsServer = true;
            handler.GoAheadReceived = OnGoAheadReceived;
            // A server has no local user: console echo defaults off (opt in via
            // settings for debugging), and the machine must never beep at a peer.
            handler.IsWriteConsole = Settings.IsWriteConsole ?? false;
            handler.EnableBell = false;
            // The server performs echoing: an inbound DO ECHO (including the
            // confirmation of our own WILL ECHO) is answered WILL, and agreed
            // inbound bytes echo down the wire via the unchanged EchoBackAsync
            // path. Gated by OfferEcho so operators can refuse echo entirely;
            // the bounce guard (never both directions) still applies.
            handler.AllowRemoteEcho = Settings.OfferEcho;
            // Secret-prompt suppression (AuthenticateAsync): negotiation state
            // is untouched, only our echo-back replay is withheld.
            handler.SuppressEchoBack = echoBackSuppressed;
            // MUD stores are session-lived (handlers are per-read): the
            // append/replace reports come back through the typed hooks.
            handler.MsspReceived = vars =>
            {
                lock (collectorLock)
                {
                    mudMsspData = new Dictionary<string, object>(vars);
                }
            };
            handler.MxpReceived = body =>
            {
                lock (collectorLock)
                {
                    mudMxpData.Add(body);
                }
            };
            handler.MspReceived = body =>
            {
                lock (collectorLock)
                {
                    mudMspData.Add(body);
                }
            };
            handler.ZmpReceived = (command, args) =>
            {
                lock (collectorLock)
                {
                    mudZmpData[command] = args;
                }
            };
            handler.AardwolfReceived = message =>
            {
                lock (collectorLock)
                {
                    mudAardwolfData.Add(message);
                }
            };
            handler.AtcpReceived = (package, value) =>
            {
                lock (collectorLock)
                {
                    mudAtcpData.Add((package, value));
                }
            };
            // Subnegotiation continuation is session-lived (handlers are
            // per-read): a frame split across reads reassembles instead of
            // dropping its first half (telnetlib3 _sb_buffer parity).
            handler.SbResumeState = sbResumeState;
            // TerminalType/TerminalSpeed keep their "vt100"/"19200,19200"
            // defaults: harmless responder values if a peer ever SENDs to us.
        }
    }
}
