# Server (`telnet_cs.Server`)

`TelnetServer` owns only the listen socket; each accepted connection is a
`ServerSession : BaseClient, IServerSession` with server-role defaults.

## Accepting

```csharp
using var server = new TelnetServer(2323, new TelnetServerOptions());
server.Start();
while (true)
{
  using var session = await server.AcceptSessionAsync();
  await session.SendOpeningPresetAsync();   // WILL ECHO/SGA + DO TTYPE/TSPEED/NAWS/ENV/LINEMODE per options
  if (await session.AuthenticateAsync((u, p) => Task.FromResult(Check(u, p)), TimeSpan.FromSeconds(30)))
  {
    await session.WriteLineAsync("welcome>");
    ...
  }
}
```

`Settings` is a live reference shared with accepted sessions; mutate
before accepting. Use port `0` for an OS-assigned port, then read
`server.Port`.

## Session behavior

- Same wire engine and I/O semantics as `Client` (read/write/terminated
  reads, `SendCommand`, `SendSynchAsync`, `RequestEnableAsync` /
  `RequestDisableAsync`), with opposite role defaults: console echo off,
  bell off, `AllowRemoteEcho` follows `OfferEcho`, LINEMODE answers use
  the server (union) rules.
- Inbound TTYPE/TSPEED/ENVIRON/NAWS reports land in the requester
  collectors: `RequestTerminalTypesAsync` (repeat-terminated chain),
  `RequestTerminalSpeedAsync` (normalized), `RequestEnvironmentAsync`,
  `ClientWindowSize`.
- LINEMODE server ops: `SendModeAsync`, `SendForwardMaskAsync`,
  `PublishSpecialCharactersAsync`, `RequestRemoteSpecialCharactersAsync`;
  `ReceiveUrgentAsync` reads the Synch octet on TCP-backed streams.
- `AuthenticateAsync` prompts (`LoginUserPrompt` /
  `LoginPasswordPrompt`), reads credential lines, and retries up to
  `MaxLoginAttempts`. A line that never terminates fails closed
  (`false`); the session stays open — disconnect policy is the caller's.
  Passwords travel the agreed echo channel: suppress echo for secrets is
  future work (see the method docs).
