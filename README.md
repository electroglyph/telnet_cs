# telnet_cs

A Telnet client **and** server library for .NET 10 (C# 14), implemented
directly from the protocol specifications: RFC 854 (base protocol, NVT,
commands, Synch), RFC 855 (option negotiation framework), RFC 1143
(negotiation state machine), plus the option RFCs 856 (binary), 857 (echo),
858 (suppress go-ahead), 859 (status), 860 (timing mark), 1073 (window size),
1079 (terminal speed), 1091 (terminal type), 1184 (linemode), and 1408
(environment). See `docs/rfcs.md` for the per-RFC coverage matrix
(verbatim RFC texts in `docs/telnet-rfcs/`), `docs/client.md`,
`docs/server.md`, and `docs/wire.md` for behavior notes.

- **Client:** `telnet_cs.Client.Client` — connect, negotiate (RFC 1143 state
  machine), read/write, login, NAWS, terminal type/speed, environment,
  linemode, status/timing-mark, echo, Synch.
- **Server:** `telnet_cs.Server.TelnetServer` + `ServerSession` — accept loop,
  server-role negotiation, authentication helper, per-client options
  (terminal type, speed, window size, environment, linemode).
- 429 tests, full suite green with warnings-as-errors. Requires the
  .NET 10 SDK and runtime (pinned via `global.json`); build with
  `dotnet build telnet_cs.sln -c Release`.

## AI-generated, experimental

This codebase was largely written by an AI coding agent working from the
RFC texts: each feature was planned spec-first, implemented against the
plan, and pinned with tests. The design favors small protocol units,
explicit state machines, and wire-exact test assertions over cleverness.

It is **experimental**: protocol coverage is broad but real-world
interoperability (odd servers, raw sockets, timing edge cases) has not
been battle-tested. Review the code, run the suite (`dotnet test
telnet_cs.sln`), and verify against your peer before trusting it in
production.
