namespace telnet_cs.IO
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text;
    using System.Text.Json.Nodes;
    using System.Threading.Tasks;
    using telnet_cs.Client;
    using telnet_cs.Protocol;
    using telnet_cs.Transport;

    /// <summary>
    /// Provides core functionality for interacting with the ByteStream.
    /// </summary>
    public partial class ByteStreamHandler : IByteStreamHandler
    {
        private const int IacByte = (int)Commands.InterpretAsCommand;
        private const int SeByte = (int)Commands.SubnegotiationEnd;
        private const int MaxSubnegotiationBytes = 1 << 20;

        private readonly IByteStream byteStream;

        /// <summary>
        /// Single-byte lookahead stash for bytes that belong to the subsequent
        /// stream (RFC 859 bare-SE terminator lookahead and RFC 854 CR LF requeue).
        /// </summary>
        private int? pushbackByte;

        /// <summary>
        /// RFC 854 command framing split across reads: a trailing IAC was
        /// consumed with no verb yet. The verb arrives with the continuation.
        /// </summary>
        private bool pendingIac;

        /// <summary>
        /// RFC 854 command framing split across reads: IAC and verb were
        /// consumed with no option byte yet. The option arrives with the continuation.
        /// </summary>
        private int? pendingVerb;

        /// <summary>
        /// A CR was just delivered with the following byte not yet examined.
        /// Raw <c>ReadAsync</c> preserves a following NUL as data (matching the
        /// reference <c>read()</c>); only the terminated/line layer collapses
        /// CR NUL to CR (matching the reference <c>readline()</c>). Persists
        /// across reads so a split CR and NUL still pair up.
        /// </summary>
        private bool sawCrAwaitingNul;

        /// <summary>
        /// Persistent incremental decoder for <see cref="TextEncoding"/>,
        /// replaced whenever the encoding changes. State survives across
        /// reads so split multibyte sequences buffer their lead.
        /// </summary>
        private System.Text.Decoder? textDecoder;

        /// <summary>The encoding <see cref="textDecoder"/> was created for.</summary>
        private System.Text.Encoding? textDecoderEncoding;

        /// <summary>
        /// Subnegotiation continuation stashed when the wire stalls mid-frame:
        /// the option byte plus the payload scanned so far. The next
        /// <c>PerformNegotiation</c> resumes the scan instead of dropping the
        /// frame (telnetlib3 <c>_sb_buffer</c> parity). Round-tripped through
        /// the session on every read, because clients and sessions build one
        /// handler per read.
        /// </summary>
        private int? sbResumeOption;

        private List<byte>? sbResumePayload;
        private bool sbResumeOverCap;

        /// <summary>
        /// Retained for <see cref="SbResumeState"/> shape compatibility; never
        /// set. A bare SE byte is ordinary payload data (uniform IAC handling
        /// like the reference: only IAC SE terminates), so there is no
        /// STATUS-specific lookahead to resume.
        /// </summary>
        private bool sbResumeSePending;

        /// <summary>
        /// An IAC was consumed at the end of an SB scan with no following byte
        /// yet (RFC 854 IAC SE framing split across reads). The next byte
        /// decides: SE terminates, IAC escapes, SB starts a nested frame.
        /// </summary>
        private bool sbResumeIacPending;

        /// <summary>
        /// Gets or sets the stashed subnegotiation continuation for the
        /// session round-trip: fed in before each read, captured after.
        /// </summary>
        internal (int Option, byte[] Payload, bool OverCap, bool SePending, bool IacPending)? SbResumeState
        {
            get => sbResumeOption.HasValue
              ? (sbResumeOption.Value, [.. sbResumePayload!], sbResumeOverCap, sbResumeSePending, sbResumeIacPending)
              : null;
            set
            {
                sbResumeOption = value?.Option;
                sbResumePayload = value is null ? null : [.. value.Value.Payload];
                sbResumeOverCap = value?.OverCap ?? false;
                sbResumeSePending = value?.SePending ?? false;
                sbResumeIacPending = value?.IacPending ?? false;
            }
        }

        internal (bool PendingIac, int? PendingVerb, bool SawCr, int? Pushback) FramingState
        {
            get => (pendingIac, pendingVerb, sawCrAwaitingNul, pushbackByte);
            set
            {
                pendingIac = value.PendingIac;
                pendingVerb = value.PendingVerb;
                sawCrAwaitingNul = value.SawCr;
                pushbackByte = value.Pushback;
            }
        }

        /// <summary>
        /// Gets whether the handler is discarding data in an RFC 854 Synch
        /// scan: set by the urgent-data trigger (or <see cref="EnterSynchDiscard"/>
        /// in tests) and cleared by in-band <c>IAC DM</c>. Extension: the
        /// reference delivers the discarded bytes; this scan drops them.
        /// </summary>
        internal bool InSynchDiscard { get; private set; }

        /// <summary>
        /// Enters RFC 854 Synch discard mode: data is dropped until in-band
        /// <c>IAC DM</c>. The urgent-data trigger calls this automatically on
        /// real TCP streams; tests call it directly for hermetic coverage.
        /// </summary>
        internal void EnterSynchDiscard()
        {
            InSynchDiscard = true;
        }

        /// <summary>
        /// Non-blocking RFC 854 Synch trigger plus scan entry: consumes one
        /// pending TCP urgent byte when the stream is a real
        /// <see cref="TcpByteStream"/> with urgent data waiting (and we are not
        /// already discarding), then runs the discard scan while the mode holds.
        /// Split out of <c>RetrieveAndParseResponse</c> to keep that method
        /// under the complexity gate. Returns null when not discarding.
        /// </summary>
        private async Task<bool?> RetrieveSynchDiscardAsync(StringBuilder sb, List<byte> rawBytes, List<int> opByteCounts, List<byte?> echoBytes)
        {
            if (PollSynchTrigger())
            {
                EnterSynchDiscard();
            }

            return InSynchDiscard
              ? await RetrieveAndParseSynchDiscard(sb, rawBytes, opByteCounts, echoBytes).ConfigureAwait(false)
              : null;
        }

        /// <summary>
        /// Non-blocking RFC 854 Synch trigger: consumes one pending TCP urgent
        /// byte when the stream is a real <see cref="TcpByteStream"/> with
        /// urgent data waiting (and we are not already discarding). Split out
        /// of <c>RetrieveAndParseResponse</c> to keep that method under the
        /// complexity gate.
        /// </summary>
        /// <returns>True when a Synch scan should start.</returns>
        private bool PollSynchTrigger()
        {
            return !InSynchDiscard && byteStream is TcpByteStream tcp && tcp.TryConsumeUrgentSignal() is not null;
        }

        /// <summary>
        /// RFC 854 Synch scan: discard data until <c>IAC DM</c>. Interesting
        /// signals and all other commands dispatch through the normal
        /// <see cref="InterpretNextAsCommand"/> path; EC/EL are swallowed (the
        /// RFC excludes them); escaped IAC is data (dropped). Nothing here
        /// surfaces, so this always returns false; the mode ends only at DM,
        /// so end-of-urgent never cuts the scan short and a later urgent byte
        /// re-triggers a fresh scan.
        /// </summary>
        private async Task<bool> RetrieveAndParseSynchDiscard(StringBuilder sb, List<byte> rawBytes, List<int> opByteCounts, List<byte?> echoBytes)
        {
            var input = ReadNextByte();
            if (input != IacByte)
            {
                // Data (or end-of-stream): dropped, never surfaced.
                return false;
            }

            var verb = TryReadByte();
            if (verb == -1)
            {
                return false;
            }

            if (verb == (int)Commands.DataMark)
            {
                InSynchDiscard = false;
                return false;
            }

            if (verb is (int)Commands.EraseCharacter or (int)Commands.EraseLine)
            {
                return false;
            }

            await InterpretNextAsCommand(sb, rawBytes, opByteCounts, echoBytes, verb).ConfigureAwait(false);
            return false;
        }

        /// <summary>
        /// Gets or sets the persistent RFC 1143 negotiation state. The
        /// <see cref="Client"/> feeds its long-lived instance before each read so
        /// replies survive across reads; a directly-constructed handler uses a
        /// fresh instance (single-read behaviour).
        /// </summary>
        internal NegotiationState Negotiation { get; set; } = new();

        /// <summary>
        /// Gets or sets the terminal type reported during negotiation.
        /// Defaults to <see cref="Client.TerminalType"/>; the client feeds its
        /// effective per-instance setting before each read.
        /// </summary>
        internal string TerminalType { get; set; } = Client.TerminalType;

        /// <summary>
        /// Optional per-SEND terminal-type source for RFC 1091 cycling. The
        /// client feeds a shared cycler before each read; a directly-constructed
        /// handler leaves this null and repeats <see cref="TerminalType"/>.
        /// </summary>
        internal Func<string>? TerminalTypeProvider { get; set; }

        /// <summary>
        /// Gets or sets the terminal speed reported during negotiation.
        /// Defaults to <see cref="Client.TerminalSpeed"/>; the client feeds its
        /// effective per-instance setting before each read.
        /// </summary>
        internal string TerminalSpeed { get; set; } = Client.TerminalSpeed;

        /// <summary>
        /// Gets or sets a value indicating whether text read is echoed to the console.
        /// Defaults to <see cref="Client.IsWriteConsole"/>.
        /// </summary>
        internal bool IsWriteConsole { get; set; } = Client.IsWriteConsole;

        /// <summary>
        /// Gets or sets whether a server <c>DO ECHO</c> may be accepted (RFC 857).
        /// The client feeds its effective per-instance setting before each read.
        /// </summary>
        internal bool AllowRemoteEcho { get; set; }

        /// <summary>
        /// Gets or sets whether agreed echo-back is withheld for the current
        /// read. Negotiation state is untouched (no <c>WONT</c> is sent), so
        /// conforming peers keep hiding local echo; only our own
        /// <c>EchoBackAsync</c> replay is skipped. Fed per read by sessions
        /// that prompt for secrets.
        /// </summary>
        internal bool SuppressEchoBack { get; set; }

        /// <summary>
        /// Whether the peer is currently echoing our input (we sent <c>DO ECHO</c>,
        /// RFC 857): local console echo is suppressed while true.
        /// </summary>
        internal bool PeerEchoing => Negotiation.IsEnabledByPeer((int)Options.Echo);

        /// <summary>
        /// Whether a read is written to the console: requested via
        /// <see cref="IsWriteConsole"/> and not suppressed by peer echo (RFC 857).
        /// </summary>
        internal bool LocalEchoEnabled => IsWriteConsole && !PeerEchoing;

        /// <summary>
        /// Gets or sets a value indicating whether a received BEL rings the console bell.
        /// Retained for compatibility; currently has no effect — BEL bytes are
        /// delivered as data like every other control byte.
        /// Defaults to <c>true</c>; the client feeds its effective per-instance setting before each read.
        /// </summary>
        internal bool EnableBell { get; set; } = true;

        /// <summary>
        /// Gets or sets the encoding used to decode received bytes. When null
        /// (the default), the legacy <see cref="StringBuilder"/> accumulation is
        /// returned verbatim for bit-identical behavior.
        /// </summary>
        internal Encoding? TextEncoding { get; set; }

        /// <summary>
        /// Gets or sets the terminal width reported via NAWS. Zero (the default)
        /// means auto-detect from the console, falling back to 80.
        /// </summary>
        internal int WindowWidth { get; set; }

        /// <summary>
        /// Gets or sets the terminal height reported via NAWS. Zero (the default)
        /// means auto-detect from the console, falling back to 24.
        /// </summary>
        internal int WindowHeight { get; set; }

        /// <summary>
        /// Gets or sets the hook invoked with the effective size each time a
        /// NAWS report is sent. The client feeds a recorder before each read so
        /// <c>RefreshWindowSizeAsync</c> can skip unchanged sizes.
        /// </summary>
        internal Action<ushort, ushort>? NawsSizeSent { get; set; }

        /// <summary>
        /// Gets or sets the server-role subnegotiation consumer. Invoked with
        /// every non-LINEMODE payload before the SEND gate; returning
        /// <c>true</c> consumes it (server sent SEND and this is the IS/INFO
        /// answer, or unsolicited NAWS/ENV-INFO). Returning <c>false</c> (or unset)
        /// falls through to the normal SEND-responder path, so direct-handler
        /// behavior (including stray-IS <c>WONT</c>) is unchanged.
        /// </summary>
        internal Func<int, List<byte>, bool>? SubnegotiationResponse { get; set; }

        /// <summary>
        /// Gets or sets the hook invoked when an unsuppressed Go-Ahead arrives
        /// (RFC 858 §5: GA is a NOP only while Suppress-GA is in effect on the
        /// peer's transmit path). The owning client or session feeds a raiser
        /// before each read; a directly-constructed handler leaves this null and
        /// the signal is dropped.
        /// </summary>
        internal Action? GoAheadReceived { get; set; }

        /// <summary>
        /// Gets or sets the LINEMODE MODE mask and SLC table (RFC 1184),
        /// shared with the owning client or session so negotiation memory
        /// survives across reads. A directly-constructed handler uses a fresh
        /// instance.
        /// </summary>
        internal LinemodeState Linemode { get; set; } = new();

        /// <summary>
        /// Gets or sets whether inbound LINEMODE MODE masks and SLC triplets
        /// use the server rules (<see cref="LinemodeState.ApplyModeAsServer"/>
        /// and <see cref="LinemodeState.ApplySlcAsServer"/>) instead of the
        /// client rules (<see cref="LinemodeState.ApplyMode"/> and
        /// <see cref="LinemodeState.ApplySlc"/>). Set by the server session;
        /// a client-side handler keeps the default client behavior.
        /// </summary>
        internal bool ApplyLinemodeAsServer { get; set; }

        /// <summary>
        /// Gets or sets the per-instance log hook. The client feeds its effective
        /// setting before each read.
        /// </summary>
        internal Action<string>? Log { get; set; }

        /// <summary>
        /// Gets or sets the value reported for the well-known <c>USER</c> variable
        /// in RFC 1408 ENVIRON responses. The client feeds its effective setting
        /// before each read.
        /// </summary>
        internal string? EnvironmentUser { get; set; }

        /// <summary>
        /// Gets or sets the value reported for the well-known <c>DISPLAY</c> variable
        /// in RFC 1408 ENVIRON responses. The client feeds its effective setting
        /// before each read.
        /// </summary>
        internal string? EnvironmentDisplay { get; set; }

        /// <summary>
        /// Gets or sets the user-defined variables reported as <c>USERVAR</c> entries
        /// in RFC 1408 ENVIRON responses. The client feeds its effective setting
        /// before each read.
        /// </summary>
        internal IReadOnlyDictionary<string, string> EnvironmentUserVars { get; set; } =
          new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>
        /// Gets or sets whether 8-bit data bytes are decoded even without an
        /// agreed inbound BINARY direction. The server sets this when the
        /// peer's environment presumes BINARY capability (a <c>CHARSET</c> or
        /// encoding-suffixed <c>LANG</c> entry, RFC 1572; telnetlib3's
        /// force-binary rule).
        /// </summary>
        internal bool ForceBinaryDecoding { get; set; }

        /// <summary>
        /// Gets or sets the X display location reported in RFC 1096
        /// X-DISPLAY-LOCATION IS answers. The client feeds its effective
        /// setting before each read. Null answers SEND with an empty display
        /// string (RFC 1096 answers SEND even when unconfigured).
        /// </summary>
        internal string? XDisplayLocation { get; set; }

        /// <summary>
        /// Gets or sets the hook invoked when <c>DO LOGOUT</c> (RFC 727)
        /// arrives. No negotiation bytes go out (the reference closes the
        /// transport); the hook closes the stream. The session feeders wire
        /// it to their stream before each read; a directly-constructed
        /// handler leaves this null and the signal is dropped.
        /// </summary>
        internal Action? LogoutRequested { get; set; }

        /// <summary>
        /// Gets or sets the hook invoked when <c>IAC EOR</c> (239, RFC 885)
        /// arrives. Like GA, the boundary is surfaced and carries no data.
        /// </summary>
        internal Action? EorReceived { get; set; }

        /// <summary>
        /// Gets or sets the location string sent spontaneously as
        /// <c>IAC SB SNDLOC &lt;location&gt; IAC SE</c> (RFC 779) after
        /// answering <c>DO SNDLOC</c> with <c>WILL</c>. Null (the default)
        /// sends nothing. The client feeds its effective setting before each read.
        /// </summary>
        internal string? SendLocation { get; set; }

        /// <summary>
        /// Gets the last location string received via SNDLOC subnegotiation.
        /// </summary>
        internal string? LastLocation { get; private set; }

        /// <summary>
        /// Gets or sets the hook invoked with each received SNDLOC location.
        /// </summary>
        internal Action<string>? LocationReceived { get; set; }

        /// <summary>
        /// Gets whether remote flow control is currently on (RFC 1372 mode
        /// ON/OFF). Defaults to <c>true</c> per the RFC 1372 initial state.
        /// </summary>
        internal bool LineflowEnabled { get; private set; } = true;

        /// <summary>
        /// Gets whether output restarts only on XON (RFC 1372 RESTART_XON)
        /// rather than on any character (RESTART_ANY). Defaults to
        /// <c>false</c> (RESTART_XON), matching the reference default send.
        /// </summary>
        internal bool LineflowXonAny { get; private set; }

        /// <summary>
        /// Gets or sets the hook invoked with each received LFLOW mode byte.
        /// </summary>
        internal Action<byte>? LineflowReceived { get; set; }

        /// <summary>
        /// Gets or sets the LFLOW restart mode this side sends as a server
        /// (RFC 1372): <c>true</c> sends RESTART_ANY, <c>false</c> (the
        /// default) sends RESTART_XON.
        /// </summary>
        internal bool SendLineflowRestartAny { get; set; }

        /// <summary>
        /// Gets or sets whether this handler sends the LFLOW mode SB after
        /// agreeing to a peer WILL (server role, RFC 1372). Defaults to
        /// <c>false</c>: only a server may send SB LFLOW, so a client-side
        /// handler never volunteers one. Fed by the server session.
        /// </summary>
        internal bool SendLineflowAsServer { get; set; }

        /// <summary>
        /// Gets or sets the character sets offered in CHARSET REQUEST answers
        /// (RFC 2066), in preference order. Defaults to UTF-8.
        /// </summary>
        internal IReadOnlyList<string> CharsetOffers { get; set; } = ["UTF-8"];

        /// <summary>
        /// Gets or sets the selector answering an inbound CHARSET REQUEST: it
        /// receives the peer's offers and returns the selected name, or null
        /// to REJECT. Defaults to null, which applies the reference selection
        /// policy: with no local encoding preference (or a weak Latin-1
        /// default) the first viable peer offer is accepted; with an explicit
        /// preference an exact match inside <see cref="CharsetOffers"/> wins,
        /// else the request is rejected so the local encoding is kept.
        /// This is the offer-vs-send split: <see cref="CharsetOffers"/> builds
        /// our outbound REQUEST, this answers inbound ones.
        /// </summary>
        internal Func<IReadOnlyList<string>, string?>? CharsetSelector { get; set; }

        /// <summary>
        /// Gets or sets whether this handler answers for a server role. A
        /// server answers a simultaneous inbound CHARSET REQUEST (one arriving
        /// while our own REQUEST is outstanding) with REJECTED; a client
        /// answers the peer's REQUEST normally (RFC 2066 §5).
        /// </summary>
        internal bool IsServerRole { get; set; }

        /// <summary>
        /// Gets or sets whether our own CHARSET REQUEST is outstanding (sent,
        /// not yet answered). Guards the single-active-subnegotiation rule
        /// (RFC 2066 §5): no second REQUEST goes out while this is set.
        /// Session owners feed the long-lived value in (handlers are
        /// per-read); answer receipt clears it.
        /// </summary>
        internal bool CharsetRequestPending { get; set; }

        /// <summary>
        /// Gets the character set agreed via CHARSET ACCEPTED, or null when
        /// none was negotiated yet (without an agreement TextEncoding stays
        /// null and received bytes pass through as (char)byte via the legacy
        /// accumulation; no bytes are dropped).
        /// An ACCEPTED name switches <see cref="TextEncoding"/> to the agreed
        /// encoding and latches <see cref="ForceBinaryDecoding"/>.
        /// </summary>
        internal string? NegotiatedCharset { get; private set; }

        /// <summary>
        /// Gets or sets the hook invoked with the accepted character-set name.
        /// </summary>
        internal Action<string>? CharsetAccepted { get; set; }

        /// <summary>
        /// Gets or sets the hook invoked when a CHARSET offer is rejected.
        /// </summary>
        internal Action? CharsetRejected { get; set; }

        /// <summary>
        /// Gets or sets the hook invoked with the codec name when a
        /// SyncTERM font-selection sequence switches the read encoding.
        /// Fires only for automatic switches (explicit
        /// <see cref="TextEncoding"/> is never overridden).
        /// </summary>
        internal Action<string>? SyncTermFontDetected { get; set; }

        /// <summary>
        /// Gets or sets whether MCCP2/MCCP3 compression (options 86/87) may be
        /// agreed. Defaults to <c>false</c>: like the reference, compression
        /// is refused unless opted in, and must stay off over TLS (CRIME/BREACH).
        /// </summary>
        internal bool EnableMccp { get; set; }

        /// <summary>
        /// Gets or sets whether this direction runs over TLS. MCCP is refused
        /// while set (CRIME/BREACH), matching the reference negotiation gate.
        /// Fed per read: the server copies its accepted-socket flag, the
        /// client its <c>UseTls</c> setting.
        /// </summary>
        internal bool IsTlsActive { get; set; }

        /// <summary>
        /// Gets whether an MCCP2 (server-to-client) compressed stream was
        /// started via an empty <c>IAC SB MCCP2 IAC SE</c>. While set, inbound
        /// bytes inflate through <see cref="MccpStream"/> until its Z_FINISH;
        /// the stream then ends and the wire resumes plaintext.
        /// </summary>
        internal bool Mccp2Active { get; set; }

        /// <summary>
        /// Gets whether an MCCP3 (client-to-server) compressed stream was
        /// started via an empty <c>IAC SB MCCP3 IAC SE</c>. See
        /// <see cref="Mccp2Active"/> for the inflation scope.
        /// </summary>
        internal bool Mccp3Active { get; set; }

        /// <summary>
        /// Gets or sets the session-owned MCCP decompressor. Created on the
        /// first arming SB and shared across this handler's per-read lifetime
        /// via <see cref="MccpStateChanged"/>; never disposed here (the
        /// session owns it). Null until compression starts.
        /// </summary>
        internal MccpDecompressor? MccpStream { get; set; }

        /// <summary>
        /// Gets or sets the hook the handler fires with
        /// <c>(mccp2Active, mccp3Active, stream)</c> whenever MCCP agreement
        /// changes (arming SB, clean stream end, corrupt shutdown) so the
        /// session can persist the state across per-read handlers.
        /// </summary>
        internal Action<bool, bool, MccpDecompressor?>? MccpStateChanged { get; set; }

        private bool mccpShutdownPending;

        private int mccpShutdownOption = (int)Options.Mccp2;

        /// <summary>
        /// Gets the first raw wire byte seen on this handler, or -1 when
        /// nothing arrived yet. The server checks it for a TLS ClientHello
        /// lead byte (0x16) on plaintext listeners.
        /// </summary>
        internal int FirstInboundByte => firstInboundByte;

        private int firstInboundByte = -1;

        /// <summary>
        /// Gets the count of raw wire bytes pulled from the stream by this
        /// handler. Every <c>ReadByte</c> call site reports here, so
        /// negotiation frames and MCCP-compressed bytes count; inflated
        /// MCCP output and pushback re-reads do not.
        /// </summary>
        internal long InboundWireBytes { get; private set; }

        /// <summary>
        /// Gets the count of raw wire bytes this handler wrote to the
        /// stream (replies, echo-back, subnegotiation answers).
        /// </summary>
        internal long OutboundWireBytes { get; private set; }

        private void NoteInboundByte(int raw)
        {
            if (raw == -1)
            {
                return;
            }

            InboundWireBytes++;
            if (firstInboundByte == -1)
            {
                firstInboundByte = raw;
            }
        }

        /// <summary>
        /// Writes <paramref name="count"/> bytes to the peer and accounts
        /// them as outbound wire bytes. The single choke point for every
        /// handler reply, so session counters match the reference
        /// <c>len(buf)</c> transmit accounting.
        /// </summary>
        private async Task WriteWireAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            await byteStream.WriteAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
            OutboundWireBytes += count;
        }

        /// <summary>
        /// Gets or sets the hook invoked when the peer starts MCCP2.
        /// </summary>
        internal Action? Mccp2StartReceived { get; set; }

        /// <summary>
        /// Gets or sets the hook invoked when the peer starts MCCP3.
        /// </summary>
        internal Action? Mccp3StartReceived { get; set; }

        /// <summary>
        /// Gets or sets whether the MUD options (MSDP 69, MSSP 70, MSP 90,
        /// MXP 91, ZMP 93, Aardwolf 102, ATCP 200, GMCP 201) may be agreed.
        /// Defaults to <c>true</c>. The client stack overrides this from its
        /// options (declined by default); the server stack leaves the default
        /// in place and agrees. Subnegotiations dispatch to the typed
        /// hooks and stores (plus the raw <see cref="MudSubnegotiationReceived"/>
        /// hook); text decoding uses the agreed CHARSET when one resolved.
        /// </summary>
        internal bool EnableMudOptions { get; set; } = true;

        /// <summary>
        /// Gets or sets whether COM port control (option 44, RFC 2217 framing
        /// level) may be agreed. Defaults to <c>true</c>. Payloads surface
        /// through <see cref="ComPortReceived"/>; modem-line semantics stay
        /// the caller's.
        /// </summary>
        internal bool EnableComPort { get; set; } = true;

        /// <summary>
        /// Gets or sets the hook invoked with MUD subnegotiation payloads as
        /// <c>(option, payload)</c>, the payload without the option byte.
        /// </summary>
        internal Action<int, byte[]>? MudSubnegotiationReceived { get; set; }

        /// <summary>
        /// Gets or sets the hook invoked with a decoded GMCP message as
        /// <c>(package, data)</c>; a blank or package-only body decodes to a
        /// null payload.
        /// </summary>
        internal Action<string, JsonNode?>? GmcpReceived { get; set; }

        /// <summary>
        /// Gets or sets the hook invoked with decoded MSDP variables.
        /// </summary>
        internal Action<IReadOnlyDictionary<string, object?>>? MsdpReceived { get; set; }

        /// <summary>
        /// Gets or sets the hook invoked with decoded MSSP variables; the
        /// same mapping is stored on <see cref="MsspData"/> (replaced wholesale,
        /// like the reference).
        /// </summary>
        internal Action<IReadOnlyDictionary<string, object>>? MsspReceived { get; set; }

        /// <summary>
        /// Gets or sets the hook invoked with a raw MSP payload; every
        /// payload is also appended to <see cref="MspData"/>.
        /// </summary>
        internal Action<byte[]>? MspReceived { get; set; }

        /// <summary>
        /// Gets or sets the hook invoked with each raw MXP payload; every
        /// payload is also appended to <see cref="MxpData"/>.
        /// </summary>
        internal Action<byte[]>? MxpReceived { get; set; }

        /// <summary>
        /// Gets or sets the hook invoked with a decoded ZMP message as
        /// <c>(command, args)</c>; the command slot of <see cref="ZmpData"/>
        /// is replaced. Empty bodies store nothing and fire nothing.
        /// </summary>
        internal Action<string, IReadOnlyList<string>>? ZmpReceived { get; set; }

        /// <summary>
        /// Gets or sets the hook invoked with each decoded Aardwolf message;
        /// every message is also appended to <see cref="AardwolfData"/>.
        /// </summary>
        internal Action<AardwolfMessage>? AardwolfReceived { get; set; }

        /// <summary>
        /// Gets or sets the hook invoked with each decoded ATCP message as
        /// <c>(package, value)</c>; every pair is also appended to
        /// <see cref="AtcpData"/>.
        /// </summary>
        internal Action<string, string>? AtcpReceived { get; set; }

        /// <summary>
        /// Gets the last MSSP variables received (replaced by each MSSP
        /// subnegotiation), or null when none has arrived yet.
        /// </summary>
        internal IReadOnlyDictionary<string, object>? MsspData { get; private set; }

        /// <summary>
        /// Gets the raw MSP payloads, in arrival order.
        /// </summary>
        internal List<byte[]> MspData { get; } = [];

        /// <summary>
        /// Gets the accumulated raw MXP payloads, in arrival order.
        /// </summary>
        internal List<byte[]> MxpData { get; } = [];

        /// <summary>
        /// Gets the ZMP arguments by command (each command slot holds its
        /// latest message).
        /// </summary>
        internal Dictionary<string, IReadOnlyList<string>> ZmpData { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// Gets the decoded Aardwolf messages, in arrival order.
        /// </summary>
        internal List<AardwolfMessage> AardwolfData { get; } = [];

        /// <summary>
        /// Gets the decoded ATCP <c>(package, value)</c> pairs, in arrival
        /// order.
        /// </summary>
        internal List<(string Package, string Value)> AtcpData { get; } = [];

        /// <summary>
        /// Gets or sets the hook invoked with COM port control payloads
        /// (without the option byte).
        /// </summary>
        internal Action<byte[]>? ComPortReceived { get; set; }

        /// <summary>
        /// Gets or sets the process-wide log hook. Falls back to
        /// <see cref="System.Diagnostics.Debug"/> when unset.
        /// </summary>
        internal static Action<string>? Trace
        {
            get => _traceFlow.CurrentOr(_traceDefault);
            set => _traceDefault = value;
        }

        internal static FlowLocal<Action<string>?> TraceOverride => _traceFlow;

        private static Action<string>? _traceDefault;

        private static readonly FlowLocal<Action<string>?> _traceFlow = new();

        /// <summary>
        /// Idle delay between read polls when no data is available.
        /// </summary>
        internal int MillisecondReadDelay { get; set; } = 16;

        private bool IsResponsePending
        {
            get
            {
                return pushbackByte.HasValue || MccpHasOutput || byteStream.Available > 0;
            }
        }

        /// <summary>
        /// Gets whether the MCCP decompressor holds output (or post-stream
        /// plaintext) the wire no longer reports via
        /// <see cref="IByteStream.Available"/>.
        /// </summary>
        private bool MccpHasOutput
        {
            get
            {
                return MccpStream is not null && (MccpStream.HasOutput || MccpStream.HasTrailing);
            }
        }

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        /// <param name="disposing"><c>true</c> to release both managed and unmanaged resources; <c>false</c> to release only unmanaged resources.</param>
        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                // The handler never owns the byte stream (the client creates one
                // handler per read over its own long-lived stream), so it must not
                // dispose it — doing so forced Client to leak the handler (CA2000).
                // It must also not cancel the token source unless it created it:
                // Client passes its own InternalCancellation, which must survive
                // the read.
                if (isCancellationTokenOwned)
                {
                    internalCancellation.Dispose();
                }
            }
        }

        private static DateTime ExtendRollingTimeout(TimeSpan timeout)
        {
            // Re-arm the incremental window to 1% of the full timeout.
            return DateTime.UtcNow.AddTicks(timeout.Ticks / 100);
        }

        private static bool IsWaitForInitialResponse(DateTime endInitialTimeout, bool isInitialResponseReceived)
        {
            return !isInitialResponseReceived && DateTime.UtcNow < endInitialTimeout;
        }

        private static bool IsTimeoutExpired(DateTime timeout)
        {
            return DateTime.UtcNow >= timeout;
        }

        private static bool IsInitialResponseReceived(StringBuilder sb)
        {
            return sb.Length > 0;
        }

        private void WriteLog(string message)
        {
            if (Log != null)
            {
                Log(message);
            }

            if (Trace != null)
            {
                Trace(message);
            }

            System.Diagnostics.Debug.WriteLine(message);
        }

        /// <summary>
        /// Reads the next byte, honouring the single-byte pushback stash.
        /// I/O failures (<see cref="System.IO.IOException"/>, including read
        /// timeouts) and over-reads surface as -1; anything else the stream
        /// throws (notably <see cref="System.Net.Sockets.SocketException"/>)
        /// propagates to the caller.
        /// </summary>
        private int ReadNextByte()
        {
            if (pushbackByte.HasValue)
            {
                var pending = pushbackByte.Value;
                pushbackByte = null;
                return pending;
            }

            return TryReadByte();
        }

        /// <summary>
        /// Blind continuation read: never polls <see cref="IByteStream.Available"/>
        /// (fakes and real sockets alike may report 0 mid-sequence), mapping I/O
        /// and over-read failures to -1. While an MCCP stream is armed, serves
        /// decompressed output (feeding whatever the wire reports available);
        /// after a clean stream end serves the queued post-stream plaintext.
        /// </summary>
        private int TryReadByte()
        {
            var mccp = MccpStream;
            if (mccp is not null)
            {
                if (mccp.TryTakeReady(out var inflated))
                {
                    return inflated;
                }

                if (Mccp2Active || Mccp3Active)
                {
                    DrainMccp(mccp);
                    if (mccp.Failed)
                    {
                        ShutdownMccpCorrupt();
                    }
                    else
                    {
                        if (mccp.StreamEnded)
                        {
                            FinishMccpStream();
                        }

                        if (mccp.TryTakeReady(out inflated))
                        {
                            return inflated;
                        }
                    }
                }
                else if (mccp.TryTakeTrailing(out var resumed))
                {
                    return resumed;
                }
            }

            try
            {
                int raw = byteStream.ReadByte();
                NoteInboundByte(raw);
                return raw;
            }
            catch (System.IO.IOException)
            {
                return -1;
            }
            catch (InvalidOperationException)
            {
                return -1;
            }
        }

        /// <summary>
        /// Feeds the MCCP decompressor from whatever the wire reports
        /// available (never blocking: a stall simply waits for the next
        /// read; the decompressor's footer probe tells stall from Z_FINISH).
        /// </summary>
        /// <param name="mccp">The session-owned decompressor.</param>
        private void DrainMccp(MccpDecompressor mccp)
        {
            while (!mccp.HasOutput && !mccp.StreamEnded && !mccp.Failed && byteStream.Available > 0)
            {
                int raw;
                try
                {
                    raw = byteStream.ReadByte();
                }
                catch (System.IO.IOException)
                {
                    return;
                }
                catch (InvalidOperationException)
                {
                    return;
                }

                if (raw == -1)
                {
                    return;
                }

                NoteInboundByte(raw);
                mccp.Feed((byte)raw);
            }
        }

        /// <summary>
        /// Ends MCCP agreement after a clean Z_FINISH: the flags drop so
        /// later bytes read raw again, while the queued post-stream
        /// plaintext keeps serving from the session-owned stream.
        /// </summary>
        private void FinishMccpStream()
        {
            WriteLog("MCCP stream ended; resuming plaintext.");
            Mccp2Active = false;
            Mccp3Active = false;
            MccpStateChanged?.Invoke(false, false, MccpStream);
        }

        /// <summary>
        /// Ends MCCP agreement after corrupt data: queued output is already
        /// dropped by the decompressor (the reader is fed nothing), the
        /// stream reference is released, and a refusal goes out at the next
        /// async flush point. The refusal is this stack's extension (the
        /// reference only clears state and logs): WONT for MCCP3 (withdrawing
        /// our offer), DONT otherwise (refusing theirs).
        /// </summary>
        private void ShutdownMccpCorrupt()
        {
            WriteLog("MCCP decompression failed; answering DONT and resuming plaintext.");
            Mccp2Active = false;
            Mccp3Active = false;
            MccpStream = null;
            MccpStateChanged?.Invoke(false, false, null);
            mccpShutdownPending = true;
        }

        /// <summary>
        /// Separate TELNET commands from text. Handle non-printable characters.
        /// </summary>
        /// <param name="sb">The incoming message.</param>
        /// <param name="rawBytes">The raw data bytes backing <paramref name="sb"/> (used when <see cref="TextEncoding"/> is set).</param>
        /// <param name="opByteCounts">Parallel to <paramref name="sb"/>: bytes of <paramref name="rawBytes"/> per appended char.</param>
        /// <param name="echoBytes">Parallel to <paramref name="sb"/>: the original data byte to echo per char, or null for command markers (never echoed) and rendering continuations.</param>
        /// <returns>True if response is pending.</returns>
        private async Task<bool> RetrieveAndParseResponse(StringBuilder sb, List<byte> rawBytes, List<int> opByteCounts, List<byte?> echoBytes)
        {
            // A corrupt MCCP stream arms its refusal from the sync byte path;
            // flush it at the async points around this pass (extension: the
            // reference only clears state and logs on corrupt data).
            await FlushMccpShutdownAsync().ConfigureAwait(false);

            // RFC 854 Synch trigger: a pending urgent byte enters discard mode.
            // Poll-gated and TCP-only, so fakes and pipes never see it; the urgent
            // byte itself is consumed by the probe, and in-band IAC DM (below)
            // ends the mode.
            var synch = await RetrieveSynchDiscardAsync(sb, rawBytes, opByteCounts, echoBytes).ConfigureAwait(false);
            if (synch.HasValue)
            {
                return synch.Value;
            }

            if (sawCrAwaitingNul && (pushbackByte.HasValue || MccpHasOutput || byteStream.Available > 0))
            {
                var followingCr = ReadNextByte();
                if (followingCr == -1)
                {
                    return false;
                }

                sawCrAwaitingNul = false;
                if (followingCr == 0)
                {
                    AppendRecorded(sb, rawBytes, opByteCounts, echoBytes, "\0", 0);
                    await FlushMccpShutdownAsync().ConfigureAwait(false);
                    return true;
                }

                pushbackByte = followingCr;
                await FlushMccpShutdownAsync().ConfigureAwait(false);
                return true;
            }

            if (!pushbackByte.HasValue && (pendingIac || pendingVerb.HasValue) && (MccpHasOutput || byteStream.Available > 0))
            {
                // RFC 854 command split across reads completes here.
                if (await ResumePendingCommandAsync(sb, rawBytes, opByteCounts, echoBytes).ConfigureAwait(false))
                {
                    return true;
                }

                return false;
            }

            if (!pushbackByte.HasValue && sbResumeOption.HasValue && (MccpHasOutput || byteStream.Available > 0))
            {
                // A subnegotiation stalled on an earlier read resumes here:
                // the newly arrived bytes continue its frame (telnetlib3
                // _sb_buffer parity) instead of being parsed as fresh input.
                await PerformNegotiation().ConfigureAwait(false);
                return true;
            }

            if (IsResponsePending)
            {
                var input = ReadNextByte();
                switch (input)
                {
                    case -1:
                        break;
                    case IacByte:
                        var inputVerb = TryReadByte();
                        if (inputVerb == -1)
                        {
                            // RFC 854 framing split: the verb arrives with the continuation.
                            pendingIac = true;
                        }
                        else if (inputVerb == IacByte)
                        {
                            // Escaped literal data byte 255: one char + one raw byte,
                            // not the decimal string "255".
                            AppendRecorded(sb, rawBytes, opByteCounts, echoBytes, (char)IacByte);
                        }
                        else
                        {
                            await InterpretNextAsCommand(sb, rawBytes, opByteCounts, echoBytes, inputVerb).ConfigureAwait(false);
                        }

                        break;
                    case 1 or 2 or 3 or 4 or 5 or 6 or 7 or 8 or 11 or 12 or 21 or 31:
                        // NVT control bytes are data, not commands: forward them
                        // verbatim so the byte stream round-trips exactly. No
                        // expansions ("^C", "NAK: ..." text), no drops (BEL,
                        // ACK), no wire side effects (ENQ must not emit an
                        // unsolicited ACK), and no destructive editing (BS must
                        // not delete already-delivered bytes) — any terminal
                        // presentation belongs in a layer above this parser.
                        // CR keeps its RFC 854 handling in the next case.
                        AppendRecorded(sb, rawBytes, opByteCounts, echoBytes, (char)input);
                        break;
                    case 13: // Carriage Return: CR is delivered now; a following
                        // NUL is preserved as data by the raw path (the line
                        // layer collapses CR NUL to CR). CR LF stays CR LF.
                        AppendRecorded(sb, rawBytes, opByteCounts, echoBytes, "\r", 13);
                        sawCrAwaitingNul = true;
                        break;
                    default:
                        AppendRecorded(sb, rawBytes, opByteCounts, echoBytes, (char)input);
                        break;
                }

                await FlushMccpShutdownAsync().ConfigureAwait(false);
                return true;
            }

            return false;
        }

        private async Task FlushMccpShutdownAsync()
        {
            if (mccpShutdownPending)
            {
                mccpShutdownPending = false;
                if (mccpShutdownOption == (int)Options.Mccp3)
                {
                    await SendWont(mccpShutdownOption).ConfigureAwait(false);
                }
                else
                {
                    await SendDont(mccpShutdownOption).ConfigureAwait(false);
                }
            }
        }

        private static void AppendRecorded(StringBuilder sb, List<byte> rawBytes, List<int> opByteCounts, List<byte?> echoBytes, string text, byte? echoByte = null)
        {
            sb.Append(text);
            rawBytes.AddRange(Encoding.ASCII.GetBytes(text));
            // Char-aligned: ASCII yields exactly one byte per char, so each sb
            // char maps to exactly one count entry (the documented invariant).
            // Only the first char carries the original data byte for echo;
            // command markers pass none and are never echoed back.
            for (var i = 0; i < text.Length; i++)
            {
                opByteCounts.Add(1);
                echoBytes.Add(i == 0 ? echoByte : null);
            }
        }

        private static void AppendRecorded(StringBuilder sb, List<byte> rawBytes, List<int> opByteCounts, List<byte?> echoBytes, char c)
        {
            sb.Append(c);
            // Data bytes are Latin-1 by definition here: the default (null
            // encoding) path returns sb.ToString() verbatim, so the recorded byte
            // only matters for explicit TextEncoding decoding.
            rawBytes.Add((byte)c);
            opByteCounts.Add(1);
            // The char overload is only used for genuine data (escaped IAC and
            // the default data case), so the byte always echoes.
            echoBytes.Add((byte)c);
        }

        /// <summary>
        /// We received a TELNET command. Handle it. Commands are consumed
        /// without touching the accumulation buffer: the decoded text and the
        /// raw bytes backing it are the application's byte record, and only
        /// data bytes may append to them.
        /// </summary>
        /// <param name="sb">The incoming message.</param>
        /// <param name="rawBytes">The raw data bytes backing <paramref name="sb"/> (used when <see cref="TextEncoding"/> is set).</param>
        /// <param name="opByteCounts">Parallel to <paramref name="sb"/>: bytes of <paramref name="rawBytes"/> per appended char.</param>
        /// <param name="echoBytes">Parallel to <paramref name="sb"/>: the original data byte to echo per char, or null for command markers (never echoed) and rendering continuations.</param>
        /// <param name="inputVerb">The command we received.</param>
        private async Task InterpretNextAsCommand(StringBuilder sb, List<byte> rawBytes, List<int> opByteCounts, List<byte?> echoBytes, int inputVerb)
        {
            WriteLog(Enum.GetName(typeof(Commands), inputVerb) ?? inputVerb.ToString());
            switch (inputVerb)
            {
                case (int)Commands.InterruptProcess:
                    WriteLog("Interrupt Process (IP) received.");
                    CancelPendingReads();
                    return;
                case (int)Commands.AreYouThere:
                    // Consumed without reply: answering with printable bytes
                    // would inject peer-visible data that no framing accounts
                    // for, corrupting strict request/response exchanges.
                    WriteLog("Are You There (AYT) received.");
                    return;
                case (int)Commands.AbortOutput:
                    // This design has no output queue (writes go straight to the
                    // stream), so there is nothing to discard: consume and log.
                    WriteLog("Abort Output (AO) received; no queued output to discard.");
                    return;
                case (int)Commands.EraseCharacter:
                    // Consumed without editing: the delivery buffer is the
                    // application's byte record, not a terminal line — erasing
                    // from it would destroy already-delivered data and corrupt
                    // raw-byte counts and echo accounting.
                    WriteLog("Erase Character (EC) received.");
                    return;
                case (int)Commands.EraseLine:
                    // Same as EC: consumed, buffer untouched.
                    WriteLog("Erase Line (EL) received.");
                    return;
                case (int)Commands.Break:
                    // Out-of-band signal: consumed and logged, never surfaced
                    // as text — marker strings would be phantom data to any
                    // caller matching terminators or counting bytes.
                    WriteLog("Break (BRK) received.");
                    return;
                case (int)Commands.EndOfFile:
                    // Same as BRK: consumed, never text.
                    WriteLog("End of file (EOF) received.");
                    return;
                case (int)Commands.Suspend:
                    // Same as BRK: consumed, never text.
                    WriteLog("Suspend (SUSP) received.");
                    return;
                case (int)Commands.Abort:
                    // Same as BRK: consumed, never text.
                    WriteLog("Abort (ABORT) received.");
                    return;
                case (int)Commands.EndOfRecord:
                    // RFC 885: IAC EOR marks a prompt boundary with no
                    // subnegotiation. Surfaced through the hook like GA, but
                    // only when EOR is in effect on the peer's transmit path
                    // (peer's WILL + our DO); otherwise it is a NOP and the
                    // boundary never enters the data stream either way.
                    if (!Negotiation.IsEnabledByPeer((int)Options.EndOfRecord))
                    {
                        WriteLog("EOR received without agreement; treating as NOP.");
                        return;
                    }

                    WriteLog("End of record (EOR) received.");
                    EorReceived?.Invoke();
                    return;
                case (int)Commands.SubnegotiationEnd:
                    // A bare IAC SE with no open SB block is delivered as data
                    // byte 0xF0 instead of being consumed. The RFCs give SE no
                    // standalone meaning (it only terminates an SB block), so
                    // either treatment is standards-compliant; delivering keeps
                    // the never-drop-bytes invariant (unparseable framed bytes
                    // fall through to the reader, exactly like the IAC IAC
                    // escape path, which likewise bypasses the 8-bit gate)
                    // and matches telnetlib3's parser.
                    WriteLog("Stray SE outside subnegotiation; delivering 0xF0 as data.");
                    AppendRecorded(sb, rawBytes, opByteCounts, echoBytes, (char)Commands.SubnegotiationEnd);
                    return;
                case (int)Commands.NoOperation:
                case (int)Commands.DataMark:
                case (int)Options.TimingMark:
                    // Stray NOP, and DM in normal mode (RFC 854: DM is a NOP
                    // outside Synch processing), carry no data: consume silently.
                    // Byte 6 (TM) rides along: it is not a defined RFC 854
                    // command, but telnetlib3 registers a NOP callback for it,
                    // so it must not fall into the data default below.
                    return;
                case (int)Commands.GoAhead:
                    // RFC 858 §5: GA is a NOP only while Suppress-GA is in effect on
                    // the peer's transmit path; otherwise it is the NVT turn-taking
                    // signal, surfaced through the GoAheadReceived hook.
                    if (Negotiation.IsEnabledByPeer((int)Options.SuppressGoAhead))
                    {
                        return;
                    }

                    WriteLog("Go Ahead (GA) received; Suppress-GA not in effect.");
                    GoAheadReceived?.Invoke();
                    return;
                case (int)Commands.Dont:
                case (int)Commands.Wont:
                case (int)Commands.Do:
                case (int)Commands.Will:
                    // All four negotiation verbs flow through ReplyToCommand, which
                    // consults the persistent RFC 1143 state: the option byte always
                    // belongs to the command (never leaks into data), and refusals or
                    // repeats are answered only when the state machine says so.
                    await ReplyToCommand(inputVerb).ConfigureAwait(false);
                    return;
                case (int)Commands.Subnegotiation:
                    await PerformNegotiation().ConfigureAwait(false);
                    return;
                default:
                    // IAC followed by a byte with no defined TELNET command
                    // meaning and no registered callback is delivered as
                    // in-band data instead of being consumed. Every command
                    // this parser handles (including TM, which telnetlib3
                    // answers with a NOP callback) has an explicit case above,
                    // so anything reaching here is one of telnetlib3's "not a
                    // legal 2-byte cmd" bytes, which its parser feeds through
                    // as data (never-drop-bytes); like the IAC IAC escape this
                    // bypasses the 8-bit gate because the peer framed the byte
                    // explicitly.
                    WriteLog($"Illegal 2-byte IAC {inputVerb}; delivering as data.");
                    AppendRecorded(sb, rawBytes, opByteCounts, echoBytes, (char)inputVerb);
                    return;
            }
        }

        /// <summary>
        /// We received a request to perform sub negotiation on a TELNET option.
        /// The terminal type, speed, and window size are taken from the settable
        /// properties on this handler (fed per read from the client's settings).
        /// </summary>
        private async Task PerformNegotiation()
        {
            int inputOption;
            List<byte> payload;
            bool overCap;
            bool iacPending;
            if (sbResumeOption.HasValue)
            {
                // Resuming a subnegotiation stalled mid-scan on an earlier
                // read: the continuation bytes belong to this frame.
                inputOption = sbResumeOption.Value;
                payload = sbResumePayload!;
                overCap = sbResumeOverCap;
                iacPending = sbResumeIacPending;
                sbResumeOption = null;
                sbResumePayload = null;
                sbResumeOverCap = false;
                sbResumeSePending = false;
                sbResumeIacPending = false;
            }
            else
            {
                var option = TryReadByte();
                if (option == -1)
                {
                    // RFC 854 framing split: the option byte arrives with the continuation.
                    pendingVerb = (int)Commands.Subnegotiation;
                    return;
                }

                if (option == IacByte)
                {
                    var following = TryReadByte();
                    if (following == SeByte)
                    {
                        WriteLog("Discarding empty subnegotiation without option byte.");
                        return;
                    }

                    if (following != -1)
                    {
                        pushbackByte = following;
                    }

                    return;
                }

                inputOption = option;
                payload = [];
                overCap = false;
                iacPending = false;
            }

            await ScanAndDispatchSbAsync(inputOption, payload, overCap, iacPending).ConfigureAwait(false);
        }

        private async Task ScanAndDispatchSbAsync(int inputOption, List<byte> payload, bool overCap, bool iacPending)
        {
            // Scan to IAC SE. The payload is capped: over-long input keeps being
            // consumed (so the stream resynchronises) but is then ignored.
            // Framing is uniform for every option (reference parity): only
            // IAC SE terminates; a bare SE byte is ordinary payload data.
            // A stall mid-scan stashes the frame; the next read resumes it.
            bool scanDone = false;

            if (!scanDone && iacPending)
            {
                // RFC 854 IAC SE split: the IAC was consumed, its following byte arrives now.
                var followingSplit = TryReadByte();
                if (followingSplit == -1)
                {
                    StashSbResume(inputOption, payload, overCap, seAwait: false, iacAwait: true);
                    return;
                }

                if (followingSplit == SeByte)
                {
                    scanDone = true;
                }
                else if (followingSplit == IacByte)
                {
                    AddPayloadByte(IacByte);
                }
                else if (followingSplit == (int)Commands.Subnegotiation)
                {
                    // RFC 854 defines no nesting, so a second IAC SB inside an
                    // open frame cannot be a nested frame. Recovery: drop the
                    // outer frame and scan the inner one fresh, which is what
                    // the reference implementation does (it warns, clears its
                    // SB buffer, and re-buffers starting from the inner SB, so
                    // the inner frame is still terminated at its own IAC SE
                    // and dispatched normally — an unknown inner option is then
                    // ignored without reply, a known one handled as usual).
                    // PerformNegotiation below does exactly that: it reads a
                    // fresh option byte and starts a fresh scan.
                    // Do NOT "fix" this by returning early instead: the inner
                    // frame's remaining bytes (option, payload, IAC SE) would
                    // stay in the stream and be delivered as application data
                    // on the next read, which neither this stack nor the
                    // reference does — both consume through the inner IAC SE.
                    // Any reply-vs-silence difference for the inner frame comes
                    // from the payload dispatch rules (SEND vs stray payload),
                    // not from this recovery path.
                    await PerformNegotiation().ConfigureAwait(false);
                    return;
                }
                else
                {
                    // IAC followed by anything else: framing is lost, give up.
                    return;
                }
            }

            if (!scanDone)
            {
                while (true)
                {
                    var b = TryReadByte();
                    if (b == -1)
                    {
                        StashSbResume(inputOption, payload, overCap, seAwait: false, iacAwait: false);
                        return;
                    }

                    if (b == IacByte)
                    {
                        var following = TryReadByte();
                        if (following == -1)
                        {
                            StashSbResume(inputOption, payload, overCap, seAwait: false, iacAwait: true);
                            return;
                        }

                        if (following == SeByte)
                        {
                            break;
                        }

                        if (following == IacByte)
                        {
                            // Escaped literal IAC inside the payload.
                            AddPayloadByte(IacByte);
                            continue;
                        }

                        if (following == (int)Commands.Subnegotiation)
                        {
                            // Same recovery as the split path above: the outer
                            // frame is dropped and the inner one is scanned
                            // fresh (see the detailed note there). Returning
                            // early here would leak the inner frame's tail
                            // into the data stream — never do that.
                            await PerformNegotiation().ConfigureAwait(false);
                            return;
                        }

                        // IAC followed by anything else: framing is lost, give up.
                        return;
                    }

                    AddPayloadByte((byte)b);
                }
            }

            void AddPayloadByte(byte value)
            {
                if (overCap)
                {
                    return;
                }

                if (payload.Count < MaxSubnegotiationBytes)
                {
                    payload.Add(value);
                }
                else
                {
                    overCap = true;
                }
            }

            void StashSbResume(int option, List<byte> body, bool capped, bool seAwait, bool iacAwait)
            {
                sbResumeOption = option;
                sbResumePayload = body;
                sbResumeOverCap = capped;
                sbResumeSePending = seAwait;
                sbResumeIacPending = iacAwait;
            }

            if (overCap)
            {
                return;
            }

            if (payload.Count == 0)
            {
                // MCCP2/MCCP3 start on an empty SB, and several MUD options
                // allow one; anything else empty is dropped (unchanged).
                if (IsEmptySbAllowed(inputOption))
                {
                    ReplyEmptySb(inputOption);
                }

                return;
            }

            if (SubnegotiationResponse?.Invoke(inputOption, payload) is true)
            {
                // Server-role consumer (TTYPE/TSPEED/ENVIRON IS or INFO, inbound
                // NAWS, LINEMODE import requests): collected or answered by the
                // session, nothing further to do. Null for clients and
                // directly-constructed handlers, so their path is unchanged.
                return;
            }

            if (inputOption == (int)Options.LineMode)
            {
                // LINEMODE payloads start with their own subcommand (MODE /
                // FORWARDMASK / SLC), not SEND, so they bypass the SEND gate below.
                // In particular a server DO FORWARDMASK ([253, 2, …]) must be
                // refused in-band, never mistaken for a stray SEND.
                await ReplyLinemodeAsync(payload).ConfigureAwait(false);
                return;
            }

            if (inputOption == (int)Options.SendLocation)
            {
                // RFC 779: the SB carries the raw ASCII location with no
                // SEND/IS discrimination.
                ReplySendLocation(payload);
                return;
            }

            if (inputOption == (int)Options.RemoteFlowControl)
            {
                // RFC 1372: the SB carries a single mode byte (0-3), no verbs.
                await ReplyLineflowAsync(payload).ConfigureAwait(false);
                return;
            }

            if (inputOption == (int)Options.COMPortControl)
            {
                // RFC 2217 framing level: surface the raw payload; modem-line
                // semantics stay the caller's.
                ComPortReceived?.Invoke([.. payload]);
                return;
            }

            if (IsMudOption(inputOption))
            {
                DispatchMud(inputOption, [.. payload]);
                return;
            }

            if (inputOption == (int)Options.Mccp2 || inputOption == (int)Options.Mccp3)
            {
                // Padding-carrying SBs start compression under the same gates
                // as the empty form (agreement first, never over TLS).
                if (!MccpStartAllowed(inputOption))
                {
                    return;
                }

                WriteLog("Starting MCCP compression; ignoring padding bytes.");
                if (inputOption == (int)Options.Mccp2)
                {
                    Mccp2Active = true;
                }
                else
                {
                    Mccp3Active = true;
                }

                ArmMccpStream(inputOption);
                if (inputOption == (int)Options.Mccp2)
                {
                    Mccp2StartReceived?.Invoke();
                }
                else
                {
                    Mccp3StartReceived?.Invoke();
                }

                return;
            }

            if (inputOption == (int)Options.CharacterSet && payload[0] != CharsetProtocol.Request)
            {
                // ACCEPTED/REJECTED answers (and unimplemented table verbs)
                // never look like SEND, so they bypass the gate below.
                await ReplyCharsetAnswerAsync(payload).ConfigureAwait(false);
                return;
            }

            if (payload[0] != 1) // Sub-negotiation SEND command.
            {
                WriteLog("Ignoring unsolicited subnegotiation answer.");
                return;
            }

            await ReplySendAsync(inputOption, payload).ConfigureAwait(false);
        }

        /// <summary>
        /// Answer a SEND subnegotiation for an option with the SEND/IS shape:
        /// terminal type, terminal speed, environment, X display, status, or
        /// character set.
        /// </summary>
        /// <param name="inputOption">The option under negotiation.</param>
        /// <param name="payload">The full received subnegotiation payload, SEND first.</param>
        private async Task ReplySendAsync(int inputOption, List<byte> payload)
        {
            switch (inputOption)
            {
                case (int)Options.TerminalType:
                    await SendNegotiation(inputOption, TerminalTypeProvider?.Invoke() ?? TerminalType).ConfigureAwait(false);
                    break;
                case (int)Options.TerminalSpeed:
                    string? speed = TerminalSpeedProtocol.Validate(TerminalSpeed);
                    if (speed is null)
                    {
                        WriteLog("Skipping TERMINAL-SPEED reply: malformed speed (want \"<tx>,<rx>\" decimal).");
                        break;
                    }

                    await SendNegotiation(inputOption, speed).ConfigureAwait(false);
                    break;
                case (int)Options.OldEnvironment:
                case (int)Options.NewEnvironment:
                    await ReplyEnvironmentAsync(inputOption, payload).ConfigureAwait(false);
                    break;
                case (int)Options.XDisplay:
                    // RFC 1096 §4: IS answers SEND, even when unconfigured, with an
                    // empty display string.
                    await SendNegotiation(inputOption, XDisplayLocation ?? string.Empty).ConfigureAwait(false);
                    break;
                case (int)Options.Status:
                    if (!Negotiation.IsEnabledByUs((int)Options.Status))
                    {
                        WriteLog("Ignoring STATUS SEND without agreement.");
                        break;
                    }

                    await ReplyStatusAsync().ConfigureAwait(false);
                    break;
                case (int)Options.CharacterSet:
                    // RFC 2066: REQUEST shares the SEND byte value (1), so it
                    // arrives here; ACCEPTED/REJECTED bypass the gate above.
                    await ReplyCharsetRequestAsync(payload).ConfigureAwait(false);
                    break;
                default:
                    // We don't handle other sub negotiation options yet.
                    WriteLog("Request to negotiate: " + Enum.GetName(typeof(Options), inputOption));
                    break;
            }
        }

        private Task SendWont(int inputOption)
        {
            var outBuffer = new byte[3];
            outBuffer[0] = (byte)Commands.InterpretAsCommand;
            outBuffer[1] = (byte)Commands.Wont;
            outBuffer[2] = (byte)inputOption;
            return WriteWireAsync(outBuffer, 0, outBuffer.Length, internalCancellation.Token);
        }

        private Task SendDont(int inputOption)
        {
            var outBuffer = new byte[3];
            outBuffer[0] = (byte)Commands.InterpretAsCommand;
            outBuffer[1] = (byte)Commands.Dont;
            outBuffer[2] = (byte)inputOption;
            return WriteWireAsync(outBuffer, 0, outBuffer.Length, internalCancellation.Token);
        }

        /// <summary>
        /// Send the sub negotiation response to the server.
        /// </summary>
        /// <param name="inputOption">The option we are negotiating.</param>
        /// <param name="optionMessage">The setting for <paramref name="inputOption"/>.</param>
        private Task SendNegotiation(int inputOption, string optionMessage)
        {
            WriteLog("Sending: " + Enum.GetName(typeof(Options), inputOption) + " Setting: " + optionMessage);
            return SendNegotiation(inputOption, [EnvironmentProtocol.Is, .. ToNegotiationBytes(optionMessage)]);
        }

        private static byte[] ToNegotiationBytes(string optionMessage)
        {
            // Latin-1 one-to-one mapping (manual truncating loop, kept to pin the
            // historical byte mapping). IAC escaping happens in the byte[]
            // overload below, not here.
            var bytes = new byte[optionMessage.Length];
            for (var i = 0; i < optionMessage.Length; i++)
            {
                bytes[i] = (byte)optionMessage[i];
            }

            return bytes;
        }

        /// <summary>
        /// Send the sub negotiation response to the server, escaping literal IAC
        /// bytes in the payload by doubling them (RFC 854).
        /// </summary>
        /// <param name="inputOption">The option we are negotiating.</param>
        /// <param name="verbFirstPayload">The payload bytes starting with the subnegotiation
        /// verb (<c>IS</c>, <c>INFO</c>, ...), without IAC SB/SE framing.</param>
        private Task SendNegotiation(int inputOption, byte[] verbFirstPayload)
        {
            var frame = EnvironmentProtocol.FrameSubnegotiation(inputOption, verbFirstPayload);
            return WriteWireAsync(frame, 0, frame.Length, internalCancellation.Token);
        }

        /// <summary>
        /// Answer an RFC 1408/1572 ENVIRON SEND with an IS built from the
        /// configured environment values. The SEND type list (after the verb)
        /// is mirrored. Old (36) and new (39) forms share framing and verbs;
        /// the answer goes out on whichever option asked. A <c>VAR</c> (or
        /// empty) request also volunteers the session parameters
        /// <c>TERM</c>, <c>LANG</c>, <c>COLUMNS</c> and <c>LINES</c>, matching
        /// telnetlib3's auto-sent <c>send_env</c> set; <c>LANG</c> is
        /// <c>C</c> without an explicit <see cref="TextEncoding"/>, else
        /// <c>en_US.&lt;encoding&gt;</c>.
        /// </summary>
        /// <param name="inputOption">The option under negotiation (old or new).</param>
        /// <param name="payload">The full received subnegotiation payload, verb first.</param>
        private Task ReplyEnvironmentAsync(int inputOption, List<byte> payload)
        {
            var (width, height) = NawsProtocol.GetEffectiveSize(WindowWidth, WindowHeight);
            var lang = TextEncoding is null ? "C" : "en_US." + TextEncoding.WebName.Replace("-", string.Empty, StringComparison.Ordinal);
            var colorTerm = System.Environment.GetEnvironmentVariable("COLORTERM") ?? string.Empty;
            var response = EnvironmentProtocol.BuildResponse(
              EnvironmentProtocol.Is,
              payload.Skip(1),
              EnvironmentUser,
              EnvironmentDisplay,
              EnvironmentUserVars,
              string.IsNullOrEmpty(TerminalType) ? null : TerminalType,
              lang,
              width.ToString(System.Globalization.CultureInfo.InvariantCulture),
              height.ToString(System.Globalization.CultureInfo.InvariantCulture),
              colorTerm);
            WriteLog("Sending: " + Enum.GetName(typeof(Options), inputOption));
            return SendNegotiation(inputOption, response);
        }

        /// <summary>
        /// Gets whether <paramref name="inputOption"/> allows an empty
        /// <c>IAC SB &lt;opt&gt; IAC SE</c>: MCCP2/MCCP3 start compression
        /// with one, and several MUD options permit one (reference
        /// <c>_EMPTY_SB_OK</c> set).
        /// </summary>
        /// <param name="inputOption">The option under negotiation.</param>
        private static bool IsEmptySbAllowed(int inputOption)
        {
            return inputOption is (int)Options.Mccp2 or (int)Options.Mccp3 or
              (int)Options.Msp or (int)Options.Mxp or (int)Options.Zmp or
              (int)Options.Aardwolf or (int)Options.Atcp;
        }

        /// <summary>
        /// Gets whether <paramref name="inputOption"/> is a MUD option with an
        /// opaque subnegotiation body (MSDP, MSSP, MSP, MXP, ZMP, Aardwolf,
        /// ATCP, GMCP).
        /// </summary>
        /// <param name="inputOption">The option under negotiation.</param>
        private static bool IsMudOption(int inputOption)
        {
            return inputOption is (int)Options.Msdp or (int)Options.Mssp or
              (int)Options.Msp or (int)Options.Mxp or (int)Options.Zmp or
              (int)Options.Aardwolf or (int)Options.Atcp or (int)Options.Gmcp;
        }

        /// <summary>
        /// Decodes with the agreed CHARSET when one resolved (strict, so the
        /// codecs still fall back to Latin-1), else UTF-8 with Latin-1
        /// fallback — the reference's <c>environ_encoding or "utf-8"</c>.
        /// </summary>
        private Encoding? MudEncoding()
        {
            if (NegotiatedCharset is null)
            {
                return null;
            }

            try
            {
                return Encoding.GetEncoding(
                  NegotiatedCharset,
                  EncoderFallback.ExceptionFallback,
                  DecoderFallback.ExceptionFallback);
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        /// <summary>
        /// Dispatches a MUD subnegotiation body (empty or not) to its
        /// per-protocol decoder: typed hooks fire, the reference's stores
        /// update (MSSP replaced, MXP/Aardwolf/ATCP appended, ZMP per-command
        /// replaced), and the raw <see cref="MudSubnegotiationReceived"/>
        /// hook fires last so it still surfaces every body.
        /// </summary>
        /// <param name="inputOption">The MUD option number.</param>
        /// <param name="body">The subnegotiation body without the option byte.</param>
        private void DispatchMud(int inputOption, byte[] body)
        {
            var encoding = MudEncoding();
            switch (inputOption)
            {
                case (int)Options.Gmcp:
                    var (package, data) = MudProtocol.GmcpDecode(body, encoding);
                    GmcpReceived?.Invoke(package, data);
                    break;
                case (int)Options.Msdp:
                    MsdpReceived?.Invoke(MudProtocol.MsdpDecode(body, encoding));
                    break;
                case (int)Options.Mssp:
                    var status = MudProtocol.MsspDecode(body, encoding);
                    MsspData = new Dictionary<string, object>(status);
                    MsspReceived?.Invoke(status);
                    break;
                case (int)Options.Msp:
                    MspData.Add(body);
                    MspReceived?.Invoke(body);
                    break;
                case (int)Options.Mxp:
                    MxpData.Add(body);
                    MxpReceived?.Invoke(body);
                    break;
                case (int)Options.Zmp:
                    var parts = MudProtocol.ZmpDecode(body, encoding);
                    if (parts.Count > 0)
                    {
                        IReadOnlyList<string> args = [.. parts.Skip(1)];
                        ZmpData[parts[0]] = args;
                        ZmpReceived?.Invoke(parts[0], args);
                    }

                    break;
                case (int)Options.Aardwolf:
                    var message = MudProtocol.AardwolfDecode(body);
                    AardwolfData.Add(message);
                    AardwolfReceived?.Invoke(message);
                    break;
                case (int)Options.Atcp:
                    var (atcpPackage, atcpValue) = MudProtocol.AtcpDecode(body, encoding);
                    AtcpData.Add((atcpPackage, atcpValue));
                    AtcpReceived?.Invoke(atcpPackage, atcpValue);
                    break;
                default:
                    break;
            }

            MudSubnegotiationReceived?.Invoke(inputOption, body);
        }

        /// <summary>
        /// Handles an empty subnegotiation for an option that allows one:
        /// MCCP2/MCCP3 arm inflation through the session-owned
        /// <see cref="MccpStream"/> (created here, replaced when spent) and
        /// fire their start hooks; MUD options dispatch an empty body through
        /// <see cref="DispatchMud"/> like any other body.
        /// </summary>
        /// <param name="inputOption">The option under negotiation.</param>
        private void ReplyEmptySb(int inputOption)
        {
            if (inputOption is (int)Options.Mccp2 or (int)Options.Mccp3)
            {
                if (!MccpStartAllowed(inputOption))
                {
                    return;
                }

                if (inputOption == (int)Options.Mccp2)
                {
                    WriteLog("MCCP2 compression started; inflating inbound bytes.");
                    Mccp2Active = true;
                    ArmMccpStream(inputOption);
                    Mccp2StartReceived?.Invoke();
                    return;
                }

                WriteLog("MCCP3 compression started; inflating inbound bytes.");
                Mccp3Active = true;
                ArmMccpStream(inputOption);
                Mccp3StartReceived?.Invoke();
                return;
            }

            DispatchMud(inputOption, []);
        }

        /// <summary>
        /// Whether an MCCP SB may start inflation: compression opted in,
        /// never over TLS (CRIME/BREACH), and only after WILL/DO agreement.
        /// Both the empty and the padding-carrying SB forms share this gate.
        /// </summary>
        /// <param name="inputOption">The MCCP option (MCCP2 or MCCP3).</param>
        private bool MccpStartAllowed(int inputOption)
        {
            return EnableMccp && !IsTlsActive &&
                (Negotiation.IsEnabledByPeer(inputOption) || Negotiation.IsEnabledByUs(inputOption));
        }

        /// <summary>
        /// Arms the session-owned MCCP decompressor, replacing a spent one
        /// (ended or failed) so a fresh SB always starts a fresh stream.
        /// </summary>
        /// <param name="inputOption">The MCCP option that armed (for the corrupt-path refusal: WONT for MCCP3, DONT for MCCP2).</param>
        private void ArmMccpStream(int inputOption)
        {
            if (MccpStream is null || !MccpStream.IsActive)
            {
                MccpStream = new MccpDecompressor();
            }

            mccpShutdownPending = false;
            mccpShutdownOption = inputOption;
            MccpStateChanged?.Invoke(Mccp2Active, Mccp3Active, MccpStream);
        }

        /// <summary>
        /// Answer an RFC 859 STATUS SEND with an IS snapshot rendered from the
        /// persistent negotiation state (all options, defaults omitted). Called
        /// only when we are the agreed WILL-sender (see <see cref="WeAgree"/> and
        /// the SEND gate in <see cref="ReplySendAsync"/>): unsolicited SENDs earn
        /// WONT instead.
        /// </summary>
        private Task ReplyStatusAsync()
        {
            var items = StatusProtocol.BuildIsPayload(Negotiation);
            WriteLog("Sending: " + nameof(Options.Status));
            var frame = StatusProtocol.FrameStatusIs(items);
            return WriteWireAsync(frame, 0, frame.Length, internalCancellation.Token);
        }

        /// <summary>
        /// Consumes an SNDLOC subnegotiation (RFC 779): the payload is the raw
        /// ASCII location with no SEND/IS verbs. Records it as
        /// <see cref="LastLocation"/> and fires <see cref="LocationReceived"/>.
        /// </summary>
        /// <param name="payload">The received payload (location bytes).</param>
        private void ReplySendLocation(List<byte> payload)
        {
            var location = Encoding.Latin1.GetString([.. payload]);
            WriteLog("Received SNDLOC location.");
            LastLocation = location;
            LocationReceived?.Invoke(location);
        }

        /// <summary>
        /// Consumes an LFLOW subnegotiation (RFC 1372): a single mode byte
        /// (0-3). ON/OFF flips <see cref="LineflowEnabled"/>, RESTART_ANY/XON
        /// flips <see cref="LineflowXonAny"/>; the other flag is untouched.
        /// Only the side that agreed to perform LFLOW (local agreement)
        /// adopts the mode: like the reference, a well-formed but unsolicited
        /// body is consumed silently — never answered with WONT — and a
        /// malformed body is logged and ignored.
        /// </summary>
        /// <param name="payload">The received payload (mode byte).</param>
        private Task ReplyLineflowAsync(List<byte> payload)
        {
            if (payload.Count != 1 || !LineflowProtocol.IsDefined(payload[0]))
            {
                WriteLog("Ignoring malformed LFLOW subnegotiation (want one mode byte 0-3).");
                return Task.CompletedTask;
            }

            if (!Negotiation.IsEnabledByUs((int)Options.RemoteFlowControl))
            {
                WriteLog("Ignoring unsolicited LFLOW subnegotiation (LFLOW not agreed).");
                return Task.CompletedTask;
            }

            var mode = payload[0];
            if (mode is LineflowProtocol.Off or LineflowProtocol.On)
            {
                LineflowEnabled = mode == LineflowProtocol.On;
            }
            else
            {
                LineflowXonAny = mode == LineflowProtocol.RestartXon;
            }

            WriteLog("Received LFLOW mode: " + mode);
            LineflowReceived?.Invoke(mode);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Answers a CHARSET REQUEST (RFC 2066) with ACCEPTED for the selected
        /// offer or REJECTED when nothing matches. A simultaneous REQUEST
        /// (one arriving while our own is outstanding) is REJECTED on a
        /// server role (RFC 2066 §5). ACCEPTED latches
        /// <see cref="ForceBinaryDecoding"/> and switches
        /// <see cref="TextEncoding"/> to the agreed encoding.
        /// </summary>
        /// <param name="payload">The received payload, REQUEST first.</param>
        private Task ReplyCharsetRequestAsync(List<byte> payload)
        {
            if (CharsetRequestPending && IsServerRole)
            {
                WriteLog("Rejecting simultaneous CHARSET request: our own REQUEST is outstanding.");
                var rejected = EnvironmentProtocol.FrameSubnegotiation((int)Options.CharacterSet, [CharsetProtocol.Rejected]);
                return WriteWireAsync(rejected, 0, rejected.Length, internalCancellation.Token);
            }

            var offers = CharsetProtocol.ParseRequest(payload);
            // Reference selection policy (send_charset Cases 2-3): with no
            // local encoding preference — or a weak Latin-1 default — the
            // first viable peer offer is accepted, not just offers we would
            // send ourselves. An explicit preference keeps the
            // CharsetOffers intersection with exact-match priority.
            var selected = CharsetSelector is not null
              ? CharsetSelector(offers)
              : CharsetProtocol.SelectSupported(
                  TextEncoding is null || CharsetProtocol.IsWeakDefault(TextEncoding.WebName)
                    ? offers
                    : CharsetOffers.Count > 0
                      ? CharsetOffers.Where(offered => offers.Contains(offered, StringComparer.OrdinalIgnoreCase)).ToList()
                      : offers,
                  TextEncoding?.WebName);
            if (selected is null)
            {
                WriteLog("Rejecting CHARSET request: no supported offer.");
                CharsetRejected?.Invoke();
                var frame = EnvironmentProtocol.FrameSubnegotiation((int)Options.CharacterSet, [CharsetProtocol.Rejected]);
                return WriteWireAsync(frame, 0, frame.Length, internalCancellation.Token);
            }

            WriteLog("Sending: " + nameof(Options.CharacterSet) + " ACCEPTED " + selected);
            AdoptCharset(selected);
            CharsetAccepted?.Invoke(selected);
            var accepted = EnvironmentProtocol.FrameSubnegotiation(
              (int)Options.CharacterSet, CharsetProtocol.BuildAccepted(selected));
            return WriteWireAsync(accepted, 0, accepted.Length, internalCancellation.Token);
        }

        /// <summary>
        /// Applies an agreed character set: records <see cref="NegotiatedCharset"/>
        /// (the raw wire spelling), latches <see cref="ForceBinaryDecoding"/> (the
        /// peer presumes BINARY capability), and switches
        /// <see cref="TextEncoding"/> to the agreed encoding, resolved through
        /// its canonical name so a spelling .NET does not know (e.g.
        /// <c>latin-1</c>) still switches when a normalized variant resolves
        /// (keeping the current encoding only when nothing resolves).
        /// </summary>
        /// <param name="charset">The agreed character-set name.</param>
        private void AdoptCharset(string charset)
        {
            NegotiatedCharset = charset;
            ForceBinaryDecoding = true;
            var canonical = CharsetProtocol.CanonicalName(charset);
            if (canonical is null)
            {
                return;
            }

            try
            {
                TextEncoding = Encoding.GetEncoding(canonical);
            }
            catch (ArgumentException)
            {
            }
        }

        /// <summary>
        /// Consumes a CHARSET answer (RFC 2066) to our own REQUEST. ACCEPTED
        /// records <see cref="NegotiatedCharset"/>, latches
        /// <see cref="ForceBinaryDecoding"/>, switches <see cref="TextEncoding"/>,
        /// and fires <see cref="CharsetAccepted"/>; REJECTED leaves the charset
        /// null (TextEncoding stays unset, so bytes keep passing through as
        /// (char)byte; no bytes are dropped)
        /// and fires <see cref="CharsetRejected"/>. An inbound
        /// <c>TTABLE-IS</c> is answered <c>TTABLE-REJECTED</c> (table transfer
        /// declined); <c>TTABLE-REJECTED</c> only clears the outstanding
        /// request, <c>TTABLE-ACK/NAK</c> and other verbs are logged and
        /// ignored (never thrown: the read loop must survive them).
        /// </summary>
        /// <param name="payload">The received payload, verb first.</param>
        private Task ReplyCharsetAnswerAsync(List<byte> payload)
        {
            if (payload[0] == CharsetProtocol.Accepted)
            {
                var charset = CharsetProtocol.ParseAccepted(payload);
                WriteLog("CHARSET accepted: " + charset);
                AdoptCharset(charset);
                CharsetRequestPending = false;
                CharsetAccepted?.Invoke(charset);
                return Task.CompletedTask;
            }

            if (payload[0] == CharsetProtocol.Rejected)
            {
                WriteLog("CHARSET rejected by peer.");
                CharsetRequestPending = false;
                CharsetRejected?.Invoke();
                return Task.CompletedTask;
            }

            if (payload[0] == CharsetProtocol.TTableIs)
            {
                WriteLog("Declining CHARSET table transfer with TTABLE-REJECTED.");
                var frame = EnvironmentProtocol.FrameSubnegotiation((int)Options.CharacterSet, CharsetProtocol.BuildTTableRejected());
                return WriteWireAsync(frame, 0, frame.Length, internalCancellation.Token);
            }

            if (payload[0] == CharsetProtocol.TTableRejected)
            {
                WriteLog("CHARSET table transfer rejected; charset unchanged.");
                CharsetRequestPending = false;
                return Task.CompletedTask;
            }

            WriteLog("Ignoring CHARSET table-transfer verb: " + payload[0]);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Sends <c>IAC EOR</c> (RFC 885). Fails (returns <c>false</c>) unless
        /// EOR is locally enabled (the peer sent <c>DO EOR</c>), mirroring the
        /// reference <c>send_eor</c> guard.
        /// </summary>
        internal async Task<bool> SendEorAsync()
        {
            if (!Negotiation.IsEnabledByUs((int)Options.EndOfRecord))
            {
                WriteLog("Cannot send EOR without receipt of DO EOR.");
                return false;
            }

            var frame = new byte[] { (byte)Commands.InterpretAsCommand, (byte)Commands.EndOfRecord };
            await WriteWireAsync(frame, 0, frame.Length, internalCancellation.Token).ConfigureAwait(false);
            return true;
        }

        /// <summary>
        /// Sends our location as <c>IAC SB SNDLOC &lt;location&gt; IAC SE</c>
        /// (RFC 779, ASCII, no verbs).
        /// </summary>
        /// <param name="location">The location string.</param>
        internal Task SendLocationPayloadAsync(string location)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(location);
            WriteLog("Sending: " + nameof(Options.SendLocation));
            var frame = EnvironmentProtocol.FrameSubnegotiation(
              (int)Options.SendLocation, Encoding.ASCII.GetBytes(location));
            return WriteWireAsync(frame, 0, frame.Length, internalCancellation.Token);
        }

        /// <summary>
        /// Sends the LFLOW mode (RFC 1372) as a server: RESTART_ANY when
        /// <paramref name="restartOnAny"/> is true, else RESTART_XON. Fails
        /// (returns <c>false</c>) unless the peer enabled LFLOW (sent
        /// <c>WILL LFLOW</c>), mirroring the reference server-only guard.
        /// </summary>
        /// <param name="restartOnAny">Whether any character restarts output.</param>
        internal async Task<bool> SendLineflowModeAsync(bool restartOnAny)
        {
            if (!Negotiation.IsEnabledByPeer((int)Options.RemoteFlowControl))
            {
                WriteLog("Cannot send LFLOW without receipt of WILL LFLOW.");
                return false;
            }

            var mode = restartOnAny ? LineflowProtocol.RestartAny : LineflowProtocol.RestartXon;
            WriteLog("Sending: " + nameof(Options.RemoteFlowControl) + " mode " + mode);
            var frame = EnvironmentProtocol.FrameSubnegotiation((int)Options.RemoteFlowControl, [mode]);
            await WriteWireAsync(frame, 0, frame.Length, internalCancellation.Token).ConfigureAwait(false);
            return true;
        }

        /// <summary>
        /// Sends a CHARSET REQUEST (RFC 2066) offering
        /// <see cref="CharsetOffers"/>. Fails (returns <c>false</c>) unless
        /// CHARSET is enabled on either side, offers are configured, or an
        /// identical request is already outstanding (single-active rule).
        /// </summary>
        internal async Task<bool> RequestCharsetAsync()
        {
            if (!Negotiation.IsEnabledByPeer((int)Options.CharacterSet) &&
                !Negotiation.IsEnabledByUs((int)Options.CharacterSet))
            {
                WriteLog("Cannot request CHARSET without negotiating CHARSET first.");
                return false;
            }

            if (CharsetRequestPending)
            {
                WriteLog("Cannot request CHARSET: a REQUEST is already outstanding.");
                return false;
            }

            if (CharsetOffers.Count == 0)
            {
                WriteLog("Cannot request CHARSET with no offers configured.");
                return false;
            }

            WriteLog("Sending: " + nameof(Options.CharacterSet) + " REQUEST.");
            var frame = EnvironmentProtocol.FrameSubnegotiation(
              (int)Options.CharacterSet, CharsetProtocol.BuildRequest(CharsetOffers));
            await WriteWireAsync(frame, 0, frame.Length, internalCancellation.Token).ConfigureAwait(false);
            CharsetRequestPending = true;
            return true;
        }

        /// <summary>
        /// Sends a MUD subnegotiation body (MSDP, MSSP, MSP, MXP, ZMP,
        /// Aardwolf, ATCP, GMCP) or a COM port control body (RFC 2217
        /// framing level). Fails (returns <c>false</c>) unless the option is
        /// enabled on either side.
        /// </summary>
        /// <param name="option">The MUD or COM-port option.</param>
        /// <param name="payload">The body bytes (without the option byte).</param>
        internal async Task<bool> SendExtensionPayloadAsync(Options option, byte[] payload)
        {
            ArgumentNullException.ThrowIfNull(payload);
            var number = (int)option;
            if (!IsMudOption(number) && number != (int)Options.COMPortControl)
            {
                throw new ArgumentOutOfRangeException(nameof(option), option, "Only MUD options and COM port control can be sent with SendExtensionPayloadAsync.");
            }

            if (!Negotiation.IsEnabledByPeer(number) && !Negotiation.IsEnabledByUs(number))
            {
                WriteLog("Cannot send " + option + " without negotiating it first.");
                return false;
            }

            var frame = EnvironmentProtocol.FrameSubnegotiation(number, payload);
            await WriteWireAsync(frame, 0, frame.Length, internalCancellation.Token).ConfigureAwait(false);
            return true;
        }

        /// <summary>
        /// Sends a GMCP message. Fails (returns <c>false</c>) unless GMCP is
        /// enabled on either side; the frame doubles embedded IAC bytes.
        /// </summary>
        /// <param name="package">The dotted package name.</param>
        /// <param name="data">Optional data, encoded as compact JSON.</param>
        internal Task<bool> SendGmcpAsync(string package, object? data = null)
        {
            return SendExtensionPayloadAsync(Options.Gmcp, MudProtocol.GmcpEncodeData(package, data));
        }

        /// <summary>
        /// Sends MSDP variables. Fails (returns <c>false</c>) unless MSDP is
        /// enabled on either side.
        /// </summary>
        /// <param name="variables">Variable names to values.</param>
        internal Task<bool> SendMsdpAsync(IReadOnlyDictionary<string, object?> variables)
        {
            return SendExtensionPayloadAsync(Options.Msdp, MudProtocol.MsdpEncode(variables));
        }

        /// <summary>
        /// Sends MSSP variables. Fails (returns <c>false</c>) unless MSSP is
        /// enabled on either side.
        /// </summary>
        /// <param name="variables">Variable names to single or repeated values.</param>
        internal Task<bool> SendMsspAsync(IReadOnlyDictionary<string, object> variables)
        {
            return SendExtensionPayloadAsync(Options.Mssp, MudProtocol.MsspEncode(variables));
        }

        /// <summary>
        /// Sends a ZMP message. Fails (returns <c>false</c>) unless ZMP is
        /// enabled on either side.
        /// </summary>
        /// <param name="parts">The command followed by its arguments.</param>
        internal Task<bool> SendZmpAsync(params string[] parts)
        {
            return SendExtensionPayloadAsync(Options.Zmp, MudProtocol.ZmpEncode(parts));
        }

        /// <summary>
        /// Answer an RFC 1184 LINEMODE subnegotiation. Dispatches on the
        /// LINEMODE subcommand byte: MODE mask confirmation, FORWARDMASK
        /// refusal, or SLC table update.
        /// </summary>
        /// <param name="payload">The full received subnegotiation payload, subcommand first.</param>
        private Task ReplyLinemodeAsync(List<byte> payload)
        {
            switch (payload[0])
            {
                case LinemodeProtocol.Mode:
                    return ReplyModeAsync(payload);
                case LinemodeProtocol.SetLocalCharacters:
                    return ReplySlcAsync(payload);
                case (byte)Commands.Do:
                case (byte)Commands.Dont:
                case (byte)Commands.Will:
                case (byte)Commands.Wont:
                    return ReplyForwardMaskAsync(payload);
                default:
                    WriteLog("Ignoring unknown LINEMODE subcommand: " + payload[0]);
                    return Task.CompletedTask;
            }
        }

        /// <summary>
        /// Confirm a MODE mask (RFC 1184 §2.2): client rules by default, server
        /// rules when <see cref="ApplyLinemodeAsServer"/> is set. A server also
        /// publishes its SLC table on the first MODE (reference: SLC goes out
        /// on the first ACKed MODE via <c>_slc_sent</c>).
        /// </summary>
        /// <param name="payload">The MODE payload ([MODE, mask]).</param>
        private async Task ReplyModeAsync(List<byte> payload)
        {
            if (payload.Count != 2)
            {
                WriteLog("Ignoring malformed LINEMODE MODE (want [MODE, mask]).");
                return;
            }

            if (!Negotiation.IsEnabledByUs((int)Options.LineMode) && !Negotiation.IsEnabledByPeer((int)Options.LineMode))
            {
                WriteLog("Ignoring LINEMODE MODE without LINEMODE agreement.");
                return;
            }

            if (ApplyLinemodeAsServer && Linemode.MarkSlcPublished())
            {
                byte[]? triplets = Linemode.ExportTriplets();
                if (triplets is not null)
                {
                    WriteLog("Sending: " + nameof(Options.LineMode) + " SLC table.");
                    await SendNegotiation((int)Options.LineMode,
                      [LinemodeProtocol.SetLocalCharacters, .. triplets]).ConfigureAwait(false);
                }
            }

            byte? reply = ApplyLinemodeAsServer ? Linemode.ApplyModeAsServer(payload[1]) : Linemode.ApplyMode(payload[1]);
            if (reply is null)
            {
                return;
            }

            WriteLog("Sending: " + nameof(Options.LineMode) + " MODE " + reply.Value);
            await SendNegotiation((int)Options.LineMode, [LinemodeProtocol.Mode, reply.Value]).ConfigureAwait(false);
        }

        /// <summary>
        /// Handle a FORWARDMASK exchange (RFC 1184 §2.3). A well-formed DO with
        /// a 1–32 byte mask is stored silently and marks the local sub-state;
        /// WILL/WONT mark the remote sub-state; DONT clears the local one. An
        /// empty DO and a DONT with payload bytes are warned on and ignored.
        /// No reply is emitted in any case.
        /// </summary>
        /// <param name="payload">The FORWARDMASK payload ([verb, FORWARDMASK, mask…]).</param>
        private Task ReplyForwardMaskAsync(List<byte> payload)
        {
            if (payload.Count < 2 || payload[1] != LinemodeProtocol.ForwardMask)
            {
                WriteLog("Ignoring malformed LINEMODE FORWARDMASK.");
                return Task.CompletedTask;
            }

            if (payload[0] == (byte)Commands.Will || payload[0] == (byte)Commands.Wont)
            {
                Linemode.ApplyForwardMaskAnswer(payload[0] == (byte)Commands.Will);
                return Task.CompletedTask;
            }

            if (payload[0] == (byte)Commands.Dont)
            {
                if (payload.Count > 2)
                {
                    WriteLog("Ignoring LINEMODE FORWARDMASK DONT with payload bytes.");
                }

                Linemode.ApplyForwardMaskRefusal();
                return Task.CompletedTask;
            }

            if (payload[0] != (byte)Commands.Do)
            {
                return Task.CompletedTask;
            }

            if (payload.Count < 3)
            {
                WriteLog("Ignoring empty LINEMODE FORWARDMASK DO.");
                return Task.CompletedTask;
            }

            byte[] mask = payload.Skip(2).ToArray();
            if (!Linemode.ApplyForwardMaskOffer(mask))
            {
                WriteLog($"Ignoring LINEMODE FORWARDMASK with invalid length: {mask.Length}.");
                return Task.CompletedTask;
            }

            WriteLog("Storing LINEMODE FORWARDMASK.");
            return Task.CompletedTask;
        }

        /// <summary>
        /// Apply an inbound SLC triplet list (RFC 1184 §2.4/§5.5) and reply
        /// with the resulting ACKs/disagreements, if any. A payload whose
        /// triplet tail is not a multiple of 3 throws (telnetlib3
        /// <c>_handle_sb_linemode_slc</c> raises <c>ValueError</c>); the whole
        /// buffer is rejected, including any valid triplets before the bad tail.
        /// A server additionally requests a forwardmask after every SLC block
        /// (reference <c>request_forwardmask</c>).
        /// </summary>
        /// <param name="payload">The SLC payload ([SLC, func, mod, value, …]).</param>
        /// <exception cref="InvalidDataException">The triplet tail is misaligned.</exception>
        private async Task ReplySlcAsync(List<byte> payload)
        {
            if ((payload.Count - 1) % 3 != 0)
            {
                throw new InvalidDataException($"SLC buffer wrong size: expect multiple of 3: {payload.Count - 1}.");
            }

            List<byte>? replies = null;
            for (int i = 1; i < payload.Count; i += 3)
            {
                (byte Modifier, byte Value)? reply = ApplyLinemodeAsServer
                  ? Linemode.ApplySlcAsServer(payload[i], payload[i + 1], payload[i + 2])
                  : Linemode.ApplySlc(payload[i], payload[i + 1], payload[i + 2]);
                if (reply is not null)
                {
                    replies ??= [];
                    replies.Add(payload[i]);
                    replies.Add(reply.Value.Modifier);
                    replies.Add(reply.Value.Value);
                }
            }

            if (replies is not null)
            {
                WriteLog("Sending: " + nameof(Options.LineMode) + " SLC reply.");
                await SendNegotiation((int)Options.LineMode, [LinemodeProtocol.SetLocalCharacters, .. replies]).ConfigureAwait(false);
            }

            if (ApplyLinemodeAsServer)
            {
                byte[] mask = LinemodeProtocol.BuildForwardMask(Negotiation.IsEnabledByUs((int)Options.TransmitBinary));
                WriteLog("Sending: " + nameof(Options.LineMode) + " DO FORWARDMASK.");
                await SendNegotiation((int)Options.LineMode,
                  [(byte)Commands.Do, LinemodeProtocol.ForwardMask, .. mask]).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Completes an RFC 854 command split across reads: a stashed IAC or
        /// IAC-plus-verb consumes its continuation bytes and dispatches exactly
        /// as if the three bytes arrived together.
        /// </summary>
        /// <param name="sb">The incoming message.</param>
        /// <param name="rawBytes">The raw data bytes backing <paramref name="sb"/>.</param>
        /// <param name="opByteCounts">Parallel to <paramref name="sb"/>: bytes of <paramref name="rawBytes"/> per appended char.</param>
        /// <param name="echoBytes">Parallel to <paramref name="sb"/>: the original data byte to echo per char.</param>
        private async Task<bool> ResumePendingCommandAsync(StringBuilder sb, List<byte> rawBytes, List<int> opByteCounts, List<byte?> echoBytes)
        {
            if (pendingIac)
            {
                pendingIac = false;
                var verb = TryReadByte();
                if (verb == -1)
                {
                    pendingIac = true;
                    return false;
                }

                if (verb == IacByte)
                {
                    AppendRecorded(sb, rawBytes, opByteCounts, echoBytes, (char)IacByte);
                    return true;
                }

                if (verb is (int)Commands.Dont or (int)Commands.Wont or (int)Commands.Do or (int)Commands.Will)
                {
                    var stashedOption = TryReadByte();
                    if (stashedOption == -1)
                    {
                        pendingVerb = verb;
                        return true;
                    }

                    if (stashedOption == IacByte)
                    {
                        return true;
                    }

                    await ReplyToCommandWithOption(verb, stashedOption).ConfigureAwait(false);
                    return true;
                }

                if (verb == (int)Commands.Subnegotiation)
                {
                    var sbOption = TryReadByte();
                    if (sbOption == -1)
                    {
                        pendingVerb = verb;
                        return true;
                    }

                    if (sbOption == IacByte)
                    {
                        return true;
                    }

                    await ScanAndDispatchSbAsync(sbOption, [], false, false).ConfigureAwait(false);
                    return true;
                }

                await InterpretNextAsCommand(sb, rawBytes, opByteCounts, echoBytes, verb).ConfigureAwait(false);
                return true;
            }

            if (pendingVerb.HasValue)
            {
                var stashedVerb = pendingVerb.Value;
                pendingVerb = null;
                var stashedOption = TryReadByte();
                if (stashedOption == -1)
                {
                    pendingVerb = stashedVerb;
                    return false;
                }

                if (stashedOption == IacByte)
                {
                    return true;
                }

                if (stashedVerb == (int)Commands.Subnegotiation)
                {
                    await ScanAndDispatchSbAsync(stashedOption, [], false, false).ConfigureAwait(false);
                    return true;
                }

                await ReplyToCommandWithOption(stashedVerb, stashedOption).ConfigureAwait(false);
                return true;
            }

            return false;
        }

        private async Task ReplyToCommand(int inputVerb)
        {
            var inputOption = TryReadByte();
            if (inputOption == -1)
            {
                // RFC 854 framing split: the option byte arrives with the continuation.
                pendingVerb = inputVerb;
                return;
            }

            if (inputOption == IacByte)
            {
                // An IAC where the option byte belongs: not a real option,
                // so there is nothing to reply to.
                return;
            }

            await ReplyToCommandWithOption(inputVerb, inputOption).ConfigureAwait(false);
        }

        private async Task ReplyToCommandWithOption(int inputVerb, int inputOption)
        {
            WriteLog(Enum.GetName(typeof(Options), inputOption) ?? inputOption.ToString());
            if (inputOption == (int)Options.TimingMark)
            {
                if (inputVerb == (int)Commands.Do)
                {
                    await WriteWireAsync([(byte)Commands.InterpretAsCommand, (byte)Commands.Will, (byte)inputOption], 0, 3, internalCancellation.Token).ConfigureAwait(false);
                    return;
                }

                // A timing-mark reply only completes a ping we sent: an
                // outstanding DO TM is cleared and the agreement persisted,
                // with no reply bytes either way. Anything else is ignored.
                if (Negotiation.GetStates((int)Options.TimingMark).Him == NegotiationState.SideState.WantYes)
                {
                    if (inputVerb == (int)Commands.Will)
                    {
                        Negotiation.ReceivedWill((int)Options.TimingMark, agree: true);
                        return;
                    }

                    if (inputVerb == (int)Commands.Wont)
                    {
                        Negotiation.ReceivedWont((int)Options.TimingMark);
                        return;
                    }
                }

                WriteLog($"Ignoring {(Commands)inputVerb} TIMING-MARK without outstanding DO.");
                return;
            }

            if (inputOption == (int)Options.Logout && inputVerb == (int)Commands.Do)
            {
                WriteLog("Peer requested LOGOUT; closing without negotiation bytes.");
                LogoutRequested?.Invoke();
                return;
            }

            if (inputOption == (int)Options.Echo && IsServerRole && inputVerb == (int)Commands.Will)
            {
                WriteLog("Ignoring WILL ECHO on server role.");
                return;
            }

            var reply = inputVerb switch
            {
                (int)Commands.Do => Negotiation.ReceivedDo(inputOption, AgreeEcho(inputOption, peerPerforms: false)),
                (int)Commands.Dont => Negotiation.ReceivedDont(inputOption),
                (int)Commands.Will => Negotiation.ReceivedWill(inputOption, AgreeEcho(inputOption, peerPerforms: true)),
                (int)Commands.Wont => Negotiation.ReceivedWont(inputOption),
                _ => null,
            };
            if (reply is null)
            {
                WriteLog($"No reply to {inputVerb} {inputOption}: already in that state (RFC 1143).");
                return;
            }

            var outBuffer = new byte[]
            {
        (byte)Commands.InterpretAsCommand,
        (byte)reply,
        (byte)inputOption,
            };
            await WriteWireAsync(outBuffer, 0, outBuffer.Length, internalCancellation.Token).ConfigureAwait(false);

            if (inputOption == (int)Options.WindowSize && reply is Commands.Will or Commands.Do)
            {  // NAWS needs to be sent immediately because the server doesn't request subnegotiation.
                await SendWindowSize().ConfigureAwait(false);
            }

            if (inputOption == (int)Options.SendLocation && reply is Commands.Will && SendLocation is not null)
            {
                // RFC 779: the client volunteers its location right after
                // agreeing (no SEND request exists for this option).
                await SendLocationPayloadAsync(SendLocation).ConfigureAwait(false);
            }

            if (inputOption == (int)Options.RemoteFlowControl && reply is Commands.Do && SendLineflowAsServer)
            {
                await SendLineflowModeAsync(SendLineflowRestartAny).ConfigureAwait(false);
            }

            if (inputOption == (int)Options.Status && inputVerb == (int)Commands.Will && reply is Commands.Do)
            {
                // RFC 859 initiation (reference handle_will): a peer
                // announcing WILL STATUS is immediately put to the test with
                // a SEND probe. Only on the state-changing agreement (a
                // repeat WILL earns no reply above, and no re-probe here).
                var probe = StatusProtocol.FrameStatusSend();
                await WriteWireAsync(probe, 0, probe.Length, internalCancellation.Token).ConfigureAwait(false);
            }

            if (inputOption == (int)Options.Status && inputVerb == (int)Commands.Do && reply is Commands.Will)
            {
                // RFC 859 initiation (reference handle_do): volunteer our
                // snapshot the moment we agree to send it, without waiting
                // for a SEND first.
                await ReplyStatusAsync().ConfigureAwait(false);
            }

            if (inputOption == (int)Options.LineMode && inputVerb == (int)Commands.Do
                && reply is Commands.Will && !ApplyLinemodeAsServer)
            {
                // RFC 1184 §2.4 (reference handle_do): the client initiates
                // the SLC exchange immediately after WILL LINEMODE by
                // requesting the peer's table (func 0, DEFAULT).
                await SendNegotiation((int)Options.LineMode,
                  [LinemodeProtocol.SetLocalCharacters, 0, LinemodeProtocol.LevelDefault, 0]).ConfigureAwait(false);
            }
        }

        private bool WeAgree(int inputOption, bool peerWill)
        {
            if (inputOption == (int)Options.Mccp2 || inputOption == (int)Options.Mccp3)
            {
                // The reference refuses MCCP unless compression is opted in,
                // and always over TLS (CRIME/BREACH).
                return EnableMccp && !IsTlsActive;
            }

            if (inputOption == (int)Options.COMPortControl)
            {
                return EnableComPort;
            }

            if (IsMudOption(inputOption))
            {
                // Role split, mirroring the reference (client-gated decline):
                // a client agrees only when opted in (its options default to
                // decline), while a server agrees. The client stack applies
                // its setting over the default-true plumbing value.
                return EnableMudOptions;
            }

            if (IsServerRole)
            {
                if (!peerWill)
                {
                    if (inputOption == (int)Options.TerminalType ||
                        inputOption == (int)Options.WindowSize ||
                        inputOption == (int)Options.TerminalSpeed ||
                        inputOption == (int)Options.RemoteFlowControl ||
                        inputOption == (int)Options.LineMode ||
                        inputOption == (int)Options.XDisplay ||
                        inputOption == (int)Options.NewEnvironment ||
                        inputOption == (int)Options.SendLocation)
                    {
                        return false;
                    }
                }
            }
            else
            {
                if (peerWill)
                {
                    if (inputOption == (int)Options.WindowSize ||
                        inputOption == (int)Options.LineMode ||
                        inputOption == (int)Options.SendLocation)
                    {
                        return false;
                    }
                }
            }

            return inputOption == (int)Options.SuppressGoAhead ||
              inputOption == (int)Options.TerminalType ||
              inputOption == (int)Options.TerminalSpeed ||
              inputOption == (int)Options.WindowSize ||
              inputOption == (int)Options.TransmitBinary ||
              inputOption == (int)Options.OldEnvironment ||
              inputOption == (int)Options.NewEnvironment ||
              inputOption == (int)Options.XDisplay ||
              inputOption == (int)Options.Status ||
              inputOption == (int)Options.TimingMark ||
              inputOption == (int)Options.LineMode ||
              inputOption == (int)Options.SendLocation ||
              inputOption == (int)Options.EndOfRecord ||
              inputOption == (int)Options.RemoteFlowControl ||
              inputOption == (int)Options.CharacterSet;
        }

        /// <summary>
        /// Agreement for RFC 857 ECHO, which needs per-direction guards on top of
        /// <see cref="WeAgree"/>: the option only controls remote echo, and both
        /// sides echoing at once bounces characters forever.
        /// </summary>
        /// <param name="inputOption">The negotiated option.</param>
        /// <param name="peerPerforms">True for a received WILL (the peer would echo).</param>
        /// <returns>Whether to agree: non-ECHO defers to <see cref="WeAgree"/>.</returns>
        private bool AgreeEcho(int inputOption, bool peerPerforms)
        {
            if (inputOption != (int)Options.Echo)
            {
                return WeAgree(inputOption, peerPerforms);
            }

            if (peerPerforms)
            {
                return !Negotiation.IsEnabledByUs(inputOption);
            }

            if (!IsServerRole)
            {
                return false;
            }

            return AllowRemoteEcho && !Negotiation.IsEnabledByPeer(inputOption);
        }

        /// <summary>
        /// Reports the terminal size per RFC 1073 as Width(16-bit) Height(16-bit),
        /// network byte order. Explicit <see cref="WindowWidth"/>/
        /// <see cref="WindowHeight"/> win (0 means auto); otherwise the console
        /// size is used, falling back to 80x24 when unavailable. Each dimension
        /// is clamped to the 0-65535 wire range.
        /// </summary>
        private Task SendWindowSize()
        {
            var (width, height) = NawsProtocol.GetEffectiveSize(WindowWidth, WindowHeight);
            NawsSizeSent?.Invoke(width, height);
            var payload = new byte[]
            {
        (byte)(width >> 8), (byte)width,
        (byte)(height >> 8), (byte)height,
            };
            return SendNegotiation((int)Options.WindowSize, payload);
        }

        private async Task<bool> IsResponseAnticipated(bool isInitialResponseReceived, DateTime endInitialTimeout, DateTime rollingTimeout)
        {
            // Drain immediately while bytes wait. Otherwise yield every idle
            // pass: an earlier form short-circuited on the open initial window
            // and never reached the delay, busy-spinning the calling thread
            // for the whole window and starving same-thread producers of
            // mid-read bytes (#62: a blocked read never observed an Enqueue).
            if (IsResponsePending)
            {
                return true;
            }

            bool continueWaiting = IsWaitForInitialResponse(endInitialTimeout, isInitialResponseReceived) ||
              !IsTimeoutExpired(rollingTimeout);
            await Task.Delay(MillisecondReadDelay, internalCancellation.Token).ConfigureAwait(false);
            return continueWaiting;
        }
    }
}
