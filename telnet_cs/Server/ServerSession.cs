namespace telnet_cs.Server;

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
/// in). Derives from <see cref="TelnetSessionBase"/> for the role-neutral
/// machinery: stream ownership, send/read rate limits, cancellation, and
/// terminator helpers.
/// </summary>
public partial class ServerSession : TelnetSessionBase
{
    /// <summary>
    /// Gets the per-instance settings. The reference passed to the
    /// constructor is kept (live settings, as with
    /// <see cref="Client.Settings"/>).
    /// </summary>
    public TelnetServerOptions Settings { get; }

    /// <summary>
    /// Gets or sets the per-session terminated-read length override in
    /// chars. Null (the default) follows
    /// <see cref="TelnetServerOptions.MaxTerminatedReadChars"/>; any
    /// non-negative value wins for this session only (<c>0</c> disables
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
    /// Gets the persistent RFC 1143 negotiation state for this connection,
    /// fed to every per-read <see cref="ByteStreamHandler"/> so repeats are
    /// not re-answered and refusals are remembered for the life of the
    /// session.
    /// </summary>
    public NegotiationState Negotiation { get; } = new();

    /// <summary>
    /// Gets or sets whether the accepted socket runs over TLS. Set by
    /// <see cref="TelnetServer.AcceptTcpAsync"/>; gates MCCP
    /// (refused over TLS, CRIME/BREACH).
    /// </summary>
    internal bool IsTls { get; set; }

    // Outbound MCCP2 compression (see MaybeStartMccp2Async): null while
    // the wire stays plaintext, otherwise the compressing view every
    // session and handler write goes through. Published once, under the
    // send gate, after the SB start marker went out raw; reads, closes
    // and urgent sends keep using the raw ByteStream.
    private MccpWriteFilter? mccp2Filter;

    // Serializes the publish above against the deferred flush: the view
    // is installed once and runs to disconnect (never unpublished), so
    // the gate only ever guards a single publish.
    private readonly Lock mccpFilterGate = new();

    /// <inheritdoc/>
    protected override IByteStream WriteStream
    {
        get
        {
            lock (mccpFilterGate)
            {
                return mccp2Filter ?? ByteStream;
            }
        }
    }

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
        // The setter arms the idle timer, so no separate start call follows.
        Timeout = options.IdleTimeout;
        StartHandshakeTimer();
        // Background inbound processing (reference data_received): the
        // pump answers negotiation and buffers text even when the caller
        // never reads (see ServerSession.Pump.cs).
        _ = Task.Run(PumpInboundAsync);
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
        // Explicit reads drive the wire themselves; the background pump
        // stays dormant while they do (see PumpInboundAsync).
        Interlocked.Exchange(ref lastExplicitReadTicks, DateTime.UtcNow.Ticks);
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
            // Drain text a terminated read stashed past its terminator —
            // or the background pump buffered — before touching the wire,
            // so pipelined data is never lost.
            string pending = TakePendingText();
            if (pending.Length != 0)
            {
                return pending;
            }

            string result = await ReadWireOnceAsync(timeout, cancellationToken, backgroundPass: false).ConfigureAwait(false);
            if (result.Length == 0)
            {
                // Nothing on the wire: surface a wire error the pump
                // swallowed (if any) instead of reporting empty.
                lock (pumpLock)
                {
                    if (pumpWireError is not null)
                    {
                        var captured = pumpWireError;
                        pumpWireError = null;
                        captured.Throw();
                    }
                }
            }

