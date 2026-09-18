# Client usage guide

Last verified: 2026-09-18 (suite 1745/1745 green).

The client lives in the `telnet_cs.Client` namespace. The main type is
`Client` (implements `IClient`); options are carried by the
`TelnetClientOptions` record. Everything is async-only.

## Connecting

```csharp
using telnet_cs.Client;

// Plain TCP, 30 s default timeout.
using var client = await Client.ConnectAsync("mud.example.com", 4000);

// With options and cancellation.
var options = new TelnetClientOptions
{
    TerminalTypes = ["xterm-256color", "xterm"],
    TerminalSpeed = "38400,38400",
    TextEncoding = Encoding.UTF8,
    WindowWidth = 100,
    WindowHeight = 40,
};
using var client2 = await Client.ConnectAsync(
    "mud.example.com", 4000, options, cancellationToken, TimeSpan.FromSeconds(10));
```

`ConnectAsync` also does implicit TLS when `UseTls = true` (set `TlsHost`
for SNI/validation, optionally `TlsValidationCallback`,
`TlsClientCertificates`, `TlsProtocols`). There is no STARTTLS upgrade —
TLS is negotiated before the first telnet byte.

For tests or custom transports, construct over an `IByteStream` directly:

```csharp
var client = new Client(byteStream, cancellationToken);
```

The hermetic option is `telnet_cs.Transport.InMemoryPipe.Create()`, which
returns two linked ends with no sockets — hand one to the client and the
other to a `ServerSession`:

```csharp
var (clientStream, serverStream) = InMemoryPipe.Create();
using var client = new Client(clientStream, cancellationToken);
```

## Configuring answers

The client answers option queries automatically from `Settings` on the read
path — there are no polling getters for peer values. Set these before you
start reading:

| Setting | Answers |
|---|---|
| `TerminalType` / `TerminalTypes` | TTYPE `IS`, cycling the list per `SEND` (default `"unknown"`) |
| `TerminalSpeed` | TSPEED `IS` `"<tx>,<rx>"` per RFC 1079 §4, validated not verbatim (trimmed, leading zeros stripped, two ASCII-digit parts required; malformed `SEND` gets no reply). Default `"38400,38400"` |
| `WindowWidth` / `WindowHeight` | NAWS size, clamped to 0–65535; `0` is sent as-is (RFC 1073 "unspecified"), never probed from the console |
| `EnvironmentUser`, `EnvironmentDisplay`, `EnvironmentUserVars` | ENVIRON `USER`/custom vars, plus auto `TERM`/`LANG`/`COLUMNS`/`LINES`/`COLORTERM` (`LANG` is `en_US.<WebName minus "-">`, `C` when `TextEncoding` is null). `DISPLAY` is never volunteered in `SB` answers (only spontaneous `INFO` carries it) |
| `CharsetOffers` | CHARSET preference order (default `["UTF-8", "LATIN1", "US-ASCII"]`); an empty inbound offer list answers `REJECTED` (an empty own list only blocks outbound `REQUEST`s we send) |
| `XDisplayLocation`, `SendLocation` | X-DISPLAY / SNDLOC answers |
| `TextEncoding` | Decode charset plus BINARY-path encode charset (default UTF-8; null = legacy Latin-1; non-BINARY writes stay strict ASCII) and advertises `LANG=en_US.<WebName minus "-">` (`C` when null) |
| `AllowRemoteEcho` | `true` accepts the server's `DO ECHO` (default refuses). Agreement is negotiation state only — received bytes are never replayed, so an app that wants echo writes them back itself |
| `EnableMccp` | Passively accept MCCP2/3 (default on; the stack inflates inbound and compresses outbound); always refused over TLS |
| `EnableMudOptions` / `EnableComPort` | Agree MUD options (default off) / RFC 2217 framing (default on) |
| `Log`, `IsWriteConsole`, `EnableBell` | Diagnostics, console echo, BEL setting (currently no effect — BEL arrives as data) |

Global process-wide defaults (`Client.TerminalType`, `Client.TerminalSpeed`,
`Client.IsWriteConsole`) apply when the per-instance setting
is left at its default; prefer `TelnetClientOptions`.
`Client.Trace` is additive, not a fallback: client writes invoke both
`Settings.Log` and `Client.Trace`, and per-read handler logs use
`Settings.Log` only.

Spontaneous ENVIRON `INFO` updates ride the negotiated option, preferring
`NEW_ENVIRON` when agreed (what reference peers `DO`) and falling back to
`OLD_ENVIRON`.

## Reading and writing

