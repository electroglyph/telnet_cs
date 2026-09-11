namespace telnet_cs.Client
{
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
        /// default) answers no SEND. Follows the Unix DISPLAY convention
        /// (<c>host:display.screen</c>); qualify bare local values yourself.
        /// </summary>
        public string? XDisplayLocation { get; set; }

        /// <summary>
        /// Gets or sets whether text read is echoed to the console. Null follows <see cref="Client.IsWriteConsole"/>.
        /// </summary>
        public bool? IsWriteConsole { get; set; }

        /// <summary>
        /// Opts in to performing remote echo (RFC 857): a server <c>DO ECHO</c>
        /// is answered with <c>WILL</c> and received data is echoed back, unless
        /// the peer is already echoing (the infinite-bounce hazard). False (the
        /// default) answers <c>DO ECHO</c> with <c>WONT</c>.
        /// </summary>
        public bool AllowRemoteEcho { get; set; }

        /// <summary>
        /// Gets or sets whether a received BEL rings the console bell. Null means enabled.
        /// </summary>
        public bool? EnableBell { get; set; }

        /// <summary>
        /// Gets or sets the encoding for outbound strings and inbound decoding.
        /// Null keeps the legacy Latin-1 mapping.
        /// </summary>
        public Encoding? TextEncoding { get; set; }

        /// <summary>
        /// Gets or sets the terminal width reported via NAWS. Zero means auto-detect.
        /// </summary>
        public int WindowWidth { get; set; }

        /// <summary>
        /// Gets or sets the terminal height reported via NAWS. Zero means auto-detect.
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
        /// Value reported for the well-known <c>DISPLAY</c> variable in RFC 1408 ENVIRON responses.
        /// Null (the default) omits <c>DISPLAY</c> unless explicitly requested.
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
}
