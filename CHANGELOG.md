# Changelog

## [Unreleased]

## [0.19.0] - 2026-09-18

### Removed

- The blocking `Client` constructors; `Client.CreateAsync` is now the only
  construction path. No wire, preset, or default changes.

### Fixed

- `RequestTerminalTypesAsync` no longer re-requests answers the pump already
  filed, matching the reference chase-one-answer-per-request behavior.

### Changed

- All frame writes throttle through `SendFrameLockedAsync` (server-side byte
  accounting preserved); SLC triplets share one enumerator; `+` concatenation
  is now interpolation; new `!` suppressions use real guards. No wire,
  preset, or default changes.

## [0.18.0] - 2026-09-18

### Changed

- God-class splits (pure moves, no wire/preset/default changes):
  `ByteStreamHandler` into pump/parsing/negotiation/MUD/replies/linemode
  parts, `ServerSession.Collectors` into TTYPE/requester/consumer parts,
  `ServerSession.Negotiation` into terminated-read/auth parts,
  `TelnetServer` into accept/session-bookkeeping parts, and
  `MccpDecompressor` into core/raw-end-locator parts.
- Shared internal helpers replace client/server twins with identical
  output: enable/disable resolvers, the linemode state property, the
  send-throttle helper, terminator finders, SLC import/export payload and
  triplet helpers, the MSDP table encoder, and the CHARSET separator
  selector. No wire, preset, or default changes.
- `NegotiationState.GetStates(option)` is now the `this[option]` indexer
  (same lock-guarded snapshot); `ServerSession.SetTimeout` is now the
  settable `Timeout` property (setting it re-arms the timer, as before).
- `BaseClient` is renamed `TelnetSessionBase` to reflect server use; the
  `IBaseClient` contract is unchanged.
- Enforcement unification: the buffer/environ/MUD cap partials read
  through one `CapsGate` helper and are renamed `*CapEnforcement`;
  `StatusReportItem` lives in its own `ServerSession` part file.
- `MccpWriteFilter` moves to `telnet_cs.IO` alongside the MCCP
  compressor/decompressor; `DuplexEnd` is a top-level internal type.
- One-type-per-file promotions (no API changes): `LinemodeEdit`,
  `SlcEntry`, `MttsCapabilities`, `NegotiationStormGuard`.
- Allocation-only cleanups with identical bytes: span-based MSDP/MSSP/ZMP
  and CHARSET/ENVIRON parsing (the MSDP parser is a zero-copy `ref`
  struct), pooled decode buffers, collection-expression frames, and
  `SequenceEqual` input matching.
- Tests: `Wire`/`WireTap` live in `Helpers/`; login/socket suites in
  `Integration/`; shared `TerminatedReadLimitCases`; TCP relay twins and
  the empty `MudDetectorTests` shell merged away; `WriteHandler` doubles
  collapsed to a `Func<>` queue.
- Docs: new `docs/fuzz.md`; guide cross-links to the vendored
  `mud-protocols/` notes and `telnet-rfcs/` texts; echo/STATUS,
  `DuplexPipe`-vs-`InMemoryPipe`, and retro-accessory coverage.
- Catch-up for previously unlogged features (all predate this release;
  behavior unchanged): `DuplexPipe`, `TelnetEncodingProvider` codepages
  80001–80004, `InputFilter`/`LinemodeBuffer`, `SyncTermFont`,
  `MttsCapabilities`, `AardwolfMessage`, `TelnetAccessories.Hexdump`, and
  the `ServerShells` REPL commands.

## [0.17.0] - 2026-09-18

### Added

- `Client.CreateAsync`: async factory over an existing `IByteStream` that
  waits for `Connected` with `Task.Delay` and negotiates asynchronously, so
  callers never block on async work and cancellation is honoured. The
  constructors are unchanged. No wire, preset, or default changes.
- `DuplexPipe.DuplexEnd.WaitForData`: cancellable wait for peer writes/close
  for async code; the synchronous `ReadByte` keeps its blocking semantics. No
  wire, preset, or default changes.

### Changed

- Shared internal helpers replace client/server twins with identical output:
  `TelnetCommands.IsStandaloneControl` (the `SendCommand` allow-list),
  `CharsetProtocol.FrameVerb`, and the `BaseClient` terminator-cut/limit
  helpers. No wire, preset, or default changes.
