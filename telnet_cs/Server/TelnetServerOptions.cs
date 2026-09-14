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
        /// Gets or sets whether sessions offer <c>WILL TransmitBinary</c>
        /// (RFC 856) in the opening preset: the server may send 8-bit data.
        /// Defaults to <c>true</c> (the reference advanced preset offers it).
        /// </summary>
        public bool OfferBinary { get; set; } = true;

        /// <summary>
        /// Gets or sets whether sessions request terminal-type reports
        /// (<c>DO TerminalType</c>, RFC 1091) in the opening preset. Defaults
        /// to <c>true</c>.
        /// </summary>
        public bool RequestTerminalType { get; set; } = true;

        /// <summary>
        /// Gets or sets whether sessions request terminal-speed reports
        /// (<c>DO TerminalSpeed</c>, RFC 1079). Defaults to <c>true</c> but is
        /// never sent unsolicited (the reference advanced preset has no
        /// TSPEED): request explicitly via
        /// <c>ServerSession.RequestTerminalSpeedAsync</c>.
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
        /// (<c>DO OldEnvironment</c>, RFC 1408). Defaults to <c>true</c> but is
        /// never sent unsolicited (the reference advanced preset has no
        /// OLD_ENVIRON): request explicitly via
        /// <c>ServerSession.RequestEnvironmentAsync</c>.
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
        /// (<c>DO LineMode</c>, RFC 1184). Defaults to <c>false</c> (char mode,
        /// like the reference <c>line_mode=False</c>); never sent unsolicited
        /// in any case — LINEMODE lives only in the dedicated LinemodeServer
        /// path there. Request explicitly if a linemode session is wanted.
        /// </summary>
        public bool RequestLinemode { get; set; }

        /// <summary>
        /// Gets or sets whether sessions request new-form environment reports
        /// (<c>DO NewEnvironment</c>, RFC 1572) once the terminal type
        /// resolves. Defaults to <c>true</c>: the deferred request goes out
        /// after TTYPE (never in the opening preset), except against
        /// Microsoft telnet (an <c>"ANSI"</c> first answer defers it past
        /// the second report, which crashes on NEW_ENVIRON).
        /// </summary>
        public bool RequestNewEnvironment { get; set; } = true;

        /// <summary>
        /// Gets or sets whether sessions request the peer location
        /// (<c>DO SendLocation</c>, RFC 779). Defaults to <c>false</c>; never
        /// sent unsolicited — request explicitly via
        /// <c>ServerSession.RequestSendLocationAsync</c>.
        /// </summary>
        public bool RequestSendLocation { get; set; }

        /// <summary>
        /// Gets or sets whether sessions request character-set negotiation
        /// (<c>DO CharacterSet</c>, RFC 2066) in the advanced preset (once
        /// negotiation advances, like the reference which sends it whenever a
        /// default encoding is configured). Defaults to <c>true</c>.
        /// </summary>
        public bool RequestCharacterSet { get; set; } = true;

        /// <summary>
        /// Gets or sets the character sets offered in CHARSET REQUESTs
        /// (RFC 2066), in preference order. Defaults to the reference
        /// executable offer list (US-ASCII last).
        /// </summary>
        public IList<string> CharsetOffers { get; set; } = ["UTF-8", "UTF-16", "LATIN1", "CP1252", "ISO-8859-15", "CP437", "SHIFT_JIS", "CP932", "BIG5", "CP950", "GBK", "GB2312", "CP936", "EUC-KR", "CP949", "US-ASCII"];

        /// <summary>
        /// Gets or sets whether sessions may agree MCCP2/MCCP3 compression
        /// (options 86/87) and inflate the inbound stream. Defaults to
        /// <c>true</c> (the reference passively accepts unless opted out);
        /// stays refused over TLS in any case.
        /// </summary>
        public bool EnableMccp { get; set; } = true;

        /// <summary>
        /// Gets or sets whether sessions offer outbound MCCP2 compression
        /// (<c>WILL MCCP2</c>, option 86) in the advanced preset. Defaults to
        /// <c>false</c>: offering is explicit opt-in (like the reference
        /// compression flag), while <see cref="EnableMccp"/> alone only
        /// passively accepts the peer's offer. When the peer accepts, the
        /// session sends the empty SB start marker and compresses everything
        /// after it; stays unoffered over TLS in any case.
        /// </summary>
        public bool OfferMccp2 { get; set; }

        /// <summary>
        /// Gets or sets whether sessions offer client-to-server MCCP3
        /// compression (<c>WILL MCCP3</c>, option 87) in the advanced preset.
        /// Defaults to <c>false</c>: offering is explicit opt-in (like the
        /// reference compression flag), while <see cref="EnableMccp"/> alone
        /// only passively accepts the peer's offer. When the peer accepts, it
        /// sends its own empty SB start marker and compresses everything
        /// after it, which the session inflates; stays unoffered over TLS in
        /// any case.
        /// </summary>
        public bool OfferMccp3 { get; set; }

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

        /// <summary>
        /// Gets or sets the idle disconnect timeout (the reference
        /// <c>timeout = 300</c>): a session whose <see cref="TelnetSessionContext"/>
        /// saw no text read for this long is sent
        /// <c>"Timeout."</c> and closed. Defaults to 300 seconds;
        /// <c>Timeout.InfiniteTimeSpan</c> (or any non-positive span) disables.
        /// </summary>
        public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromSeconds(300);

        /// <summary>
        /// Gets or sets the status-log interval (the reference
        /// <c>StatusLogger(interval = 20)</c>): while set, the server logs one
        /// <c>endpoint (rx,tx,idle,tls)</c> line per accepted session whenever
        /// its counters changed since the previous tick. Defaults to 20
        /// seconds; null (or any non-positive span) disables. Silent without
        /// <see cref="Log"/>.
        /// </summary>
        public TimeSpan? StatusInterval { get; set; } = TimeSpan.FromSeconds(20);

        /// <summary>
        /// Gets or sets the TLS-auto-detect peek window (the reference
        /// <c>--tls-auto</c>, const 0.5 s): when <see cref="ServerCertificate"/>
        /// is set and this is finite, each accept peeks at the first inbound
        /// byte for this long — <c>0x16</c> (TLS ClientHello) handshakes as the
        /// TLS server, anything else (or a quiet peer) stays plaintext.
        /// Defaults to <c>Timeout.InfiniteTimeSpan</c> (disabled: explicit
        /// cert-or-plaintext).
        /// </summary>
        public TimeSpan TlsAutoDetect { get; set; } = System.Threading.Timeout.InfiniteTimeSpan;

        /// <summary>
        /// Gets or sets whether prompt loops (see <see cref="ServerShells"/>)
        /// skip the per-prompt Go-Ahead (the reference
        /// <c>--never-send-ga</c>). Defaults to <c>false</c> (send GA, which
        /// <c>SendGaAsync</c> still suppresses while SGA is in effect).
        /// </summary>
        public bool NeverSendGa { get; set; }
    }
}
