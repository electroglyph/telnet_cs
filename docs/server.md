# Server usage guide

Last verified: 2026-09-16 (suite 1593/1593 green).

The server lives in the `telnet_cs.Server` namespace. `TelnetServer` owns
only the listen socket; each accepted connection is a `ServerSession`
(which derives from `Client.BaseClient`, so the read/write/waiter API
mirrors the client). Options are carried by `TelnetServerOptions`.
I/O is async-only; construction, disposal, and a few inspectors are sync.

## Starting and accepting

```csharp
using telnet_cs.Server;

var options = new TelnetServerOptions
{
    Log = Console.WriteLine,
    IdleTimeout = TimeSpan.FromMinutes(5),
};
using var server = new TelnetServer(port: 2323, options);
server.Start();   // port 0 = ephemeral; read back server.Port afterwards

while (true)
{
    ServerSession session;
    try
    {
        session = await server.AcceptSessionAsync(ct);
    }
    catch (InvalidOperationException ex)
    {
        // Over-capacity / filter reject (socket already disposed), or the
        // listener was never started. Log and keep accepting.
        Console.WriteLine(ex.Message);
        continue;
    }
    catch (TimeoutException ex)
    {
        // TLS handshake or opening preset exceeded HandshakeTimeout
        // (socket already disposed). Log and keep accepting.
        Console.WriteLine(ex.Message);
        continue;
    }
    _ = HandleAsync(session);   // AcceptSessionAsync already sent the opening preset
}

async Task HandleAsync(ServerSession session)
{
    using (session)
    {
        if (!await session.AuthenticateAsync(ValidateAsync, TimeSpan.FromSeconds(30)))
            return;
        await ServerShells.RunReplAsync(session, ct);
    }
}

Task<bool> ValidateAsync(string user, string password) =>
    Task.FromResult(user == "admin" && password == "secret");
```

To split admission from negotiation (inspect the peer before the first
preset byte), use `AcceptTcpAsync` + `NegotiateAsync` — the same steps
`AcceptSessionAsync` runs, so the wire behavior is identical:

```csharp
var pending = await server.AcceptTcpAsync(ct);
Console.WriteLine($"incoming from {pending.RemoteEndPoint}");
var session = await server.NegotiateAsync(pending, ct);
```

`Stop()` stops the listener and the status timer, closes accepted
sessions, and clears the bound port (`Start()` re-arms afterwards);
`Dispose()` does all that, latches disposed (further `Start()` throws),
and additionally completes the accept queue. `Settings` is kept by reference,
so most scalar options are read live (per read/accept), but a few are
snapshotted per session — the table below marks which is which. Mutating
`Settings` after `Start()` is supported; only the snapshotted rows apply
to subsequently accepted sessions.

The opening preset is just `DO TTYPE` (when `RequestTerminalType`, on by
default). It is sent by `AcceptSessionAsync` before it returns. To silence
all server negotiation at once, set `DisableAllNegotiation`: the opening
preset sends nothing, the advanced preset / TTYPE probe / deferred
ECHO+NEW-ENVIRON below are suppressed regardless of per-flag values, and
inbound peer negotiation is silently ignored (no answers, no errors).
Per-feature flags stay for progressive enablement.

Everything else follows in the advanced preset once negotiation advances
(any option enabled on either side; a peer that refuses everything gets
nothing further; fires at most once). Flags on `TelnetServerOptions` default
to requesting TTYPE, TSPEED (never sent unsolicited — request explicitly),
NAWS, old ENVIRON (never sent unsolicited — request explicitly), new ENVIRON
(deferred, never in the opening preset), and CHARSET, and offering ECHO/SGA/Binary; XDisplay,
Linemode, SendLocation, and MCCP offers (`OfferMccp2`/`OfferMccp3`) default
off, while passive MCCP accept (`EnableMccp`) defaults on. Once negotiation
advances, the server sends `WILL SGA`, `WILL BINARY`, `DO NAWS`, `DO CHARSET`
— each gated by its own flag — plus `DO LINEMODE`
only if requested and `WILL MCCP2/3` only if offered and not over TLS.
TSPEED auto-probes after the peer `WILL`s it, but explicit
`RequestTerminalSpeedAsync` sends unconditionally; old ENVIRON is never
requested unsolicited. `WILL ECHO` and `DO NewEnviron` are deferred until
after TTYPE answers (or suppressed entirely by `DisableAllNegotiation`; an
`"ANSI"` first answer defers NEW_ENVIRON past the
second report; `WONT`/timeout still need the advance gate; ECHO needs
`OfferEcho`, NewEnviron needs `RequestNewEnvironment` plus no volunteered
peer `WILL`) — and ECHO is suppressed entirely for MUD clients so
password-mode rendering doesn't break.

