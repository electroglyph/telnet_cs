# Client usage guide

The client lives in the `telnet_cs.Client` namespace. The main type is
`Client` (implements `IClient`); options are carried by the
`TelnetClientOptions` record. Everything is async-only.

## Connecting

```csharp
using telnet_cs.Client;

// Plain TCP, 30 s default timeout.
await using var client = await Client.ConnectAsync("mud.example.com", 4000);

// With options and cancellation.
var options = new TelnetClientOptions
{
    TerminalTypes = ["xterm-256color", "xterm"],
    TerminalSpeed = "38400,38400",
    TextEncoding = Encoding.UTF8,
    WindowWidth = 100,
    WindowHeight = 40,
};
await using var client2 = await Client.ConnectAsync(
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

## Configuring answers

The client answers option queries automatically from `Settings` on the read
path — there are no polling getters for peer values. Set these before you
start reading:

| Setting | Answers |
|---|---|
| `TerminalType` / `TerminalTypes` | TTYPE `IS`, cycling the list per `SEND` (default `"vt100"`) |
| `TerminalSpeed` | TSPEED `IS`, sent verbatim (default `"19200,19200"`) |
| `WindowWidth` / `WindowHeight` | NAWS size; `0` = auto-detect from console |
| `EnvironmentUser`, `EnvironmentDisplay`, `EnvironmentUserVars` | ENVIRON `USER`/`DISPLAY`/custom vars, plus auto `TERM`/`LANG`/`COLUMNS`/`LINES` |
| `CharsetOffers` | CHARSET preference order (default `["UTF-8"]`; empty answers `REJECTED`) |
| `XDisplayLocation`, `SendLocation` | X-DISPLAY / SNDLOC answers |
| `TextEncoding` | Non-null switches both directions off legacy Latin-1 and advertises `LANG=en_US.<WebName>` |
| `AllowRemoteEcho` | `true` accepts the server's `DO ECHO` (default refuses) |
| `EnableMccp` | Agree MCCP2/3 framing (you do the zlib yourself); always refused over TLS |
| `EnableMudOptions` / `EnableComPort` | Agree MUD options (default off) / RFC 2217 framing (default on) |
| `Log`, `IsWriteConsole`, `EnableBell` | Diagnostics, console echo, BEL handling |

Global process-wide defaults (`Client.TerminalType`, `Client.TerminalSpeed`,
`Client.Trace`, `Client.IsWriteConsole`) apply when the per-instance setting
is left at its default; prefer `TelnetClientOptions`.

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
    TimeSpan.FromSeconds(5));
bool done = await client.WaitForNegotiationAsync(
    s => s.IsEnabledByUs((int)Options.OldEnvironment), TimeSpan.FromSeconds(5));
```

On window resize, re-announce (there is no console-resize event in .NET —
poll this yourself):

```csharp
await client.RefreshWindowSizeAsync();
```

`GoAheadReceived` fires on the read path for unsuppressed `GA`
(RFC 858 turn-taking). `SendGaAsync` sends `IAC GA` unless our own `WILL`
Suppress-GA holds (local side only; then it sends nothing and returns `false`).
`SendTimingMarkAsync` does an RFC 860 round-trip. `Dispose()` (or
`await using`) is the only close.

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
after the ~350 ms ESC delay to release a held lone `ESC`.

## Gotchas

- Text is Latin-1 unless `Settings.TextEncoding` is set; `WriteLineAsync`
  sends `"\r\n"` per RFC 854 (pass `Client.LegacyLineFeed` for bare `"\n"`).
- Reads serialize; `MillisecondReadDelay` (default 16 ms) tunes the idle poll.
- `SendSynchAsync` / `ReceiveUrgentAsync` need a real `TcpByteStream` and
  throw `NotSupportedException` on fakes.
- Negotiation repeats are suppressed; refusals stick until you re-request
  explicitly.
