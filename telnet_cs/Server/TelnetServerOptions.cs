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
        /// Gets or sets the master negotiation switch. When <c>true</c>, the
        /// opening preset sends nothing and the advanced preset, the TTYPE /
        /// TSPEED / XDisplay probes, and deferred ECHO / NEW-ENVIRON are all
        /// suppressed regardless of the per-flag values below; inbound peer
        /// negotiation is silently ignored (no answers, no errors). Defaults
        /// to <c>false</c>. Per-feature flags stay for progressive
        /// enablement, and every future default-on option must honor this.
        /// </summary>
        public bool DisableAllNegotiation { get; set; }

        /// <summary>
        /// Gets or sets whether sessions offer <c>WILL ECHO</c> (RFC 857) in the
        /// opening preset. Agreement is negotiation state only: the read path
        /// never replays inbound bytes, so a session that wants remote echo
        /// writes the bytes back itself. Defaults to <c>true</c>.
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
        /// Defaults to UTF-8 (pre-1.0 change from the legacy Latin-1 null
        /// default, matching <see cref="TelnetClientOptions.TextEncoding"/>).
        /// Null keeps the legacy Latin-1 mapping. Agreed CHARSET still
        /// overrides per session.
        /// </summary>
        public Encoding? TextEncoding { get; set; } = Encoding.UTF8;

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

        /// <summary>
        /// Gets or sets the maximum concurrently tracked sessions
        /// (reservations plus live sessions after pruning dead weak refs and
        /// disconnected records). Over budget accepts are refused before any
        /// TLS handshake or preset bytes: the socket is disposed, a rejected
        /// counter bumps, and <c>over-capacity:</c> is logged. Defaults to
        /// 256; <c>0</c> means unlimited. Negative values are rejected at
        /// <c>Start</c>. Counted per accept; the live value is read
        /// under the server lock.
        /// </summary>
        public int MaxConcurrentSessions { get; set; } = 256;

        /// <summary>
        /// Gets or sets the maximum concurrent sessions per remote IP
        /// address (parsed <c>IPEndPoint.Address</c> normalized with
        /// <c>MapToIPv4()</c>; null or unparsable endpoints share a single
        /// unknown bucket). Counted with the same prune as
        /// <see cref="MaxConcurrentSessions"/> and refused the same way
        /// before any TLS handshake or preset bytes. Many legitimate clients
        /// behind one NAT address share this budget. Defaults to 16;
        /// <c>0</c> means unlimited. Negative values are rejected at
        /// <c>Start</c>.
        /// </summary>
        public int MaxConnectionsPerIp { get; set; } = 16;

        /// <summary>
        /// Gets or sets an optional accept filter evaluated after accept and
        /// before any TLS handshake or preset bytes, with the raw remote
        /// endpoint. Null (the default) allows everything. A <c>false</c>
        /// return or a thrown exception disposes the accepted socket with no
        /// bytes sent, bumps the filter-reject counter, logs
        /// <c>over-capacity:</c> filter-reject, and throws
        /// <see cref="ConnectionRefusedByFilterException"/> out of the accept
        /// path (a filter-thrown exception is preserved as
        /// <c>InnerException</c>). When both this and
        /// <see cref="AcceptFilterV2"/> are set, this <c>bool</c> filter wins:
        /// a <c>true</c> verdict accepts without consulting V2.
        /// </summary>
        public Func<System.Net.EndPoint?, bool>? AcceptFilter { get; set; }

        /// <summary>
        /// Gets or sets an optional accept filter that returns its own refuse
        /// reason, evaluated at the same point as <see cref="AcceptFilter"/>
        /// (after accept, before any TLS handshake or preset bytes). Null
        /// (the default) means no V2 verdict. A refusing verdict disposes the
        /// accepted socket with no bytes sent, bumps the filter-reject
        /// counter, surfaces the decision reason in the
        /// <c>over-capacity: filter-reject endpoint=…</c> log line, and throws
        /// <see cref="ConnectionRefusedByFilterException"/> carrying that
        /// reason (an empty reason normalizes to <c>filter-reject</c>). A
        /// thrown exception refuses with reason <c>filter-threw</c> and the
        /// throw preserved as <c>InnerException</c>. Consulted only when
        /// <see cref="AcceptFilter"/> is null.
        /// </summary>
        public Func<System.Net.EndPoint?, AcceptDecision>? AcceptFilterV2 { get; set; }

        /// <summary>
        /// Gets or sets the pre-auth handshake deadline: the clock starts at
        /// accept-loop entry (bounding TLS sniff and handshake) and the
        /// session construction snapshots the remaining deadline. Completes
        /// on the first successful <c>AuthenticateAsync</c> or the first
        /// inbound decoded text byte; negotiation-only frames neither satisfy
        /// nor reset it. On fire a short bounded notice is best-effort
        /// written, then the stream closes (same close shape as the idle
        /// timeout) and <c>handshake-timeout:</c> is logged. Human think time
        /// before the first keystroke can exceed this; anonymous REPL shells
        /// rely on the first keystroke arriving in time. Defaults to 10
        /// seconds; <c>Timeout.InfiniteTimeSpan</c> or any non-positive span
        /// disables. Snapshotted per accept: later mutations affect
        /// subsequently accepted sessions only.
        /// </summary>
        public TimeSpan HandshakeTimeout { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>
        /// Gets or sets the maximum buffered inbound text chars
        /// (<c>pumpBufferedText + PendingText</c> combined, checked under
        /// the pump lock after each pump append). On exceed the session is
        /// closed fail-closed (never silently drop authenticated data) and
        /// <c>buffer-cap:</c> is logged. The pump append path holds the
        /// pump lock while operator-side <c>PendingText</c> appends occur
        /// outside it, so enforcement for those appends is delayed to the
        /// next check, not missed. Enforcement is post-hoc per wire pass
        /// (the whole pass is appended before the check), so a flood can
        /// transiently lodge about one pass before the session closes.
        /// Defaults to 65536; <c>0</c> means
        /// unlimited. Negative values are rejected at <c>Start</c>.
        /// Read live per append.
        /// </summary>
        public int MaxBufferedTextChars { get; set; } = 65536;

        /// <summary>
        /// Optional hook invoked alongside the <c>buffer-cap:</c> log line
        /// when buffered inbound text passes <c>MaxBufferedTextChars</c>
        /// (pump appends and collector stash-backs alike). Informational
        /// only, carrying the same values the log line carries: the
        /// fail-closed close is unchanged and there is no silent-drop mode.
        /// Fires at most once per buffered-text accumulation, never per byte
        /// over. Exceptions are swallowed to <c>Debug</c>; the callback runs
        /// on the pump/collector path and must not call back into the
        /// session. Read live per fire.
        /// </summary>
        public Action<BufferCapEvent>? OnBufferCap { get; set; }

        /// <summary>
        /// Gets or sets the maximum REPL line length in chars (see
        /// <see cref="ServerShells"/>). Pipelined bytes stashed for future
        /// lines are not charged against the current line. Checked after
        /// backspace handling on every appended chunk without a newline. On
        /// exceed a pinned notice is sent, the session closes, and pending
        /// bytes are cleared so no over-cap bytes leak into the next line.
        /// Enforcement is post-hoc per chunk (the whole chunk is appended
        /// before the check), so one overlong chunk is transiently buffered
        /// first. Defaults to 4096; <c>0</c> means unlimited. Read live per line.
        /// </summary>
        public int MaxReplLineLength { get; set; } = 4096;

        /// <summary>
        /// Gets or sets the maximum terminated-read length in chars (see
        /// <c>TerminatedReadAsync</c>): a buffer past this without the
        /// terminator throws <c>InvalidOperationException</c> naming the
        /// limit (the C# analog of the reference
        /// <c>LimitOverrunError</c>). The overlong line is consumed, the
        /// session survives, and the next line reads clean. Defaults to
        /// 65536 (the historical 64 KiB reference limit); <c>0</c> means
        /// unlimited. Negative values are rejected at <c>Start</c>. Read
        /// live per read (a per-session <c>ServerSession</c> override wins).
        /// </summary>
        public int MaxTerminatedReadChars { get; set; } = 65536;

        /// <summary>
        /// Gets or sets the maximum distinct ENVIRON variables stored per
        /// dictionary (old and new forms counted separately). Duplicates
        /// within budget update in place last-wins without consuming budget;
        /// the first N distinct keys win and extras are ignored plus counted
        /// and logged (truncated, sanitized key names only, never values).
        /// Defaults to 128; <c>0</c> means unlimited.
        /// </summary>
        public int MaxEnvironVars { get; set; } = 128;

        /// <summary>
        /// Gets or sets the maximum ENVIRON value length in chars. Overlong
        /// values leave the stored entry untouched and are ignored plus
        /// logged. Defaults to 4096; <c>0</c> means unlimited.
        /// </summary>
        public int MaxEnvironValueChars { get; set; } = 4096;

        /// <summary>
        /// Gets or sets the maximum ENVIRON key length in chars. Overlong
        /// keys are ignored leaving stored entries untouched, plus logged
        /// with truncated sanitized names. Defaults to 256; <c>0</c> means
        /// unlimited.
        /// </summary>
        public int MaxEnvironKeyChars { get; set; } = 256;

        /// <summary>
        /// Gets or sets the maximum single TTYPE answer length in chars.
        /// Overlong answers are ignored (chain untouched) plus logged.
        /// The answer chain itself hard-stops at 9 entries, each bounded by
        /// the subnegotiation cap rather than this one, so <c>0</c> retains
        /// up to about 9 MiB per session. Defaults to 256; <c>0</c> means
        /// unlimited.
        /// </summary>
        public int MaxTtypeChars { get; set; } = 256;

        /// <summary>
        /// Gets or sets the maximum items kept per MUD append list
        /// (MSP, MXP, Aardwolf, ATCP). Keeps last-N with drop-oldest plus a
        /// counter, logged once per window with counts only. Defaults to
        /// 128; <c>0</c> means unlimited.
        /// </summary>
        public int MaxMudListItems { get; set; } = 128;

        /// <summary>
        /// Gets or sets the maximum total bytes kept per MUD append list
        /// (same lists as <see cref="MaxMudListItems"/>). Keeps last-N bytes
        /// with drop-oldest. Defaults to 256 KiB; <c>0</c> means unlimited.
        /// </summary>
        public int MaxMudListBytes { get; set; } = 256 * 1024;

        /// <summary>
        /// Gets or sets the maximum distinct ZMP commands stored (the ZMP
        /// map replaces per command and would otherwise grow across frames).
        /// Over-cap commands are ignored plus logged. MSSP is replaced
        /// wholesale per frame so it cannot grow across frames, but its
        /// per-frame var count is still capped for parse cost. Defaults to
        /// 128; <c>0</c> means unlimited.
        /// </summary>
        public int MaxMudKeys { get; set; } = 128;

        /// <summary>
        /// Gets or sets the maximum outstanding decompressed bytes queued in
        /// the MCCP decompressor. A continuous MCCP stream legitimately
        /// exceeds any lifetime total over hours, so this bounds queued
        /// output, not lifetime output. On exceed the queue clears, the
        /// stream marks failed, and the existing corrupt path runs
        /// (DONT/WONT plus resume plaintext) with <c>mccp-output-cap:</c>
        /// logged. Defaults to 256 KiB; <c>0</c> means unlimited.
        /// </summary>
        public int MaxDecompressedBytes { get; set; } = 256 * 1024;

        /// <summary>
        /// Gets or sets the maximum decompression ratio (decompressed per
        /// compressed byte) enforced only after floors (64 KiB decompressed
        /// and 1 KiB compressed) so tiny legit streams never false-positive.
        /// Defaults to 100; <c>0</c> means unlimited.
        /// </summary>
        public int MaxDecompressionRatio { get; set; } = 100;

        /// <summary>
        /// Gets or sets the maximum compressed input bytes retained by the
        /// MCCP decompressor. The input buffer is never compacted by design
        /// (end-confirmation re-examines consumed bytes), so growth is
        /// bounded by this cap instead: on exceed the stream marks failed,
        /// clears queued output, logs <c>mccp-output-cap:</c>, and the
        /// handler refuses compression and resumes plaintext. Defaults to 8
        /// MiB; <c>0</c> means unlimited.
        /// </summary>
        public int MaxCompressedBytes { get; set; } = 8 * 1024 * 1024;

        /// <summary>
        /// Gets or sets the delay awaited between failed login attempts and
        /// after the final failure (so looping <c>AuthenticateAsync</c>
        /// cannot skip the rate limit), including the credential-timeout
        /// path. Linked with the caller token plus session cancellation so
        /// shutdown never hangs; cancellation during the delay throws rather
        /// than returning <c>false</c>. Validate exceptions propagate with
        /// no delay and are not logged here. Defaults to 1 second;
        /// <c>TimeSpan.Zero</c> (or any non-positive span) disables.
        /// </summary>
        public TimeSpan LoginAttemptDelay { get; set; } = TimeSpan.FromSeconds(1);

        /// <summary>
        /// Gets or sets whether exhausted login attempts (and
        /// credential-timeout fail-closed paths) close the session. When
        /// false (the default) the session stays open and the caller decides
        /// disconnect policy. When true the close uses the same shape as the
        /// idle timeout with a pinned notice that never distinguishes
        /// user-vs-password failure.
        /// </summary>
        public bool DisconnectOnExhaustion { get; set; }
    }
}
