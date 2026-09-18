namespace telnet_cs.Client;

using System;
using System.Collections.Generic;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;

/// <summary>
/// Per-instance client settings. Every member is optional: null (or zero
/// for the window size) falls back to the corresponding static on
/// <see cref="Client"/> (or the documented default), so existing code that
/// only touches the statics behaves exactly as before.
/// A record: <c>ConnectAsync</c> snapshots it with a compiler-generated
/// <c>with</c>-clone, so newly added members flow automatically and can
/// never be silently dropped by a manual field list.
/// </summary>
public record TelnetClientOptions
{
    /// <summary>
    /// Gets or sets the terminal type to negotiate. Null follows <see cref="Client.TerminalType"/>.
    /// </summary>
    public string? TerminalType { get; set; }

    /// <summary>
    /// Ordered terminal-type list (most to least specific) for RFC 1091
    /// cycling: successive SENDs walk this list instead of repeating
    /// <see cref="TerminalType"/>. Empty (the default) disables cycling.
    /// Settable (not init-only) like the rest of this bag, and re-seated
    /// — never shared — by the <c>with</c>-clone in
    /// <c>Client.ApplyOptions</c>.
    /// </summary>
    public IList<string> TerminalTypes { get; set; } = [];

    /// <summary>
    /// Gets or sets the terminal speed to negotiate. Null follows <see cref="Client.TerminalSpeed"/>.
    /// </summary>
    public string? TerminalSpeed { get; set; }

    /// <summary>
    /// Value reported in RFC 1096 X-DISPLAY-LOCATION IS answers. Null (the
    /// default) answers SEND with an empty display string. Follows the Unix DISPLAY convention
    /// (<c>host:display.screen</c>); qualify bare local values yourself.
    /// </summary>
    public string? XDisplayLocation { get; set; }

    /// <summary>
    /// Gets or sets whether text read is echoed to the console. Null follows <see cref="Client.IsWriteConsole"/>.
    /// </summary>
    public bool? IsWriteConsole { get; set; }

    /// <summary>
    /// Opts in to the remote-echo role (RFC 857): a server <c>DO ECHO</c>
    /// is answered with <c>WILL</c>, unless the peer is already echoing
    /// (the infinite-bounce hazard). Agreement is negotiation state only:
    /// received data is never replayed, so an app that wants echo writes
    /// the bytes back itself. False (the default) answers <c>DO ECHO</c>
    /// with <c>WONT</c>.
    /// </summary>
    public bool AllowRemoteEcho { get; set; }

    /// <summary>
    /// Gets or sets whether a received BEL rings the console bell. Null means enabled.
    /// Retained for compatibility; currently has no effect — BEL bytes are
    /// delivered as data like every other control byte.
    /// </summary>
    public bool? EnableBell { get; set; }

    /// <summary>
    /// Gets or sets the encoding for outbound strings and inbound decoding.
    /// Defaults to UTF-8 like the reference client.
    /// </summary>
    public Encoding? TextEncoding { get; set; } = Encoding.UTF8;

    /// <summary>
    /// Gets or sets the maximum terminated-read length in chars (see
    /// <c>TerminatedReadAsync</c>): a buffer past this without the
    /// terminator throws <c>InvalidOperationException</c> naming the
    /// limit. The overlong line is consumed and the next line reads
    /// clean. Defaults to 65536 (the historical 64 KiB reference
    /// limit); <c>0</c> means unlimited. Negative values are rejected
    /// when applied via <c>Client.ApplyOptions</c>. Read live per read
    /// (a per-instance <c>Client</c> override wins).
    /// </summary>
    public int MaxTerminatedReadChars { get; set; } = 65536;

    /// <summary>
    /// Gets or sets the terminal width reported via NAWS. Zero is sent
    /// as-is (RFC 1073 "unspecified").
    /// </summary>
    public int WindowWidth { get; set; }

    /// <summary>
    /// Gets or sets the terminal height reported via NAWS. Zero is sent
    /// as-is (RFC 1073 "unspecified").
    /// </summary>
    public int WindowHeight { get; set; }

    /// <summary>
    /// Optional handler invoked with protocol log messages. Null (the default) disables logging.
    /// </summary>
    public Action<string>? Log { get; set; }

    /// <summary>
    /// Value reported for the well-known <c>USER</c> variable in RFC 1408 ENVIRON responses.
    /// Null (the default) omits <c>USER</c> unless explicitly requested.
    /// </summary>
    public string? EnvironmentUser { get; set; }

    /// <summary>
    /// Value reported for the well-known <c>DISPLAY</c> variable in
    /// spontaneous ENVIRON INFO updates. Null (the default) omits
    /// <c>DISPLAY</c>. Never volunteered on the SB answer path (reference
    /// security rule); INFO is the only carrier.
    /// </summary>
    public string? EnvironmentDisplay { get; set; }

