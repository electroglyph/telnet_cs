# telnet_cs

What this is: a C# telnet server/client library, async only.

Fair warning: this is 100% GMO, non-organic clanker generated code.

This should be considered experimental for now, I will continue to add
test coverage, etc.

My development process: telnet server/client made based on a core group
of RFCs. Code reorganized and audited multiple times by AI for clarity
and correctness. After that I cloned [telnetlib3](https://github.com/jquast/telnetlib3) and had AI
port some of the missing features over. The full list is below.

I left a lot of telnetlib3's stuff out of scope, because it does A LOT.

Thanks and respect to jquast and all the other contributors of telnetlib3.

Mostly AI generated text follows:

A Telnet client **and** server library for .NET 10 (C# 14), implemented
directly from the protocol specifications: RFC 854 (base protocol, NVT,
commands, Synch), RFC 855 (option negotiation framework), RFC 1143
(negotiation state machine), plus the option RFCs 856 (binary), 857 (echo),
858 (suppress go-ahead), 859 (status), 860 (timing mark), 1073 (window size),
1079 (terminal speed), 1091 (terminal type), 1184 (linemode), and 1408
(environment).

- **Client:** `telnet_cs.Client.Client` — connect, negotiate (RFC 1143 state
  machine), read/write, login, NAWS, terminal type/speed, environment,
  linemode, status/timing-mark, echo, Synch.
- **Server:** `telnet_cs.Server.TelnetServer` + `ServerSession` — accept loop,
  server-role negotiation, authentication helper, per-client options
  (terminal type, speed, window size, environment, linemode).
- **Ported from [telnetlib3](https://github.com/jquast/telnetlib3):**
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
- 429 tests, full suite green with warnings-as-errors. Requires the
  .NET 10 SDK and runtime; build with
  `dotnet build telnet_cs.sln -c Release`.
