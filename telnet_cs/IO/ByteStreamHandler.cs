namespace telnet_cs.IO;

using System;
using System.Collections.Generic;
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
    /// Bare <c>IAC SB IAC</c> split across reads with no option byte yet:
    /// the post-IAC byte arrives with the continuation. SE then discards
    /// the empty frame, anything else is pushed back (telnetlib3
    /// <c>cmd=SB</c> + <c>iac=True</c> persisting across
    /// <c>feed_byte</c> calls).
    /// </summary>
    private bool sbHeaderIacPending;

    /// <summary>
    /// Gets or sets the stashed subnegotiation continuation for the
    /// session round-trip: fed in before each read, captured after.
    /// </summary>
    internal (int Option, byte[] Payload, bool OverCap, bool SePending, bool IacPending, bool HeaderIacPending)? SbResumeState
    {
        get => sbResumeOption.HasValue || sbHeaderIacPending
          ? (sbResumeOption ?? -1, sbResumePayload is null ? [] : [.. sbResumePayload], sbResumeOverCap, sbResumeSePending, sbResumeIacPending, sbHeaderIacPending)
          : null;
        set
        {
            sbResumeOption = value is null || value.Value.Option < 0 ? null : value.Value.Option;
            sbResumePayload = value is null || value.Value.Payload is null ? null : [.. value.Value.Payload];
            sbResumeOverCap = value?.OverCap ?? false;
            sbResumeSePending = value?.SePending ?? false;
            sbResumeIacPending = value?.IacPending ?? false;
            sbHeaderIacPending = value?.HeaderIacPending ?? false;
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
    /// Gets or sets whether inbound negotiation verbs are silently
    /// ignored: no state change, no reply, no log. Fed per read from
    /// <c>TelnetServerOptions.DisableAllNegotiation</c> on server
    /// sessions; always <c>false</c> on the client.
    /// </summary>
    internal bool SilenceNegotiation { get; set; }

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
    /// is sent as-is (RFC 1073 "unspecified").
    /// </summary>
    internal int WindowWidth { get; set; }

    /// <summary>
    /// Gets or sets the terminal height reported via NAWS. Zero (the default)
    /// is sent as-is (RFC 1073 "unspecified").
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
    /// Gets the SLC function code of the most recently delivered data byte
    /// (telnetlib3 <c>slc_received</c>), or null when that byte matches no
    /// SLC table row. Reset on every consumed input byte and set only by
    /// data-byte delivery while <see cref="IsSlcSnoopActive"/> holds, so it
    /// always describes the last byte — never a stale one.
    /// </summary>
    internal byte? SlcReceived { get; private set; }

    /// <summary>
    /// Gets or sets the hook invoked with the SLC function code when a
    /// delivered data byte matches an SLC table row (telnetlib3's
    /// <c>_slc_callback</c>). The byte itself stays in-band: like the
    /// reference, snooping observes without consuming.
    /// </summary>
    internal Action<byte>? SlcFunctionReceived { get; set; }

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
    /// preference an exact canonical match wins, else the request is
    /// rejected so the local encoding is kept.
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
    /// agreed. Defaults to <c>true</c>: like the reference, compression
    /// is passively accepted unless opted out, and must stay off over TLS (CRIME/BREACH).
    /// </summary>
    internal bool EnableMccp { get; set; } = true;

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

    // Whether the pending corrupt-path refusal sends WONT (our offer
    // withdrawn) rather than DONT (the peer's offer refused), captured
    // when the stream armed: a peer-WILLed option refuses with DONT, an
    // option we offered ourselves withdraws with WONT.
    private bool mccpShutdownWont;

    // Arm-time capture feeding the flag above (the corrupt path itself
    // carries no option: the last armed stream selects the refusal).
    private bool mccpArmedWont;

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

    /// <summary>
    /// Gets or sets the hook invoked when the peer starts MCCP2.
    /// </summary>
    internal Action? Mccp2StartReceived { get; set; }

    /// <summary>
    /// Gets or sets the hook invoked when the peer starts MCCP3.
    /// </summary>
    internal Action? Mccp3StartReceived { get; set; }

    /// <summary>
    /// Gets or sets the hook invoked after this side sends the empty MCCP3
    /// start marker (client role agreeing to compress outbound). The
    /// session installs its compressing write view here: the marker went
    /// out raw, so everything after it must be compressed.
    /// </summary>
    internal Action? Mccp3StartSent { get; set; }

    /// <summary>
    /// Gets or sets whether the MUD options other than GMCP/ZMP (MSDP 69,
    /// MSSP 70, MSP 90, MXP 91, Aardwolf 102, ATCP 200) may be agreed.
    /// Defaults to <c>true</c>. The client stack overrides this from its
    /// options (declined by default); the server stack leaves the default
    /// in place and agrees. Subnegotiations dispatch to the typed
    /// hooks and stores (plus the raw <see cref="MudSubnegotiationReceived"/>
    /// hook); text decoding uses the agreed CHARSET when one resolved.
    /// </summary>
    internal bool EnableMudOptions { get; set; } = true;

    /// <summary>
    /// Gets or sets whether GMCP (option 201) may be agreed. Defaults to
    /// <c>true</c>: a client passively agrees and answers with
    /// <c>Core.Hello</c> plus <c>Core.Supports.Set</c>; other MUD options
    /// stay behind <see cref="EnableMudOptions"/>.
    /// </summary>
    internal bool EnableGmcp { get; set; } = true;

    /// <summary>
    /// Gets or sets whether ZMP (option 93) may be agreed. Defaults to
    /// <c>true</c>: a client passively agrees, answers with
    /// <c>zmp.ident</c> plus one <c>zmp.support</c> per supported command,
    /// and auto-answers <c>zmp.check</c>/<c>zmp.send-support</c>.
    /// </summary>
    internal bool EnableZmp { get; set; } = true;

    /// <summary>
    /// Gets or sets the ZMP commands this end supports, advertised one
    /// <c>zmp.support</c> per command after <c>zmp.ident</c> and consulted
    /// (together with <see cref="ZmpCheckHandler"/>) when answering
    /// <c>zmp.check</c>/<c>zmp.send-support</c> queries. Empty (the
    /// default) advertises nothing.
    /// </summary>
    internal IList<string> ZmpSupportedCommands { get; set; } = [];

    /// <summary>
    /// Gets or sets the predicate answering <c>zmp.check &lt;cmd&gt;</c>
    /// and <c>zmp.send-support</c> queries with <c>zmp.support</c> (true)
    /// or <c>zmp.no-support</c> (false), disjunctively with
    /// <see cref="ZmpSupportedCommands"/>. Null (the default) leaves the
    /// decision to the command list alone.
    /// </summary>
    internal Func<string, bool>? ZmpCheckHandler { get; set; }

    /// <summary>
    /// Gets or sets whether COM port control (option 44, RFC 2217 framing
    /// level) may be agreed. Defaults to <c>true</c>. Payloads surface
    /// through <see cref="ComPortReceived"/>; modem-line semantics stay
    /// the caller's.
    /// </summary>
    internal bool EnableComPort { get; set; } = true;

    /// <summary>
    /// Whether the GMCP <c>Core.Hello</c> handshake was already sent for
    /// this connection (reference <c>_gmcp_hello_sent</c>): the hello goes
    /// out once, on the first WILL GMCP agreement.
    /// </summary>
    private bool gmcpHelloSent;

    /// <summary>
    /// Whether the ZMP <c>zmp.ident</c> handshake was already sent for
    /// this connection (reference <c>_zmp_ident_sent</c>). Session-lived:
    /// the client stack round-trips it across per-read handlers so a
    /// bare <c>zmp.send-support</c> is only answered before the ident.
    /// </summary>
    internal bool ZmpIdentSent { get; set; }

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
    /// Gets or sets the per-read MUD list cap (items, 0 = unlimited).
    /// Mirrors <c>TelnetServerOptions.MaxMudListItems</c> when driven by
    /// a server session; defaults to unlimited for standalone use.
    /// </summary>
    internal int MaxMudListItems { get; set; }

    /// <summary>
    /// Gets or sets the per-read MUD list byte cap (0 = unlimited).
    /// </summary>
    internal int MaxMudListBytes { get; set; }

    /// <summary>
    /// Gets or sets the per-read MUD distinct-key cap for ZMP/MSSP
    /// (0 = unlimited).
    /// </summary>
    internal int MaxMudKeys { get; set; }

    /// <summary>
    /// Gets or sets the per-value char cap reused for ZMP args, ATCP
    /// fields, and MSSP string values (0 = unlimited).
    /// </summary>
    internal int MaxMudValueChars { get; set; }

    /// <summary>
    /// Gets or sets the MCCP outstanding decompressed cap (0 = unlimited).
    /// </summary>
    internal int MaxDecompressedBytes { get; set; }

    /// <summary>
    /// Gets or sets the MCCP decompression ratio cap (0 = unlimited).
    /// </summary>
    internal int MaxDecompressionRatio { get; set; }

    /// <summary>
    /// Gets or sets the MCCP compressed input cap (0 = unlimited).
    /// </summary>
    internal int MaxCompressedBytes { get; set; }

    /// <summary>
    /// Gets or sets the MCCP cap log hook.
    /// </summary>
    internal Action<string>? MccpCapLog { get; set; }

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
        get => _traceFlow.CurrentOr(field);
        set => field = value;
    }

    internal static FlowLocal<Action<string>?> TraceOverride => _traceFlow;

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
        Log?.Invoke(message);
        Trace?.Invoke(message);
        System.Diagnostics.Debug.WriteLine(message);
    }
}