    /// <summary>
    /// User-defined variables reported as <c>USERVAR</c> entries in RFC 1408 ENVIRON responses.
    /// Empty by default. Settable (not init-only) like the rest of this
    /// bag, and re-seated — never shared — by the <c>with</c>-clone in
    /// <c>Client.ApplyOptions</c>.
    /// </summary>
    public Dictionary<string, string> EnvironmentUserVars { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Value sent spontaneously as <c>IAC SB SNDLOC &lt;location&gt; IAC SE</c>
    /// (RFC 779, ASCII) after answering <c>DO SNDLOC</c>. Null (the
    /// default) sends nothing.
    /// </summary>
    public string? SendLocation { get; set; }

    /// <summary>
    /// Character sets offered in CHARSET REQUEST answers (RFC 2066), in
    /// preference order. Defaults to UTF-8, LATIN1, US-ASCII like the
    /// reference client. Empty answers REJECTED. Deliberately narrower
    /// than <see cref="telnet_cs.Server.TelnetServerOptions.CharsetOffers"/> (the
    /// server must match any peer request, the client only offers).
    /// Settable (not init-only) like the rest of this bag, and re-seated
    /// — never shared — by the <c>with</c>-clone in
    /// <c>Client.ApplyOptions</c>.
    /// </summary>
    public IList<string> CharsetOffers { get; set; } = ["UTF-8", "LATIN1", "US-ASCII"];

    /// <summary>
    /// Opts in to MCCP2/MCCP3 compression (options 86/87). True (the
    /// default) passively accepts compression like the reference (only an
    /// explicit False refuses); keep it off over TLS (CRIME/BREACH).
    /// Framing-level only: zlib decoding stays the caller's (see the
    /// MCCP start hooks).
    /// </summary>
    public bool EnableMccp { get; set; } = true;

    /// <summary>
    /// Whether the MUD options other than GMCP/ZMP (MSDP, MSSP, MSP,
    /// MXP, Aardwolf, ATCP) may be agreed. False (the default) declines
    /// them, like the reference client, which only agrees when explicitly
    /// opted in: agreeing invites subnegotiation frames the application
    /// must parse. Set <c>true</c> for MUD play. The server side always
    /// agrees.
    /// </summary>
    public bool EnableMudOptions { get; set; }

    /// <summary>
    /// Whether GMCP (option 201) may be agreed. True (the default)
    /// passively agrees like the reference client, answering WILL GMCP
    /// with <c>Core.Hello</c> plus <c>Core.Supports.Set</c>.
    /// </summary>
    public bool EnableGmcp { get; set; } = true;

    /// <summary>
    /// Whether ZMP (option 93) may be agreed. True (the default)
    /// passively agrees like the reference client, answering WILL ZMP
    /// with <c>zmp.ident</c> plus one <c>zmp.support</c> per
    /// <see cref="ZmpSupportedCommands"/> entry, and auto-answering
    /// <c>zmp.check</c>/<c>zmp.send-support</c> queries.
    /// </summary>
    public bool EnableZmp { get; set; } = true;

    /// <summary>
    /// ZMP commands this client supports, advertised one
    /// <c>zmp.support</c> per command after <c>zmp.ident</c>. Empty (the
    /// default) advertises nothing.
    /// </summary>
    public IList<string> ZmpSupportedCommands { get; set; } = [];

    /// <summary>
    /// Predicate answering <c>zmp.check &lt;cmd&gt;</c> with
    /// <c>zmp.support</c> (true) or <c>zmp.no-support</c> (false). Null
    /// (the default) refuses every command, like the reference client.
    /// </summary>
    public Func<string, bool>? ZmpCheckHandler { get; set; }

    /// <summary>
    /// Whether COM port control (option 44, RFC 2217 framing level) may
    /// be agreed. True (the default) agrees.
    /// </summary>
    public bool EnableComPort { get; set; } = true;

    /// <summary>
    /// Master switch for implicit TLS (telnets-style): the TLS handshake
    /// completes before the first telnet byte flows. False (the default)
    /// keeps the plaintext path byte-identical.
    /// </summary>
    public bool UseTls { get; set; }

    /// <summary>
    /// SNI host name and certificate validation target. Null (the default)
    /// falls back to the connect hostname.
    /// </summary>
    public string? TlsHost { get; set; }

    /// <summary>
    /// Custom server-certificate validation. Null (the default) keeps OS
    /// chain validation.
    /// </summary>
    public RemoteCertificateValidationCallback? TlsValidationCallback { get; set; }

    /// <summary>
    /// Client certificates offered when the server requests them. Null
    /// (the default) offers none.
    /// </summary>
    public X509CertificateCollection? TlsClientCertificates { get; set; }

    /// <summary>
    /// TLS protocol versions to offer. <c>SslProtocols.None</c> (the
    /// default) lets the OS pick the best available.
    /// </summary>
    public SslProtocols TlsProtocols { get; set; } = SslProtocols.None;
}