- Allocation-only cleanups with identical bytes: `AsSpan` in MCCP checksum
  paths, index copies instead of LINQ `Skip` in CHARSET/LINEMODE parsing, and
  collection expressions for fixed protocol frames. No wire, preset, or
  default changes.
- Style-only: `is null` checks, interpolated messages, `field`-backed static
  properties, guarded codec/timer initialization instead of `!`, and `switch`
  expressions for charset-alias/environment selection. No wire, preset, or
  default changes.

### Fixed

- `ServerSession` latches the accepted socket's TLS flag at construction (new
  internal overload; `TelnetServer` passes it directly): the background pump's
  first pass can no longer answer pre-enqueued negotiation as plaintext (MCCP
  agreed instead of refused over TLS). No wire, preset, or default changes.

## [0.16.0] - 2026-09-17

### Removed

- `TelnetServerOptions.AcceptFilterV2`: the boolean `AcceptFilter` is gone
  and the verdict-returning filter takes its name. `AcceptFilter` is now
  `Func<EndPoint?, AcceptDecision>`; the old `EndPoint? → bool` form and the
  bool-wins precedence rule are deleted. No wire, preset, or default changes.

## [0.15.0] - 2026-09-17

### Fixed

- Handshake-deadline classification is latched to the deadline token instead
  of the wall clock (`TelnetServer.IsHandshakeTimeout`): a deadline expiry now
  maps to `TimeoutException` deterministically even when observed before the
  wall-clock deadline, and a caller cancel still surfaces as
  `OperationCanceledException`. No wire, preset, or default changes.
- Reads drain parser-held bytes after the peer closes (`ByteStreamHandler`
  only reports end-of-stream when nothing is held): a `\n` stashed by the CR
  LF continuation at end-of-stream is delivered on the next read instead of
  dropped. No wire, preset, or default changes.

### Changed

- Split-accept doc example (`TelnetServer.AcceptTcpAsync` xmldoc,
  `docs/server.md`): endpoint correlation now goes through `AcceptFilter`
  (`ServerSession.RemoteEndPoint` is internal, so the old
  `pending.RemoteEndPoint` line did not compile for consumers).

## [0.14.0] - 2026-09-17

### Fixed

- Clean peer close (TCP FIN, TLS close_notify) is now observed instead of
  ghosting the session: a definitive end-of-stream byte read closes the
  stream so `Connected` flips false and in-flight reads end at once, and a
  zero-timeout FIN probe (`Poll(SelectRead)` with nothing available)
  surfaces an idle close the gated parse loop would otherwise never read.
  Urgent (SYNCH) bytes are excluded from the probe, and queued bytes still
  drain before EOF (socket parity), so final writes such as idle-timeout
  notices and goodbye banners are never truncated. No wire, preset, or
  default changes.
- `DuplexPipe` ends no longer nest the peer lock inside the local lock
  (`Connected` and the `ReadByte` end-of-stream check snapshot instead):
  pump/reader pairs on opposite ends could ABBA-deadlock and stall
  delivery.

## [0.13.0] - 2026-09-17

### Added

- `ServerSession.SetEchoAsync` / `WriteWithEchoAsync`: game-driven ECHO
  (`IAC WILL`/`WONT ECHO` through the RFC 1143 machine; the latter fuses
  the toggle ahead of the prompt in one atomic write). The first manual
  call stands down the deferred auto-ECHO offer for the session.

### Fixed

- `DisableAllNegotiation` is now honored before the Q-machine is touched
  in `RequestEnableAsync` / `RequestDisableAsync` / the ECHO offer paths
  (previously the public `Request*` entry points emitted bytes and mutated
  negotiation state under the switch).

## [0.12.0] - 2026-09-16

### Added

- Fuzzer: new `repl` (REPL prompt loop), `request` (server collector
  requests), `tlssniff` (leading-0x16 sniff), `caps` (server option caps),
  and `storm` (negotiation storm guard) modes; behavior-hash novelty
  guidance on all targets; `--input` replay, `--jobs` parallelism,
  `--seconds` budget, `--faults` I/O-fault injection, `--no-minimize`,
  `--list-modes`, `summary.json`, and multi-round sequence minimization.

### Fixed

- Fuzzer codec oracle: GMCP round-trip check no longer flags packages
  containing spaces (unencodable by construction, split at first space).