## Reading and writing

`ServerSession` reads mirror the client: `ReadAsync()` (100 ms slice,
`""` on timeout/cancel/EOF), `TerminatedReadAsync` overloads (string,
regex, multi-terminator), and the `WaitForNegotiationAsync` /
`WaitForOptionEnabledAsync` waiters. Writes:

```csharp
await session.WriteLineAsync("Welcome");        // + "\r\n" (RFC 854)
await session.WriteAsync(rawBytes);             // byte[] gets IAC doubling
await session.SendGaAsync();    // sends nothing, returns false while SGA agreed; SendCommand(GoAhead) is suppressed while our WILL Suppress-GA holds (never IAC NOP)
```

`AuthenticateAsync` prompts with `LoginUserPrompt`/`LoginPasswordPrompt` and
retries up to `MaxLoginAttempts`. Neither credential line is echoed: the read
path never replays inbound bytes (agreed ECHO is negotiation state only), so
password secrecy is structural and the username line stays silent too — a
session that wants remote echo writes the input bytes back itself. It returns `false` on exhausted attempts or a
timed-out credential line — the session stays open unless
`DisconnectOnExhaustion` is set (writes `\r\nLogin failed.\r\n`, then
closes; default `false`). Between attempts it waits `LoginAttemptDelay`
(default 1 s; zero disables) so online guessing is throttled, and each
failed attempt is logged as `auth-exhausted: <endpoint> attempt <n> <reason>`
(`bad-credentials` / `timeout` / `exhausted`; never the secrets).
The background pump stands down for the whole exchange, so typed input is
never double-consumed. A credential buffer past `MaxTerminatedReadChars`
(64 KiB default, `0` disables) throws `InvalidOperationException` and cancel
throws `OperationCanceledException` instead of returning `false`.

## Resource limits

Unauthenticated input is bounded; defaults preserve existing wire
behavior and new limits only close or refuse, never alter framing:

