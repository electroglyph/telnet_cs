namespace telnet_cs.Server
{
    using System;
    using System.Security.Authentication;
    using System.Security.Cryptography.X509Certificates;
    using System.Text;
    using telnet_cs.Client;

    /// <summary>
    /// Per-instance server settings. Every member is optional and mirrors the
    /// client-side <see cref="TelnetClientOptions"/> from the server's side of
    /// the wire: where the client *answers* (terminal type, speed, window
    /// size, environment), the server *requests*; where the client requests,
    /// the server *offers*.
    /// </summary>
    public class TelnetServerOptions
    {
        /// <summary>
        /// Gets or sets the listen backlog (pending-connection queue length).
        /// Must be positive; defaults to 32.
        /// </summary>
        public int Backlog { get; set; } = 32;

        /// <summary>
        /// Gets or sets whether sessions offer <c>WILL ECHO</c> (RFC 857) in the
        /// opening preset: the server echoes agreed inbound data back down the
        /// wire. Defaults to <c>true</c>.
        /// </summary>
        public bool OfferEcho { get; set; } = true;

        /// <summary>
        /// Gets or sets whether sessions offer <c>WILL SuppressGoAhead</c>
        /// (RFC 858) in the opening preset. Defaults to <c>true</c>.
        /// </summary>
        public bool OfferSuppressGoAhead { get; set; } = true;

        /// <summary>
        /// Gets or sets whether sessions request terminal-type reports
        /// (<c>DO TerminalType</c>, RFC 1091) in the opening preset. Defaults
        /// to <c>true</c>.
        /// </summary>
        public bool RequestTerminalType { get; set; } = true;

        /// <summary>
        /// Gets or sets whether sessions request terminal-speed reports
        /// (<c>DO TerminalSpeed</c>, RFC 1079) in the opening preset. Defaults
        /// to <c>true</c>.
        /// </summary>
        public bool RequestTerminalSpeed { get; set; } = true;

        /// <summary>
        /// Gets or sets whether sessions request window-size reports
        /// (<c>DO WindowSize</c>, RFC 1073) in the opening preset. Defaults to
        /// <c>true</c>.
        /// </summary>
        public bool RequestWindowSize { get; set; } = true;

        /// <summary>
        /// Gets or sets whether sessions request environment reports
        /// (<c>DO OldEnvironment</c>, RFC 1408) in the opening preset. Defaults
        /// to <c>true</c>.
        /// </summary>
        public bool RequestEnvironment { get; set; } = true;

        /// <summary>
        /// Gets or sets whether sessions request X display reports
        /// (<c>DO XDisplay</c>, RFC 1096) in the opening preset. Defaults to
        /// <c>false</c>: unlike the other requests, X display reporting stays
        /// opt-in so default opening-preset bytes are unchanged.
        /// </summary>
        public bool RequestXDisplay { get; set; }

        /// <summary>
        /// Gets or sets whether sessions request linemode negotiation
        /// (<c>DO LineMode</c>, RFC 1184) in the opening preset. Defaults to
        /// <c>true</c>.
        /// </summary>
        public bool RequestLinemode { get; set; } = true;

        /// <summary>
        /// Gets or sets the prompt sent before reading the login name in
        /// <c>ServerSession.AuthenticateAsync</c> (S2). Defaults to
        /// <c>"login: "</c>.
        /// </summary>
        public string LoginUserPrompt { get; set; } = "login: ";

        /// <summary>
        /// Gets or sets the prompt sent before reading the password in
        /// <c>ServerSession.AuthenticateAsync</c> (S2). Defaults to
        /// <c>"Password: "</c>.
        /// </summary>
        public string LoginPasswordPrompt { get; set; } = "Password: ";

        /// <summary>
        /// Gets or sets the maximum login attempts before
        /// <c>ServerSession.AuthenticateAsync</c> (S2) gives up. Must be at
        /// least 1; defaults to 3.
        /// </summary>
        public int MaxLoginAttempts { get; set; } = 3;

        /// <summary>
        /// Gets or sets the encoding for outbound strings and inbound decoding.
        /// Null keeps the legacy Latin-1 mapping.
        /// </summary>
        public Encoding? TextEncoding { get; set; }

        /// <summary>
        /// Gets or sets whether text read is echoed to the server console.
        /// Defaults to <c>false</c> (a server has no local user); set
        /// <c>true</c> for debugging.
        /// </summary>
        public bool? IsWriteConsole { get; set; }

        /// <summary>
        /// Optional handler invoked with protocol log messages. Null (the default) disables logging.
        /// </summary>
        public Action<string>? Log { get; set; }

        /// <summary>
        /// Server TLS certificate for implicit TLS (telnets-style). Null (the
        /// default) keeps plaintext: every accepted session handshakes as the
        /// TLS server before the opening preset only when this is set. The
        /// caller loads the certificate (file, PEM, or store).
        /// </summary>
        public X509Certificate2? ServerCertificate { get; set; }

        /// <summary>
        /// TLS protocol versions for the server handshake. <c>SslProtocols.None</c>
        /// (the default) lets the OS pick the best available. Mirrors the
        /// client's <c>TlsProtocols</c>; only used when <c>ServerCertificate</c>
        /// is set.
        /// </summary>
        public SslProtocols TlsProtocols { get; set; } = SslProtocols.None;

        /// <summary>
        /// Local address to listen on. <c>IPAddress.Any</c> (the default) keeps
        /// today's IPv4-everywhere bind; set <c>IPAddress.Loopback</c> for
        /// local-only servers or <c>IPAddress.IPv6Any</c> for IPv6
        /// (dual-mode where the OS supports it). Read once at construction:
        /// later mutations do not rebind the listener.
        /// </summary>
        public System.Net.IPAddress ListenAddress { get; set; } = System.Net.IPAddress.Any;
    }
}
