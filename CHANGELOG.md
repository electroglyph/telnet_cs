# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/): `Added` /
`Changed` / `Fixed` per release, with wire, preset, and default changes
called out explicitly. Behavioral reviews start from this file plus the
green gates, not from member-name diffing.

## [0.11.0] - 2026-09-16

### Added

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