            return result;
        }
        finally
        {
            ReadRateLimit.Release();
        }
    }

    /// <summary>
    /// One serialized wire pass: feeds a per-read handler from the
    /// session state, reads, notes activity/counters, flushes deferred
    /// negotiation, and round-trips the framing state. Shared by
    /// <see cref="ReadAsync(TimeSpan, CancellationToken)"/> and the
    /// background pump (which holds the same <c>ReadRateLimit</c>, so at
    /// most one pass runs at a time).
    /// </summary>
    /// <param name="timeout">The rolling timeout for no further response.</param>
    /// <param name="callerToken">The caller's cancellation token (linked
    /// with the session's own).</param>
    /// <param name="backgroundPass">True when the background pump (not a
    /// caller read) runs this pass: forwarded to the deferred-negotiation
    /// flush (see there).</param>
    /// <returns>Any text read from the session.</returns>
    private async Task<string> ReadWireOnceAsync(TimeSpan timeout, CancellationToken callerToken, bool backgroundPass)
    {
        // A per-read linked source: caller cancel (or session teardown)
        // aborts this pass without cancelling the session's own
        // InternalCancellation (which must survive for subsequent reads).
        // Safe to dispose: the handler no longer disposes (or cancels)
        // anything it does not own.
        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(InternalCancellation.Token, callerToken))
        using (var handler = new ByteStreamHandler(WriteStream, linked, MillisecondReadDelay))
        {
            FeedSession(handler);
            // Single-flight (ReadRateLimit): this is the only pass
            // running, so subnegotiation callbacks below re-enter on
            // this thread and RefreshActiveReadHandlerEncoding always
            // reaches exactly this handler.
            activeReadHandler = handler;
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
                    MarkHandshakeComplete();
                }

                // Deferred opening negotiation (WILL ECHO, DO
                // NEW_ENVIRON, encoding check) armed by this read.
                await FlushDeferredNegotiationAsync(linked.Token, backgroundPass).ConfigureAwait(false);
                return result;
            }
            catch (OperationCanceledException)
            {
                // A cancelled wait means "no data", not an error
                // (the handler throws on a pre-cancelled read).
                return string.Empty;
            }
            catch (System.Net.Sockets.SocketException)
            {
                // Dead peer, like every other read-path death: empty.
                return string.Empty;
            }
            finally
            {
                activeReadHandler = null;
                sbResumeState = handler.SbResumeState;
                framingState = handler.FramingState;
                // Raw wire bytes the handler pulled (negotiation
                // frames included) and wrote (replies, echo-back)
                // count here; decoded text never does. Inbound bytes —
                // even IAC-only frames with no text — mark activity so
                // keepalives never idle out (see NoteWireTransfer).
                Context.NoteWireTransfer(handler.InboundWireBytes, handler.OutboundWireBytes);
            }
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
        if (Settings.TextEncoding is not null)
        {
            // Custom encoding: pre-encode here so the exact bytes hit the stream.
            // Already IAC-escaped by the converter, so send raw.
            await WriteRawAsync(ByteStringConverter.ConvertStringToByteArray(command, Settings.TextEncoding), cancellationToken).ConfigureAwait(false);
            return;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, InternalCancellation.Token);
        if (WriteStream.Connected && !linked.Token.IsCancellationRequested)
        {
            await SendRateLimit.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                await WriteTextLockedAsync(command, linked.Token).ConfigureAwait(false);
            }
            finally
            {
                SendRateLimit.Release();
            }
        }
    }

    // Null-encoding text send (assumes SendRateLimit is held): the
    // stream path for prompts when no session encoding is configured.
    private async Task WriteTextLockedAsync(string command, CancellationToken cancellationToken)
    {
        await WriteStream.WriteAsync(command, cancellationToken).ConfigureAwait(false);
        // The stream encodes exactly like the converter with a
        // null encoding (its TextEncoding is never set): Latin-1
        // plus IAC escaping, so this is the on-the-wire length.
        Context.NoteWritten(ByteStringConverter.ConvertStringToByteArray(command, null).Length);
    }

    /// <summary>
    /// Writes the specified <paramref name="data"/> to the peer.
    /// </summary>
    /// <param name="data">The byte array to send.</param>
    /// <param name="cancellationToken">A token to cancel the write.</param>
    /// <returns>An awaitable Task.</returns>
    public Task WriteAsync(byte[] data, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        // RFC 854: a literal IAC byte in user data must be escaped by
        // doubling (telnetlib3 write() parity). Protocol frames bypass
        // this method and write to the byte stream directly.
        // WriteRawAsync counts the escaped on-the-wire length.
        var escaped = ByteStringConverter.EscapeIacBytes(data);
        return WriteRawAsync(escaped, cancellationToken);
    }

    private async Task WriteRawAsync(byte[] data, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, InternalCancellation.Token);
        if (WriteStream.Connected && !linked.Token.IsCancellationRequested)
        {
            await SendRateLimit.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                await WriteStream.WriteAsync(data, 0, data.Length, linked.Token).ConfigureAwait(false);
                Context.NoteWritten(data.Length);
            }
            finally
            {
                SendRateLimit.Release();
            }
        }
    }

    /// <summary>
    /// Reports a transmitted <c>IAC GA</c> pair (see
    /// <see cref="TelnetSessionBase.SendGaAsync"/>) to the session counters.
    /// </summary>
    protected override void NoteGaSent()
    {
        Context.NoteWritten(2);
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
        handler.StormGuard = stormGuard;
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
        try
        {
            handler.MaxDecompressedBytes = Settings.MaxDecompressedBytes;
            handler.MaxDecompressionRatio = Settings.MaxDecompressionRatio;
            handler.MaxCompressedBytes = Settings.MaxCompressedBytes;
            handler.MccpCapLog = msg => WriteLog(msg);
            if (mccpStream is not null)
            {
                mccpStream.MaxDecompressedBytes = Settings.MaxDecompressedBytes;
                mccpStream.MaxDecompressionRatio = Settings.MaxDecompressionRatio;
                mccpStream.MaxCompressedBytes = Settings.MaxCompressedBytes;
                mccpStream.CapLog = msg => WriteLog(msg);
            }
        }
        catch
        {
        }

        handler.MccpStateChanged = (mccp2, mccp3, stream) =>
        {
            mccp2Agreed = mccp2;
            mccp3Agreed = mccp3;
            mccpStream = stream;
        };
        // A peer WONT/DONT never stops our outbound view: like the
        // reference compressor it runs to disconnect, so there is no
        // agreement-loss hook to wire here.
        // A received CHARSET (or encoding-suffixed LANG) environment
        // entry presumes BINARY capability: decode 8-bit data even
        // without an agreed inbound BINARY direction. An agreed CHARSET
        // additionally switches the read encoding to the charset. The
        // pair is read atomically: both latch together in
        // TryConsumeCharset, and a split read could mix generations. A
        // latch landing mid-pass is pushed into this same handler by
        // RefreshActiveReadHandlerEncoding, so the snapshot here only
        // needs to be whole, not re-taken.
        bool forceBinary;
        System.Text.Encoding? agreedEncoding;
        lock (collectorLock)
        {
            forceBinary = forceBinaryDecoding;
            agreedEncoding = charsetEncoding;
        }

        handler.ForceBinaryDecoding = forceBinary;
        if (agreedEncoding is not null)
        {
            handler.TextEncoding = agreedEncoding;
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
        handler.Linemode = SharedLinemodeState;
        handler.ApplyLinemodeAsServer = true;
        // Only a server may send SB LFLOW (RFC 1372): agreeing to a peer
        // WILL LFLOW volunteers the configured restart mode.
        handler.SendLineflowAsServer = true;
        handler.GoAheadReceived = OnGoAheadReceived;
        // RFC 727: a DO LOGOUT asks us to end the session. No
        // negotiation bytes go out; the hook closes the stream.
        // Close is idempotent, so repeat DOs are harmless.
        handler.LogoutRequested = () => ByteStream.Close();
        // A server has no local user: console echo defaults off (opt in via
        // settings for debugging), and the machine must never beep at a peer.
        handler.IsWriteConsole = Settings.IsWriteConsole ?? false;
        handler.EnableBell = false;
        // The server negotiates echoing: an inbound DO ECHO (including the
        // confirmation of our own WILL ECHO) is answered WILL. Agreement is
        // negotiation state only — the read path never replays inbound
        // bytes, so echoing is the application's job (a session that wants
        // remote echo writes the bytes back itself). Gated by OfferEcho so
        // operators can refuse echo entirely; the bounce guard (never both
        // directions) still applies.
        handler.AllowRemoteEcho = Settings.OfferEcho;
        // Master negotiation switch, fed live per read like the flags
        // above: while set, inbound verbs earn no state, no reply, no
        // log (see ByteStreamHandler.SilenceNegotiation).
        handler.SilenceNegotiation = Settings.DisableAllNegotiation;
        // MUD stores are session-lived (handlers are per-read): the
        // append/replace reports come back through the typed hooks.
        // Per-read handler lists are capped from the same options so a
        // pipelined burst cannot spike within one pass.
        try
        {
            handler.MaxMudListItems = Settings.MaxMudListItems;
            handler.MaxMudListBytes = Settings.MaxMudListBytes;
            handler.MaxMudKeys = Settings.MaxMudKeys;
            handler.MaxMudValueChars = Settings.MaxEnvironValueChars;
        }
        catch
        {
        }

        handler.MsspReceived = vars =>
        {
            lock (collectorLock)
            {
                mudMsspData = CapMsspVars(vars);
            }
        };
        handler.MxpReceived = body =>
        {
            lock (collectorLock)
            {
                if (IsMudValueOverCap(body.Length))
                {
                    LogMudCap("mxp", body.Length);
                    return;
                }

                mudMxpData.Add(body);
                TrimMudSessionBytes(mudMxpData, static b => b.Length);
            }
        };
        handler.MspReceived = body =>
        {
            lock (collectorLock)
            {
                if (IsMudValueOverCap(body.Length))
                {
                    LogMudCap("msp", body.Length);
                    return;
                }

                mudMspData.Add(body);
                TrimMudSessionBytes(mudMspData, static b => b.Length);
            }
        };
        handler.ZmpReceived = (command, args) =>
        {
            lock (collectorLock)
            {
                if (!TryCapZmp(command, args))
                {
                    return;
                }

                mudZmpData[command] = args;
            }
        };
        handler.AardwolfReceived = message =>
        {
            lock (collectorLock)
            {
                int size = message.Channel.Length + message.DataBytes.Length;
                if (IsMudValueOverCap(size))
                {
                    LogMudCap("aardwolf", size);
                    return;
                }

                mudAardwolfData.Add(message);
                TrimMudSessionBytes(mudAardwolfData, static m => m.Channel.Length + m.DataBytes.Length);
            }
        };
        handler.AtcpReceived = (package, value) =>
        {
            lock (collectorLock)
            {
                if (IsMudValueOverCap(package.Length + value.Length))
                {
                    LogMudCap("atcp", package.Length + value.Length);
                    return;
                }

                mudAtcpData.Add((package, value));
                TrimMudSessionBytes(mudAtcpData, static t => t.Package.Length + t.Value.Length);
            }
        };
        // Subnegotiation continuation is session-lived (handlers are
        // per-read): a frame split across reads reassembles instead of
        // dropping its first half (telnetlib3 _sb_buffer parity).
        handler.SbResumeState = sbResumeState;
        handler.FramingState = framingState;
        // TerminalType/TerminalSpeed keep their responder defaults:
        // harmless values if a peer ever SENDs to us.
    }
}
