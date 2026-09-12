# Server usage guide

The server lives in the `telnet_cs.Server` namespace. `TelnetServer` owns
only the listen socket; each accepted connection is a `ServerSession`
(which derives from `Client.BaseClient`, so the read/write/waiter API
mirrors the client). Options are carried by `TelnetServerOptions`.
Everything is async-only.

## Starting and accepting

```csharp
using telnet_cs.Server;

var options = new TelnetServerOptions
{
    Log = Console.WriteLine,
    IdleTimeout = TimeSpan.FromMinutes(5),
    RequestLinemode = false,   // trim the opening preset if you don't need it
};
using var server = new TelnetServer(port: 2323, options);
server.Start();   // port 0 = ephemeral; read back server.Port afterwards

while (true)
{
    ServerSession session = await server.AcceptSessionAsync(ct);
    _ = HandleAsync(session);   // AcceptSessionAsync already sent the opening preset
}

async Task HandleAsync(ServerSession session)
{
    await using (session)
    {
        if (!await session.AuthenticateAsync(ValidateAsync, TimeSpan.FromSeconds(30)))
            return;
        await ServerShells.RunReplAsync(session, ct);
    }
}

Task<bool> ValidateAsync(string user, string password) =>
    Task.FromResult(user == "admin" && password == "secret");
```

`Stop()` closes only the listener (sessions are unaffected); `Dispose()`
stops the listener and the status timer. `Settings` is kept by reference,
so mutating it affects subsequently accepted sessions.

The opening preset offers ECHO/SGA/Binary and requests TTYPE, TSPEED,
NAWS, ENVIRON, and linemode by default (all flippable on
`TelnetServerOptions`). `WILL ECHO` and `DO NewEnviron` are deferred until
after TTYPE answers — and ECHO is suppressed entirely for MUD clients so
password-mode rendering doesn't break.

## Reading and writing

`ServerSession` reads mirror the client: `ReadAsync()` (100 ms slice,
`""` on timeout/cancel/EOF), `TerminatedReadAsync` overloads (string,
regex, multi-terminator), and the `WaitForNegotiationAsync` /
`WaitForOptionEnabledAsync` waiters. Writes:

```csharp
await session.WriteLineAsync("Welcome");        // + "\r\n" (RFC 854)
await session.WriteAsync(rawBytes);             // byte[] gets IAC doubling
await session.SendGaAsync();    // NOP while SGA agreed; SendCommand(GoAhead) always sends
```

`AuthenticateAsync` prompts with `LoginUserPrompt`/`LoginPasswordPrompt`,
retries up to `MaxLoginAttempts`, and suppresses echo-back of the password
line when ECHO was offered. It returns `false` on exhausted attempts or an
unterminated line — the session stays open, you decide whether to
disconnect.

## Querying the client

Request methods actively poll the peer; passive properties reflect whatever
has arrived so far:

```csharp
IReadOnlyList<string> types = await session.RequestTerminalTypesAsync(TimeSpan.FromSeconds(5));
string? speed = await session.RequestTerminalSpeedAsync(TimeSpan.FromSeconds(5));
IReadOnlyDictionary<string, string> env = await session.RequestEnvironmentAsync(TimeSpan.FromSeconds(5));
string? charset = await session.RequestCharsetAsync(TimeSpan.FromSeconds(5));
string? location = await session.RequestSendLocationAsync(TimeSpan.FromSeconds(5));
string? xdisplay = await session.RequestXDisplayAsync(TimeSpan.FromSeconds(5));

// Passive views (no traffic):
(session.ClientWindowSize is { } ws ? $"{ws.Width}x{ws.Height}" : "unknown");
session.ClientEffectiveTerminalType;  // MTTS-aware pick from the TTYPE chain
session.ClientNewEnvironment;         // RFC 1572 form, kept separate
session.ClientEffectiveDisplay;       // last-arrived of XDisplay vs ENVIRON DISPLAY
session.MsspData; session.ZmpData; session.AardwolfData; session.AtcpData; // MUD stores
```

Linemode (server is the DO-sender):

```csharp
await session.SendModeAsync(modeMask);
await session.SendForwardMaskAsync(mask);          // up to 32 octets
await session.PublishSpecialCharactersAsync();     // SLC SET_LOCAL
await session.RequestRemoteSpecialCharactersAsync();
await session.SendLineflowModeAsync(restartOnAny: true);
```

## Sessions, context, and timeouts

Each session carries a `Context` (`TelnetSessionContext`): `ConnectedAtUtc`,
`LastActivityUtc`/`Idle`, `CharsReceived`/`CharsSent` (string paths only),
an optional `Typescript` writer that records both directions raw, and a
`Properties` bag for your own per-session state.

Idle handling: `IdleTimeout` (default 300 s; `InfiniteTimeSpan` or `<= 0`
disables) writes `\r\nTimeout.\r\n` and closes the session. `SetTimeout`
overrides it per session and restarts the countdown immediately;
`IsIdleTimedOut` latches after a fire. `StatusInterval` (default 20 s,
needs `Log` or it stays silent) logs per-session `rx/tx/idle/tls` lines
only when counters changed.

## TLS

Set `ServerCertificate` for implicit TLS before the preset. With
`TlsAutoDetect` set to a finite window plus a certificate, the server peeks
at the first byte (`0x16` = handshake, else plaintext) instead of
committing. MCCP is always refused over TLS.

## The REPL shell

`ServerShells.RunReplAsync(session, ct)` runs a small diagnostic shell:
`Ready.` banner, `tel:sh> ` prompt, and
`quit/help/version/negotiation/stats/environ` commands (plus
`Unknown command.` for anything else). Set `NeverSendGa = true` to skip the
per-prompt Go-Ahead. It is a starting point, not a framework — write your
own loop for anything real.

## Gotchas

- `Request*Async` collectors are not re-entrant: don't call them
  concurrently on one session.
- `SendSynchAsync` / `ReceiveUrgentAsync` need a real `TcpByteStream`.
- Reads never throw for no data (`""`); `AuthenticateAsync` is the
  exception that fails closed on an unterminated line.
- `TextEncoding` defaults to legacy Latin-1; a negotiated CHARSET can
  override the read encoding per session.
