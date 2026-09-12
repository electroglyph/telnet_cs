# telnet_cs

What this is: a C# telnet server/client library, async only.

Fair warning: this is 100% GMO, non-organic clanker generated code.

This should be considered experimental for now, I will continue to add
test coverage, etc.

My development process: telnet server/client made based on a core group
of RFCs. Code reorganized and audited multiple times by AI for clarity
and correctness. After that I cloned [telnetlib3](https://github.com/jquast/telnetlib3) and had AI
port some of the missing features over. The full list is below.

Previous behavior was then aligned to telnetlib3, I trust jquast's decisions
here a lot more than my own.

I left a lot of telnetlib3's stuff out of scope, because it does A LOT.

Thanks and respect to jquast and all the other contributors of telnetlib3.

Mostly AI generated text follows:

A Telnet client **and** server library for .NET 10 (C# 14), implemented
directly from the protocol specifications: RFC 854 (base protocol, NVT,
commands, Synch), RFC 855 (option negotiation framework), RFC 1143
(negotiation state machine), plus the option RFCs 856 (binary), 857 (echo),
858 (suppress go-ahead), 859 (status), 860 (timing mark), 727 (logout),
779 (send-location), 885 (end-of-record), 1073 (window size), 1079 (terminal
speed), 1091 (terminal type), 1096 (X display), 1184 (linemode), 1372
(toggle-flow-control), 1408/1572 (environment), 2066 (charset), and 2217
(com-port, framing level). RCTE (726) is refused — obsolete.

- **Client:** `telnet_cs.Client.Client` — connect, negotiate (RFC 1143 state
  machine), read/write, login, NAWS, terminal type/speed, environment,
  linemode, charset negotiation, status/timing-mark, echo, Synch.
- **Server:** `telnet_cs.Server.TelnetServer` + `ServerSession` — accept loop
  (with TLS ClientHello sniffing), server-role negotiation, authentication
  helper, per-client options (terminal type, speed, window size, environment,
  linemode, charset), dynamic `SetTimeout`, and idle `Timeout.\r\n` notice.
- **Ported from [telnetlib3](https://github.com/jquast/telnetlib3):**
  - Negotiation behaviors — NAWS clamped to the 0–65535 wire range; terminal
    speed transported verbatim with strict validation (rounding only at the
    consumption point); TTYPE collection with LOOPMAX overflow slot, empty
    skipping, and MTTS filtering; single-active TSPEED/CHARSET requests;
    subnegotiations split across reads reassemble before dispatch; outbound
    `byte[]` data gets RFC 854 IAC doubling.
  - Environment/charset — auto TERM/LANG/COLUMNS/LINES answers, MS-telnet
    USER exclusion, force-binary on encoding-suffixed LANG/CHARSET, 4-case
    charset selection with US-ASCII fallback and TTABLE-REJECTED, deferred
    opening negotiation (ECHO and NEW-ENVIRON held back until TTYPE answers,
    skipped for MUD clients), and password echo suppression at login.
  - MUD + MCCP — GMCP/MSDP/MSSP/MSP/MXP/ZMP/ATCP/Aardwolf codecs and
    per-protocol dispatch with stores (`MsspData`, `ZmpData`, …), MTTS
    bitvector parsing, and inbound MCCP2/MCCP3 zlib decompression (refused
    over TLS, `DONT` on corrupt streams; no outbound compression).
  - Retro codecs — ATASCII, PETSCII, Atari ST, Big5-BBS (decode tables match
    telnetlib3 cell-for-cell), with strict/replace/ignore encode fallbacks
    and split-sequence-safe incremental coders.
  - Client input helpers — `Client.InputFilter` (ATASCII/PETSCII keymaps,
    longest-first sequence translation with ESC-delay hold-back) and
    `Client.LinemodeBuffer` (client-side LINEMODE EDIT: EC/EL/EW editing,
    TRAPSIG to IAC commands, forwardmask flush, CR/LF line send).
  - Server REPL shell — `Server.ServerShells.RunReplAsync` (`Ready.` banner,
    `tel:sh> ` prompt with per-prompt Go-Ahead, and
    `quit/help/version/negotiation/stats/environ` commands).
  - Runtime extras — `SendGaAsync` (SGA-aware Go-Ahead), generic
    `WaitForNegotiationAsync` / `WaitForOptionEnabledAsync` waiters,
    `Server.TelnetSessionContext` (activity timestamps, rx/tx counters,
    typescript recorder, property bag), `IdleTimeout` (default 300 s),
    `StatusInterval` (default 20 s), and opt-in `TlsAutoDetect` peek.
- 999 tests, full suite green with warnings-as-errors. Requires the
  .NET 10 SDK and runtime; build with
  `dotnet build telnet_cs.sln -c Release`.