- Fuzzer codec oracle: MSDP round-trip check no longer flags tables whose
  derived strings contain framing bytes (forbidden in values by the spec,
  re-parsed as structure on decode).

## [0.11.0] - 2026-09-16

### Added

- Fuzzer coverage: new `write` (client/server outbound writes, commands,
  negotiation requests), `term` (terminated/exact reads with fuzz
  terminators, patterns, counts), `mccp` (direct decompressor feed,
  compressor round-trip, write filter), `proto` (stateless option helpers,
  negotiation/SLC state machines, converter), and `accept` (server accept
  lifecycle plus hermetic pipe echo) modes; `codec`/`encoding` now also
  cover the encode direction. New `FuzzModeSmokeTests` pins every mode.
- NuGet autopublish: a `VersionPrefix` bump merged to `master` publishes
  to nuget.org via trusted publishing. No tags, no CI prereleases.
- Refuse-reason exception subtypes (`SessionCapacityException`,
  `PerIpCapacityException`, `ConnectionRefusedByFilterException`), all
  deriving from `InvalidOperationException`, each with `RemoteEndPoint` +
  `Reason` for mapping without message sniffing.
- `TelnetServerOptions.AcceptFilterV2`: accept filter returning its own
  refuse reason (`AcceptDecision`), surfaced in the log line and exception.
  The `bool` filter wins when both are set.
- `TelnetServerOptions.DisableAllNegotiation` (default `false`): silences
  all server negotiation output; inbound verbs ignored without reply.
- `TelnetServer.AcceptTcpAsync` + `NegotiateAsync`: split accept so
  admission (filters/caps, then TLS) runs with no negotiation bytes out
  and the opening preset goes last. `AcceptSessionAsync` is the two
  composed; disposing an unnegotiated session releases its reservation.
- `MaxTerminatedReadChars` on `TelnetServerOptions`/`TelnetClientOptions`
  (default 65536, `0` = unlimited) plus per-session/instance overrides:
  the 64 KiB terminated-read cap is now configurable, exception type and
  message shape unchanged.
- `TelnetServerOptions.OnBufferCap`: hook fired alongside the
  `buffer-cap:` log with the same values (`BufferCapEvent`), at most
  once per accumulation; exceptions swallowed, close unchanged.
- `TelnetServerOptions.GetServerCertificate`: per-handshake certificate
  callback for rotation without restart (null falls back to
  `ServerCertificate`; a throw fails that handshake).
- `telnet_cs.Transport.InMemoryPipe`: public hermetic transport pair
  (`Create()` returns two linked `IByteStream` ends, no sockets) for
  tests and adapters; loopback stays reserved for TCP/TLS/urgent/IP
  behavior.

### Changed

- **Default change (wire-visible):** `TelnetServerOptions.TextEncoding`
  now defaults to UTF-8 (was null = Latin-1). CHARSET agreement still
  overrides per session.

### Fixed

- Outbound wire encoding is strict for retro codecs with a replacement
  fallback installed: unrepresentable characters throw
  `EncoderFallbackException` for `atascii`/`petscii`/`atarist`/`big5bbs`
  instead of silently emitting `?`.
- `ServerSession.PeerStatusReport` no longer drops valid pairs after unknown
  bytes in STATUS IS: unknown single bytes are skipped and parsing continues,
  so the stored report stays complete.
- Handshake-timeout accepts released their capacity reservation twice,
  over-freeing one slot per timed-out handshake. Each accept now releases
  exactly once.
- `RequestTerminalTypesAsync` could return a one-answer chain when the
  background pump filed the opening-probe answer between the caller's last
  pump and the collect start. The already-finished shortcut now also
  requires a completed TTYPE cycle.
- An MCCP read stalled mid-stream with an empty wire consumed the next
  arriving byte raw instead of feeding the inflater, desyncing inflation
  (spurious decompression failure, refusal, and trailing garbage as text).
  A stalled read now awaits the next byte and feeds it to the inflater.
- A TTYPE collection the background pump finished could strand the
  answer-triggered WILL ECHO / DO NEW_ENVIRON: the pump correctly
  withholds them for a solicited release, but an explicit request that
  found the cycle already complete returned without any read or flush to
  release them. Completion now flushes explicitly, after every SEND.