| Option | Default | Snapshot / live | Effect on breach |
|---|---|---|---|
| `MaxConcurrentSessions` | 256 (0 = unlimited) | live, per accept | TCP close before any TLS handshake or preset bytes; throws `SessionCapacityException`; `RejectedCapacityCount++`, `over-capacity` log |
| `MaxConnectionsPerIp` | 16 (0 = unlimited) | live, per accept | TCP close before any TLS handshake or preset bytes; throws `PerIpCapacityException`; `RejectedPerIpCount++` |
| `AcceptFilter` | null (allow all) | live, per accept | TCP close before any TLS handshake or preset bytes; throws `ConnectionRefusedByFilterException` (a throwing filter also refuses, inner preserved); `RejectedFilterCount++`. Wins over `AcceptFilterV2` when both are set |
| `AcceptFilterV2` | null (no verdict) | live, per accept | Same close/throw/counter as `AcceptFilter`, but the refuse reason travels in the `over-capacity: filter-reject endpoint=…` log line and the exception; consulted only when `AcceptFilter` is null |
| `HandshakeTimeout` | 10 s (Infinite/`<= 0` disables) | snapshot per accept | `\r\nHandshake timeout.\r\n`, then close; `handshake-timeout` log |
| `IdleTimeout` | 300 s (Infinite/`<= 0` disables) | snapshot per session | `\r\nTimeout.\r\n`, then close |
| `MaxBufferedTextChars` | 65536 (0 = unlimited) | live, per read | close; `buffer-cap:` log (pump + pending text combined) plus the `OnBufferCap` hook with the same values, at most once per accumulation |
| `OnBufferCap` | null (log only) | live, per fire | informational `BufferCapEvent` (`EndPoint`, `Buffered`, `Cap`) alongside the `buffer-cap:` log; exceptions swallowed to `Debug`; must not call back into the session |
| `MaxReplLineLength` | 4096 (0 = unlimited) | live, per line | `\r\nLine too long.\r\n`, then close; `buffer-cap:` log |
| `MaxEnvironVars` / `MaxEnvironValueChars` / `MaxEnvironKeyChars` | 128 / 4096 / 256 (0 = unlimited) | live, per frame | extras dropped, first-N-wins; `environ-cap:` log (rate-limited) |
| `MaxTtypeChars` | 256 (0 = unlimited) | live, per frame | overlong TTYPE answer dropped; `environ-cap:` log |
| `MaxMudListItems` / `MaxMudListBytes` / `MaxMudKeys` | 128 / 256 KiB / 128 (0 = unlimited) | live, per frame | over-cap MSDP/GMCP ignored pre-decode, lists trimmed oldest-first; `mud-cap:` log |
| `MaxDecompressedBytes` | 256 KiB (0 = unlimited) | live, per inflate | MCCP stream failed; `mccp-output-cap:` log |
| `MaxDecompressionRatio` | 100 (0 = unlimited) | live, per inflate | fails past the 64 KiB out / 1 KiB in floors |
| `MaxCompressedBytes` | 8 MiB (0 = unlimited) | live, per feed | MCCP stream failed |
| `LoginAttemptDelay` | 1 s (zero disables) | live, per attempt | delay between auth attempts (cancellable) |
| `DisconnectOnExhaustion` | false | live, per auth | close with `\r\nLogin failed.\r\n` when attempts exhaust |

The negotiation storm guard is fixed at 100 inbound negotiation
frames/second: past that, duplicate/refused replies are dropped
(`storm-guard:` log) while agreements and TTYPE replies are never
suppressed. The `WaitForClientAsync` queue is bounded (1000,
`DropWrite`); overflows count in `QueueDroppedCount`.

### Stable log codes

The following `Settings.Log` prefixes are the stable observability
contract: `over-capacity:`, `handshake-timeout:`, `buffer-cap:`,
`storm-guard:`, `auth-exhausted:`, `environ-cap:`, `mud-cap:`,
`mccp-output-cap:`. Downstream log parsing may key on these prefixes;
their `key=value` hyphen-space shape stays byte-stable.
`StatusInterval` (default 20 s, null or `<= 0` disables, needs `Log` or it
stays silent) logs per-session `rx/tx/idle/tls` lines only when counters
changed, plus aggregate `sessions=` / `rejected` counts. Volume caveat:
the status aggregate logs *every* tick while per-session detail lines fire
on change only; idle logged sessions also emit per-pass `Timeout exceeded`
+ pump/frame chatter, so volume assertions tolerate chatter and only pin
the aggregate line.

## Querying the client

Request methods actively poll the peer; passive properties reflect whatever
has arrived so far:

```csharp
IReadOnlyList<string> types = await session.RequestTerminalTypesAsync(TimeSpan.FromSeconds(5));
string? speed = await session.RequestTerminalSpeedAsync(TimeSpan.FromSeconds(5));
IReadOnlyDictionary<string, string> env = await session.RequestEnvironmentAsync(TimeSpan.FromSeconds(5));
IReadOnlyDictionary<string, string> newEnv = await session.RequestNewEnvironmentAsync(TimeSpan.FromSeconds(5));
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
await session.SendForwardMaskAsync(mask);          // up to 32 octets (throws beyond)
await session.PublishSpecialCharactersAsync();     // SLC SET_LOCAL
await session.RequestRemoteSpecialCharactersAsync();
await session.SendLineflowModeAsync(restartOnAny: true);   // false unless the peer WILLed LFLOW
```

## Sessions, context, and timeouts

