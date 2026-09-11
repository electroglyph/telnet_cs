# Client (`telnet_cs.Client`)

`Client : BaseClient, IClient` — the initiating side.

## Connecting

```csharp
// Owns its stream; dispose when done.
using var client = await Client.ConnectAsync("host", 23);
// Or wrap an existing stream (fake-friendly):
using var client2 = new Client(byteStream, CancellationToken.None);
```

The constructor (and `ConnectAsync`) blocks briefly for the TCP connect,
then sends the proactive `IAC DO SuppressGoAhead` unless constructed with
`skipProactiveNegotiation: true` or the static
`SkipProactiveOptionNegotiation` is set.

## Settings

`client.Settings` (`TelnetClientOptions`) overrides per instance; any
member left null/zero falls back to the `Client` statics
(`TerminalType`, `TerminalSpeed`, `IsWriteConsole`) or documented
defaults. Test overrides via `Settings` — the statics are process-wide
and require the `GlobalStateGuard` pattern in tests.

## Everyday I/O

```csharp
await client.WriteLineAsync("show version");          // legacy "\n"
await client.WriteLineAsync("show version", LineEnding.Crlf); // RFC 854
string s = await client.TerminatedReadAsync(">", TimeSpan.FromSeconds(5));
bool ok = await client.TryLoginAsync("user", "pass", 5000);
```

`ReadAsync` returns whatever arrived within the rolling timeout;
cancellation returns the partial text, never throws. `TerminatedReadAsync`
polls reads until a string/collection/regex terminator matches (see
`BaseClient` matchers) and logs non-matches via `Settings.Log`.

## Negotiation and options

- `RequestEnableAsync` / `RequestDisableAsync` (`IAC DO`/`DONT`) and
  `OfferEnableAsync` semantics backed by a persistent `NegotiationState`:
  repeats and refused options stay silent; an explicit call is new
  stimulus.
- `SendTimingMarkAsync` for the RFC 860 round-trip; `RefreshWindowSizeAsync`
  resends NAWS when the size changed; `ImportRemoteSpecialCharactersAsync` /
  `ExportSpecialCharactersAsync` for LINEMODE SLC; `SendCommand` for the
  standalone control verbs (negotiation verbs throw); `SendSynchAsync`
  needs a real `TcpByteStream`.
- `MaybeSendEnvironmentInfoAsync` runs inside `ReadAsync`: changed
  `USER`/`DISPLAY`/`USERVAR` values go out as ENVIRON INFO while the peer
  stays WILL-ing.