```csharp
// Plain read: 100 ms slice, "" on timeout/cancel/EOF — never throws for no data.
string chunk = await client.ReadAsync();

// Read until a prompt appears (remainder past the terminator is kept for the
// next plain ReadAsync — don't mix the two styles on one stream carelessly).
string prompt = await client.TerminatedReadAsync("login: ", TimeSpan.FromSeconds(5));

// Regex and multi-terminator overloads exist; millisecondSpin tunes the poll.
string m = await client.TerminatedReadAsync(new Regex(@"HP:\d+"), TimeSpan.FromSeconds(5));

await client.WriteLineAsync("look");          // + "\r\n" (RFC 854)
// Explicit bare "\n" for odd peers: WriteLineAsync("look", Client.LegacyLineFeed)
await client.WriteAsync(rawBytes);            // byte[] gets RFC 854 IAC doubling
await client.SendCommand(Commands.AreYouThere);
await client.SendSynchAsync();                // TCP urgent DM (real sockets only)
```

Login is scripted explicitly (there is no login helper: prompts vary too
much to match safely — any `:`-suffixed banner line is not a prompt):

```csharp
await client.TerminatedReadAsync("login: ", TimeSpan.FromSeconds(5));
await client.WriteLineAsync("user");
await client.TerminatedReadAsync("Password: ", TimeSpan.FromSeconds(5));
await client.WriteLineAsync("secret");
```

Enable options explicitly, then wait for agreement:

```csharp
await client.RequestEnableAsync(Options.WindowSize);
bool naws = await client.WaitForOptionEnabledAsync(Options.WindowSize, local: true,
    TimeSpan.FromSeconds(5));   // false on timeout (never throws)
bool done = await client.WaitForNegotiationAsync(
    s => s.IsEnabledByUs((int)Options.OldEnvironment), TimeSpan.FromSeconds(5));
    // throws TimeoutException on a missed deadline
```

On window resize, re-announce (there is no console-resize event in .NET —
poll this yourself):

```csharp
await client.RefreshWindowSizeAsync();
```

`GoAheadReceived` fires on the read path for unsuppressed `GA`
(RFC 858 turn-taking). `SendGaAsync` sends `IAC GA` unless our own `WILL`
Suppress-GA holds (local side only; then it sends nothing and returns `false`).
`SendTimingMarkAsync` does an RFC 860 round-trip. `SendEorAsync` sends `IAC EOR`
only when the peer sent `DO EOR` (otherwise it sends nothing and returns `false`).
`Dispose()` (or `using`) is the only close (`Client` is `IDisposable`, not
`IAsyncDisposable`).

## Linemode and retro input

After LINEMODE is agreed, import/export the server's special characters
(RFC 1184):

```csharp
await client.ImportRemoteSpecialCharactersAsync();
await client.ExportSpecialCharactersAsync();
```

For client-side line editing, feed keystrokes through `LinemodeBuffer` (EC/EL/
EW editing, TRAPSIG-to-`IAC` commands, forwardmask flush) and send the
resulting `Data`. For retro endpoints, `InputFilter.CreateAtascii()` /
`CreatePetscii()` translate sessions keymaps longest-first; call `Flush()`
after the ~350 ms ESC delay to release a held lone `ESC`. A SyncTERM
font-selection sequence auto-adopts its encoding for later reads (an
explicit `TextEncoding` always wins; the sequence stays inert data on
server sessions).
`TelnetAccessories.Hexdump` renders wire bytes `hexdump -C` style for
diagnostics, and `MttsCapabilities` decodes the MTTS bitvector from the
third TTYPE answer.

## Gotchas

- Text is UTF-8 by default; Latin-1 applies only when
  `Settings.TextEncoding` is explicitly null. `WriteLineAsync`
  sends `"\r\n"` per RFC 854 (pass `Client.LegacyLineFeed` for bare `"\n"`).
  Without agreed `BINARY`, non-ASCII writes throw
  `EncoderFallbackException`.
- Reads serialize; `MillisecondReadDelay` (default 16 ms) tunes the idle poll.
- `SendSynchAsync` / `ReceiveUrgentAsync` need a real `TcpByteStream` and
  throw `NotSupportedException` on fakes.
- Negotiation repeats are suppressed; refusals stick until you re-request
  explicitly.

## References

- [MUD protocol notes](mud-protocols/README.md) (`MSDP`, `MSSP`, `GMCP`,
  `MCCP`, `MTTS`, `MXP`, `MSP`, `ZMP`, `ATCP`) and the [RFC texts](telnet-rfcs/)
  are the wire ground truth; [divergences](divergences.md) logs intentional
  deviations. The [fuzz harness](fuzz.md) exercises these paths.
