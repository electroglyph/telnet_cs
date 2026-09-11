# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

### Changed — namespaces (breaking, no shims)

- Library types moved from the flat `telnet_cs` namespace into feature
  namespaces; the assembly name is still `telnet_cs`:
  - `telnet_cs.Client`: `Client`, `BaseClient`, `TelnetClientOptions`,
    `IClient`, `IBaseClient`.
  - `telnet_cs.Server`: `ServerSession`, `TelnetServer`,
    `TelnetServerOptions`.
  - `telnet_cs.Transport`: `TcpByteStream`, `TcpClient`, `NetworkStream`,
    `IByteStream`, `ISocket`, `INetworkStream`.
  - `telnet_cs.Protocol`: `Commands`, `Options`, `NegotiationState`,
    `LinemodeState`, `LinemodeProtocol`, `EnvironmentProtocol`,
    `NawsProtocol`, `StatusProtocol`, `TerminalSpeedProtocol`,
    `TerminalTypeCycler`.
  - `telnet_cs.IO`: `ByteStreamHandler`, `IByteStreamHandler`,
    `ByteStringConverter`.
- No compatibility shims: the library is pre-1.0 with no external
  consumers, so all consumers update `using` directives to the namespaces
  above.
- `Guard` (internal) is deleted; call sites use
  `ArgumentNullException.ThrowIfNull`.

### Added — server session contract

- New `telnet_cs.Server.IServerSession` (implemented by `ServerSession`),
  mirroring `IClient` for shared I/O/negotiation plus server-only
  operations (opening preset, authentication, terminal/environment
  requesters, LINEMODE server ops, urgent receive). `RefreshWindowSizeAsync`
  stays client-only, `AuthenticateAsync` stays server-only by design.
- New `telnet_cs.Client.LineEnding` enum (`Lf`/`Crlf`) with
  `WriteLineAsync` overloads on `Client`, `ServerSession`, `IClient` and
  `IServerSession`. The string overloads keep their `"\n"` default:
  wire behavior is unchanged unless callers opt into `Crlf`.

### Changed — unified cancellation (breaking)

- `Client.WriteAsync` (both), `SendCommand`, `SendSynchAsync`,
  `RequestEnableAsync`, `RequestDisableAsync` and `SendTimingMarkAsync`
  now take `CancellationToken cancellationToken = default`, like their
  `ServerSession` counterparts. The token is linked with the instance
  cancellation, so disposal still aborts pending operations.
- `IClient` gains the same parameters plus `NegotiationState Negotiation`.

### Changed — names (breaking)

- `Options.SupressLocalEcho` typo fixed to `SuppressLocalEcho`.
- `Options.STARTTLS` → `StartTls`, `PRAGMA_LOGON` → `PragmaLogon`,
  `SSPI_LOGON` → `SspiLogon`, `PRAGMA_HEARTBEAT` → `PragmaHeartbeat`.
- `Commands.Data` → `DataMark` (it is the Synch data-mark octet, sent
  out-of-band, never a data byte). `Commands.Dont` is kept: it matches
  RFC 854 DONT and stays symmetric with `Wont`.
- `SendCancel()` (protected, cancels pending reads, sends nothing) →
  `CancelPendingReads()` on `BaseClient` and `ByteStreamHandler`.
- `ByteStreamHandler.LinemodeServerRole` (`bool`) →
  `LinemodeRole` (`telnet_cs.Protocol.LinemodeRole` enum with
  `Client`/`Server`; default `Client` preserves behavior).
- `ByteStreamHandler` hydration properties (`Negotiation`,
  `TerminalType`, `TerminalSpeed`, `IsWriteConsole`, `AllowRemoteEcho`,
  `EnableBell`, `TextEncoding`, `WindowWidth`/`Height`, `Log`,
  environment members, `Trace`) are now `internal`: configure them
  through `Client.Settings`/`TelnetServerOptions`, not the handler.

### Removed (breaking)

- The obsolete `Client(string hostname, int port, CancellationToken)`
  constructor. Use `Client.ConnectAsync` (owns its stream) or construct
  a `Transport.TcpByteStream` first.
- `GuardGapTests` and the `Guard` coverage pin: `Guard` is gone, and its
  null-guard behavior is still pinned by the constructor tests.
- The duplicate `AsyncTcpByteStreamTests` relay test (kept the
  `TcpByteStreamTests` copy).