Each session carries a `Context` (`TelnetSessionContext`): `ConnectedAtUtc`,
`LastActivityUtc`/`Idle` (any inbound wire, even IAC-only, stamps activity; transmits never do),
`CharsReceived`/`CharsSent` (raw wire bytes, negotiation frames included),
an optional `Typescript` writer that records inbound text chunks only (writes are never recorded), and a
`Properties` bag for your own per-session state.

Idle handling: `IdleTimeout` (default 300 s; `InfiniteTimeSpan` or `<= 0`
disables) writes `\r\nTimeout.\r\n` and closes the session. `SetTimeout`
overrides it per session and re-arms the timer (it never stamps activity, so the
deadline stays `LastActivityUtc + Timeout`);
`IsIdleTimedOut` latches after a fire. `StatusInterval` (default 20 s,
null or `<= 0` disables, needs `Log` or it stays silent) logs per-session `rx/tx/idle/tls` lines
only when counters changed.

## TLS

Set `ServerCertificate` for implicit TLS before the preset. With
`TlsAutoDetect` set to a finite window plus a certificate, the server peeks
at the first byte (`0x16` = handshake, else plaintext) instead of
committing. MCCP is always refused over TLS.

For rotation without restart, set `GetServerCertificate`: it is invoked on
every TLS handshake and its return is used for that handshake, so swapping
the certificate it returns rotates the next handshake with no restart. A
null return falls back to `ServerCertificate` (rotation by mutating
`ServerCertificate` keeps working); a throwing callback fails that
handshake like any failed handshake. The callback must be thread-safe —
concurrent accepts invoke it concurrently.

## Testing without sockets

`telnet_cs.Transport.InMemoryPipe.Create()` returns two linked
`IByteStream` ends with real blocking semantics and no sockets or ports:
bytes written on one end arrive on the other, and `Close` propagates
end-of-stream to the peer. Drive a `ServerSession` on one end and a
`Client` on the other to pin negotiation, reads, and writes
hermetically:

```csharp
var (clientStream, serverStream) = InMemoryPipe.Create();
using var session = new ServerSession(serverStream, new TelnetServerOptions(), ct);
using var client = new Client(clientStream, ct);
```

Reserve loopback for what memory cannot prove: the TCP accept lifecycle,
the TLS handshake, urgent data, and per-IP accounting.

## The REPL shell

`ServerShells.RunReplAsync(session, ct)` runs a small diagnostic shell:
`Ready.` banner (`Ready (secure: TLS).` over TLS), `tel:sh> ` prompt, and
`quit/help/version/negotiation/stats/environ/slc` commands (plus
`no such command.` for anything else; `quit` also writes `Goodbye.`). Set `NeverSendGa = true` to skip the
per-prompt Go-Ahead. It is a starting point, not a framework — write your
own loop for anything real.

## Gotchas

- The background pump answers negotiation and buffers text even when the
  caller never reads: 50 ms wire slices once quiet past 50 ms (20 ms
  recheck while standing down; first pass exempt so connect-time bytes are
  answered immediately), 20/100/500 ms idle backoff, under the same
  `ReadRateLimit` as reads. It stands down during active reads and for the
  whole `AuthenticateAsync` exchange. Pump-buffered text merges into the
  next `ReadAsync` (never lost, never duplicated); a stashed pump wire
  error rethrows only on an otherwise-empty `ReadAsync`. Only live wire
  errors are stashed (`OperationCanceled` / `Socket` / `ObjectDisposed`
  outcomes are never stashed); dead-peer/disposed outcomes never stash.
- `Request*Async` collectors are not re-entrant: don't call them
  concurrently on one session.
- `SendSynchAsync` / `ReceiveUrgentAsync` need a real `TcpByteStream`.
- Plain `ReadAsync` never throws for no data (`""`); `TerminatedReadAsync`
  throws `TimeoutException` on a missed deadline, `InvalidOperationException`
  past `MaxTerminatedReadChars`, and `OperationCanceledException` on cancel (plus a
  stashed pump wire error rethrows). `AuthenticateAsync` only fails closed
  (`false`) on a timed-out line.
- `TextEncoding` defaults to UTF-8; a negotiated CHARSET can
  override the read encoding per session.
