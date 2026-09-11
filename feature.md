# telnetlib3 features not supported by telnet_cs

Comparison of the Python library at `/home/anon/telnetlib3/` (`telnetlib3/`,
version 5.0.0 per `telnetlib3/accessories.py:43-45`) against the C# library at
`/home/anon/telnet_cs/telnet_cs/`.

Method: every claim below was double-checked against source on both sides
before writing. `telnet_cs` evidence cites `telnet_cs/Protocol/Options.cs`,
`telnet_cs/Protocol/Commands.cs`, `telnet_cs/IO/ByteStreamHandler.cs`
(`WeAgree`, `ReplySendAsync`, `InterpretNextAsCommand`), plus
`Client/`/`Server/` collectors. `telnetlib3` evidence cites
`telnetlib3/telopt.py`, `stream_writer.py`, `stream_reader.py`, `slc.py`,
`mud.py`, `client*.py`, `server*.py`, etc.

`telnet_cs` currently supports (so **not** listed as gaps):
RFC 854 base commands, RFC 855 + RFC 1143 Q-method (`Protocol/NegotiationState.cs`),
RFC 856 Binary (negotiation only), RFC 857 Echo, RFC 858 SGA, RFC 859 Status
(WILL-side responder), RFC 860 Timing Mark, RFC 1073 NAWS, RFC 1079 TSPEED,
RFC 1091 TTYPE (cycling), RFC 1184 Linemode + SLC, RFC 1408 Old Environ (opt 36),
RFC 1096 X Display Location (opt 35), implicit TLS (not STARTTLS option),
TCP connect/read timeouts. `WeAgree` allow-list pins this:
`telnet_cs/IO/ByteStreamHandler.cs:1110-1122` allows only
SGA/TTYPE/TSPEED/NAWS/BINARY/OldEnviron/XDisplay/Status/TimingMark/Linemode
(+ Echo via `AgreeEcho` at `:1132-1145`). Everything else earns
`WONT`/`DONT` by the generic RFC 1143 refuse path.

> Scope note: `Options.cs` enumerates many option numbers as placeholders
> (`Logout=18`, `SendLocation=23`, `EndOfRecord=25`, `RemoteFlowControl=33`,
> `NewEnvironment=39`, `CharacterSet=42`, `StartTls=46`, …) but enumeration
> alone is not support — there is no `SB` handler, no `WeAgree` entry, and no
> `ReplySendAsync` case for any feature below.

---

## 1. Logout option — RFC 727, option 18 (`0x12`)

No `IAC SB`, negotiation-only + close semantic.

- Wire: `IAC DO LOGOUT` (`FF FD 12`), `IAC DONT LOGOUT` (`FF FE 12`),
  `IAC WILL LOGOUT` (`FF FB 12`), `IAC WONT LOGOUT` (`FF FC 12`).
  There is no subnegotiation body.
- telnetlib3 behavior (`telnetlib3/telopt.py:37`, `stream_writer.py:1960-1978`,
  `:2048`, `:2129-2139`, `:2253-2256`, `:2344-2347`, `:1065`, `:1089-1090`,
  `:2023-2032`, `:2036`):
  - `DO` → close transport (voluntary logoff request granted); server-side
    only — client receiving `DO LOGOUT` raises `ValueError` (`:2036`).
  - `DONT` → log only, with no `WONT` reply (loop avoidance, `:2129-2139`).
  - `WILL` → answer `IAC DONT LOGOUT` (refuse to let peer force *our* logout);
    client receiving `WILL LOGOUT` raises `ValueError` (`:2253-2256`).
    (The `WILL`-branch log line at `:1974` mislabels it `WILL TIMEOUT`.)
  - `WONT` → log only via `handle_logout(WONT)`, plus a conditional
    `warning` when no `DO LOGOUT` was pending (`:2344-2347`).
  - Special-cased in `iac()`: `DONT LOGOUT` is always sent, never skipped as
    a duplicate (`:1089-1090`, `IAC-DONT-LOGOUT is not a rejection`), and
    `DO LOGOUT` bypasses only the `remote_option.enabled` early-return
    (`:1065`) — the pending-option check still applies. The server-refusal
    list at `:2023-2032` deliberately excludes `LOGOUT` so server-side
    `DO LOGOUT` reaches the close path at `:2048`.
- telnet_cs gap: `Options.Logout = 18` exists (`Protocol/Options.cs:45`) but
  `WeAgree` (`IO/ByteStreamHandler.cs:1110-1122`) omits it and
  `ReplySendAsync` (`:827-875`) has no case, so it is always refused. No
  close-on-`DO` semantic exists anywhere in `Client/`/`Server/`.
- Interop note: used as a polite “please disconnect” by some servers; without
  it the peer’s first `DO LOGOUT` is answered `WONT` (repeats suppressed by
  the RFC 1143 state machine) and the session lingers.

## 2. Send-Location option — RFC 779, option 23 (`0x17`)

- Wire: `IAC WILL SNDLOC / IAC DO SNDLOC` (`FF FB 17` / `FF FD 17`), then
  `IAC SB SNDLOC <ascii-location> IAC SE` (`FF FA 17 ... FF F0`). No
  `SEND`/`IS` discriminator in telnetlib3’s implementation — the payload after
  the option byte *is* the location string.
- telnetlib3 behavior (`telopt.py:39`, `stream_writer.py:2621-2625`,
  `:1830-1837`, `:2202-2206`, `:2232-2234`, `:2023-2035`, `:393`, `:418`):
  - Direction is strict: server sends `DO`, client answers `WILL`; server-side
    `DO SNDLOC` is refused with `WONT`, client-side `WILL SNDLOC` is refused
    with `DONT`.
  - `_handle_sb_sndloc` pops the option byte and decodes the rest as ASCII,
    then fires `_ext_callback[SNDLOC](location_str)` — with no
    `local/remote_option` guard, unlike the LFLOW/NAWS handlers.
  - Accepting `WILL SNDLOC` arms `pending SB+SNDLOC` (`:2232-2234`).
  - Client reply stub `handle_send_sndloc()` returns `""` (no location
    configured by default); server receipt hook is `handle_sndloc(location)`.
  - There is deliberately no `request_sndloc()` helper (unlike
    `request_ttype`/`request_xdisploc`); the server is expected to rely on the
    `DO` → `WILL` → `SB` flow.
- telnet_cs gap: `Options.SendLocation = 23` (`Protocol/Options.cs:55`) is
  enum-only. `WeAgree` omits it, `ReplySendAsync` has no case, no
  `ClientLocation`/collector exists. A peer’s `WILL SNDLOC` earns `DONT`.
- Interop note: rare today, but some hospitality/kiosk servers request it;
  RFC 779 location is free-form ASCII (room/floor/terminal label).

## 3. End-of-Record option — RFC 885, option 25 (`0x19`) + `IAC EOR` (239, `0xEF`)

- Wire: negotiation `IAC WILL EOR` / `IAC DO EOR` (`FF FB 19` / `FF FD 19`).
  Once enabled, record boundary is the two-byte command `IAC EOR` (`FF EF`,
  command 239) — **not** an `SB`. This is the prompt delimiter alternative to
  `IAC GA` (249) for block-mode / 3270-style peers.
- telnetlib3 behavior (`telopt.py:34,194`, `stream_writer.py:1123-1136`,
  `:1562-1564`, `:364`, `:376`, `:2057`, `:2186`, `client_shell.py:624,659`):
  - `send_eor()` checks `local_option[EOR]`; without a prior `DO EOR` it logs
    and returns `False`, otherwise emits `IAC CMD_EOR`.
  - `handle_eor` is registered both as the `IAC CMD_EOR` callback and the
    `SLC_EOR` callback (`:364`, `:376`); the base implementation only logs.
    Client shell *overrides* both with `_on_ga_or_eor`, treating `GA` and
    `EOR` identically as “prompt seen” for autoreply pacing
    (`client_shell.py:658-659`).
- telnet_cs gap (double-checked):
  - `Options.EndOfRecord = 25` (`Protocol/Options.cs:59`) is enum-only.
  - `Commands` enum (`Protocol/Commands.cs:8-45`) jumps from `Abort = 238`
    straight to `SubnegotiationEnd = 240` — command 239 does not exist.
  - `InterpretNextAsCommand` (`IO/ByteStreamHandler.cs:606-688`) has no 239
    case, so a received `FF EF` hits the `default:` silent-consume branch
    (`:683-687`, commented “same meaning as NOP”). `WeAgree` omits EOR.
    No `send_eor` helper exists.
- Interop note: 3270/TN3270E-style and some MUD servers delimit prompts with
  EOR instead of GA; telnet_cs will silently drop the boundary.

## 4. Terminal-Type: MTTS / TTYPE cycling nuances (RFC 1091, option 24)

telnet_cs *does* support RFC 1091 TTYPE (`Protocol/TerminalTypeCycler.cs`,
`IO/ByteStreamHandler.cs:831-833`), so the base option is not a gap. What is
missing is the server-side cycling discipline telnetlib3 implements:

- Wire (both libs agree): server `IAC SB TTYPE SEND IAC SE`
  (`FF FA 18 01 FF F0`) → client `IAC SB TTYPE IS <ascii> IAC SE`
  (`FF FA 18 00 ... FF F0`), repeated.
- telnetlib3 extras (`telnetlib3/server.py:602-653`, `:90`):
  - Stores `ttype1..N` + `TERM`; re-issues `request_ttype()` until the client
    loops back to `ttype1`, repeats the previous answer consecutively,
    answers empty, or exceeds `TTYPE_LOOPMAX = 8` (checked as `count > 8`,
    so the 9th response stops the cycle).
  - At round 3, when the *current* answer starts with `MTTS ` (upper-cased),
    treats `ttype2` as `TERM` (`server.py:640-644`): the expected MUD
    sequence is `ttype1 = real terminal`, `ttype2 = real terminal`,
    `ttype3 = MTTS <bitmask>`. A two-round `ANSI / MTTS 137` pair alone does
    not trigger it.
  - `_negotiate_echo` is re-run per round and `_negotiate_environ` is deferred
    until `ttype1 != ANSI` (works around MS-telnet crash, issue #24).
- telnet_cs gap: `ServerSession.Collectors.cs:150-171,349-375` caps the
  server-side chain at 32 entries (including the terminating duplicate,
  stripped afterwards) and stops on case-insensitive adjacent repeat, but
  has no MTTS/bitmask awareness and no per-round echo re-negotiation.
  (`TerminalTypeCycler.cs` itself has no 32 cap — the cap is server-collector
  only.) A MUD client sending `ANSI / MTTS 137 / …` will have the bitmask
  stored as a literal terminal-type alias rather than parsed capabilities.
- Note on RFC 930: telnetlib3’s `request_ttype` docstring cites RFC 930
  (`stream_writer.py:1397`) but implements RFC 1091 cycling; RFC 930’s
  single-shot `SEND`/`IS` is a historical subset, not a separate wire feature.

## 5. Remote Flow Control — RFC 1372, option 33 (`0x21`, `LFLOW`)

- Wire: `IAC WILL LFLOW / IAC DO LFLOW` (`FF FB 21` / `FF FD 21`), then
  `IAC SB LFLOW <mode> IAC SE` (`FF FA 21 <00-03> FF F0`) with
  `0 = OFF`, `1 = ON`, `2 = RESTART_ANY`, `3 = RESTART_XON`
  (`telopt.py:17,197`).
- telnetlib3 behavior (`stream_writer.py:1450-1468`, `:2677-2694`, `:2106`,
  `:2308`, `:132-137`):
  - Only the server may send the `SB`: `send_lineflow_mode()` logs an error
    and returns `False` on a client or when `remote_option[LFLOW]` is not
    set; otherwise it sends and returns `True`. The default
    (`xon_any = False`) sends `RESTART_XON`; setting `xon_any` switches the
    offer to `RESTART_ANY` (`:1461-1464`).
  - Client `_handle_sb_lflow` requires `local_option[LFLOW]` (else
    `ValueError … without DO LFLOW`). `OFF`/`ON` sets only `lflow`
    (`lflow = (opt is LFLOW_ON)`, `xon_any` untouched);
    `RESTART_ANY`/`RESTART_XON` sets only `xon_any`
    (`xon_any = (opt is LFLOW_RESTART_XON)`, `lflow` untouched).
    Unknown mode bytes raise `ValueError`. (The debug log at `:2689-2691`
    labels the two RESTART modes backwards.)
  - `DO LFLOW` arms `pending SB+LFLOW`; `WILL LFLOW` triggers
    `send_lineflow_mode()`.
- telnet_cs gap: `Options.RemoteFlowControl = 33` (`Protocol/Options.cs:75`)
  is enum-only. `WeAgree` omits it, `ReplySendAsync` has no case, no
  `LineflowMode`/`XonAny` state exists. Peers offering XON/XOFF negotiation
  are always refused.
- Interop note: matters for serial-attached and DEC-style hosts that use
  in-band XON/XOFF negotiation instead of TCP backpressure.

## 6. New Environment — RFC 1408 §6 / RFC 1572 (option 39, `0x27`) + RFC 1571 interop

telnet_cs supports **Old** Environ (opt 36, RFC 1408) on both roles
(`Protocol/EnvironmentProtocol.cs`, `IO/ByteStreamHandler.cs:844-846,929-939`,
`Server/ServerSession.Collectors.cs:233-247,412-449`). What is missing is the
**New** form (opt 39) that every modern client/server actually negotiates,
plus the RFC 1571 escaping/interop rules telnetlib3 implements:

- Wire (new form): `IAC WILL NEW_ENVIRON / IAC DO NEW_ENVIRON` (`FF FB 27` /
  `FF FD 27`), then
  `IAC SB NEW_ENVIRON SEND [VAR|USERVAR name …] IAC SE`
  (`FF FA 27 01 … FF F0`) →
  `IAC SB NEW_ENVIRON IS [VAR name VALUE value …] IAC SE`
  (`FF FA 27 00 …`) or `INFO (02)` for spontaneous updates.
  Sub-bytes: `IS/SEND/INFO = 0/1/2`, `VAR/VALUE/ESC/USERVAR = 0/1/2/3`
  (`telopt.py:195-196`).
- telnetlib3 behavior (`stream_writer.py:1297-1376,2560-2619,3540-3638`,
  `:231-234`, `server.py:476-555`, `server_fingerprinting.py:848-900`):
  - `OLD_ENVIRON` (36) is deliberately unimplemented — always `DONT`/`WONT`
    (`telnetlib.py:191` defines the constant, `telopt.py` omits it).
  - RFC 1571 escaping: literal `VAR` bytes in values are sent as `ESC VAR`,
    `USERVAR` as `ESC USERVAR` (`_escape_environ` / `_unescape_environ`);
    decode splits on non-`ESC`-escaped `VAR`/`USERVAR` and tolerates the peer
    conflating the two (`_decode_env_buf`).
  - `SEND`-batching for the GNU inetutils 256-byte subbuffer limit:
    `_ENVIRON_SB_MAX = 240`, `_batch_environ_keys` / `_send_environ_batch`.
  - Refuses “send everything” (`SEND` with bare `VAR`/`USERVAR`) for security;
    fingerprinting treats a bare `SEND` as “send all” when probing.
  - `environ_encoding` defaults to ASCII per RFC 1572 (`stream_writer.py:231-234`;
    the `cp037` note there is a manual opt-in for EBCDIC hosts such as
    IBM OS/400, not auto-detection); server `on_request_environ` sends
    `USER/LOGNAME/DISPLAY/LANG/TERM/…`, with the MS-telnet `ANSI+VT100`
    workaround (drop `USER`, `server.py:504-510`).
- telnet_cs gap (double-checked):
  - `Options.NewEnvironment = 39` (`Protocol/Options.cs:84-85`, whose comment
    still says “RFC 1408” instead of 1572) is enum-only.
  - `WeAgree` lists `OldEnvironment` only (`IO/ByteStreamHandler.cs:1117`);
    `ReplySendAsync` (`:844`) and server `RequestEnvironmentAsync`
    (`Server/ServerSession.Collectors.cs:240`) frame opt 36 only. Option 39 is
    always refused.
  - No `ESC`-escaping layer exists: `EnvironmentProtocol.cs:39-94,153-252`
    implements `IS/INFO/SEND + VAR/VALUE/ESC/USERVAR` framing for opt 36, but
    a NEW_ENVIRON peer never reaches it.
- Interop note: this is the highest-impact protocol gap. Modern telnet
  clients (PuTTY, SecureCRT, macOS telnet, MUD clients) offer 39, not 36; a
  telnet_cs server that `DO`s 36 in its opening preset
  (`Server/TelnetServerOptions.cs:60-63`) will get `WONT` from them and learn
  no `USER`/`LANG`/`DISPLAY` at all.

## 7. Charset option — RFC 2066, option 42 (`0x2A`)

Bidirectional negotiated encoding, the standards-track answer to “what bytes
mean”. telnetlib3’s census note (`README.rst:99-110`) explains why it matters:
only ~3% of MUDs/BBSs do CHARSET, so most non-ASCII peers need manual encoding
— but when CHARSET *is* offered, telnetlib3 negotiates it automatically and
telnet_cs cannot.

- Wire: `IAC WILL CHARSET / IAC DO CHARSET` (`FF FB 2A` / `FF FD 2A`). Either
  side that holds WILL+DO may then send
  `IAC SB CHARSET REQUEST <sep> <sep-joined-charset-list> IAC SE`
  (`FF FA 2A 01 <20> 55 54 46 2D 38 … FF F0`, separator usually space `0x20`)
  → `IAC SB CHARSET ACCEPTED <chosen> IAC SE` (`… 02 …`) or
  `REJECTED (03)`. Code points `TTABLE_IS/ACK/NAK/REJECTED (04-07)`
  (`telopt.py:198-200`) are defined but raise `NotImplementedError`
  (`stream_writer.py:2472-2475`) — trans-table download is out of scope on both
  sides.
- telnetlib3 behavior (`telopt.py:38`, `telnetlib.py:201`,
  `stream_writer.py:1256-1290,2441-2477,1946-1958,422-434`,
  `server.py:557-594,711-736`, `client.py:310-445`):
  - `request_charset()` requires `remote_option[CHARSET]` *or*
    `local_option[CHARSET]`, refuses while `pending SB+CHARSET`, then sends
    `REQUEST` built from `_ext_offer_callback[CHARSET]()`.
  - Offer vs accept split: base `handle_send_server_charset() → ["UTF-8"]`
    (`stream_writer.py:1946-1958`), overridden by the server shell to a
    16-entry list in `server.py:557-592` (`UTF-8 … US-ASCII`); client selects
    via `send_charset(offered)` with exact then `codecs.lookup`-canonical
    match, latin-1 weak-default fallback, else `REJECTED`.
  - On `REQUEST`: pick via `_ext_send_callback`, reply `ACCEPTED + name` (or
    `REJECTED`), set `environ_encoding = selected` and force BINARY on
    (`_force_binary_on_protocol`, `stream_writer.py:929-937,2462`).
  - On `ACCEPTED`: set `environ_encoding`, force BINARY, fire
    `_ext_callback[CHARSET](charset)` (server stores `extra["charset"]`,
    client stores `charset` for `encoding()`).
  - Direction quirk: server auto-`request_charset()` once both sides enabled
    (`server.py:725-732`); client sends `DO` but waits to be asked.
- telnet_cs gap: `Options.CharacterSet = 42` (`Protocol/Options.cs:91`) is
  enum-only. `WeAgree` omits it, `ReplySendAsync` has no case, no
  `REQUEST/ACCEPTED/REJECTED` parser exists, no `environ_encoding` concept.
  `TextEncoding` is a manual constructor setting
  (`Client/TelnetClientOptions.cs:70`, `Server/TelnetServerOptions.cs:105`);
  there is no wire negotiation of it.
- Interop note: without CHARSET, a UTF-8 peer strictly stays in 7-bit NVT
  ASCII unless BINARY is also negotiated; telnetlib3 upgrades to 8-bit clean
  automatically on `ACCEPTED`, telnet_cs never does.

## 8. MCCP compression — MCCP1 (85) / MCCP2 (86) / MCCP3 (87)

Mud Client Compression Protocol (not an RFC; de-facto MUD standard using raw
zlib/deflate inside the telnet stream). README documents it as a headline
feature (`README.rst:136-156`).

- Wire:
  - MCCP2 (server→client): server `IAC WILL MCCP2` (`FF FB 56`), client
    `IAC DO MCCP2` (`FF FD 56`), server starts with empty
    `IAC SB MCCP2 IAC SE` (`FF FA 56 FF F0`) — everything after `SE` is
    zlib-compressed until `IAC SE` ends it.
  - MCCP3 (client→server): mirror image on option 87 (`FF FB 57 …`
    `FF FA 57 FF F0`), compressing the client→server direction.
  - MCCP1 (85, `FF FB 55`) is the obsolete variant: defined in
    `telopt.py:201` but with no handler anywhere — always `WONT`/`DONT`.
- telnetlib3 behavior (`telopt.py:201`, `stream_writer.py:107,110,2065-2116,
  2180-2245,3352-3375`, `_base.py:90-94`, `client_base.py:368-400,420-429,
  479-521`, `server.py:202-249,258-289`, `server_base.py:359-368`,
  `client.py:890-896`, `server.py:1287-1293`):
  - `_EMPTY_SB_OK` includes MCCP2/MCCP3 so the empty `SB … SE` start frame is
    accepted (`stream_writer.py:107`); over-long SB guard is
    `_MAX_SUBNEGOTIATION = 1<<20` (`:115`).
  - Client default is *passive*: accept when offered unless
    `--no-compression`; `--compression` actively requests.
    Server default (`compression=None`) does not advertise but still accepts
    passively; `--compression` advertises `WILL MCCP2` + `WILL MCCP3`
    (`server.py:286-287`). Only `compression=False` rejects outright.
  - Rejected outright when `compression is False` or `ssl_object is not None`
    (CRIME/BREACH comment, `stream_writer.py:2090-2093,2225-2228`,
    `server.py:284-289`). Compression auto-disabled over TLS.
  - MCCP2 receive: `zlib.decompressobj(MAX_WBITS|32)` with raw-deflate
    fallback, `eof`/`unused_data` handling, mid-chunk `_compressed_remainder`
    split in `_base.py:90-94`. Server send side wraps `transport.write` with
    `compressobj` (`server.py:202-249`).
  - MCCP3 send: wraps `transport.write` with `compress + Z_SYNC_FLUSH`,
    `Z_FINISH` at end (`client_base.py:491-521`); server receive decompresses
    (`server_base.py:359-368`).
- telnet_cs gap (double-checked): `Protocol/Options.cs:100-113` jumps from
  `ForwardX = 49` to `PragmaLogon = 138` — 85/86/87 have no enum entries at
  all, and `rg MCCP telnet_cs/` returns zero hits. No zlib hook exists in
  `Transport/` or `IO/`. A MUD server offering `WILL MCCP2` gets `DONT` and
  falls back to uncompressed (correct but slower); a client offering MCCP3
  gets `WONT`.
- Interop note: the bandwidth win is large on MUDs (room descriptions
  compress ~5-10×); `--compression` peers will still connect, just never
  compress.

## 9. MUD application protocols: GMCP (201), MSDP (69), MSSP (70), MSP (90), MXP (91), ZMP (93), ATCP (200), Aardwolf (102)

All share one negotiation shape — `IAC WILL/DO <opt>` then
`IAC SB <opt> <payload> IAC SE` — but each payload is its own mini-format
implemented in `telnetlib3/mud.py` and dispatched in
`stream_writer.py:3260-3350`. telnet_cs has none of them (enum gap 49→138,
zero hits for `GMCP|MSDP|MSSP|MXP|ZMP|ATCP|Aardwolf` in `telnet_cs/`).

### 9a. GMCP — Generic MUD Communication Protocol, opt 201 (`0xC9`)

- Payload: `<package>[ SP <JSON>]`, e.g. `Char.Vitals {"hp":42}`
  (`mud.py:64-98`: `gmcp_encode` = utf-8 package + `b" "` +
  `json.dumps(separators=(",",":"))`; `gmcp_decode` splits on first space,
  `json.loads`, best-effort utf-8→latin-1, `ValueError` on malformed JSON).
- telnetlib3: `send_gmcp(package, data)` guards on `local or remote GMCP`
  (`stream_writer.py:1138-1150`); `_handle_sb_gmcp` decodes with
  `environ_encoding or utf-8` and fires `_ext_callback[GMCP](package, data)`
  (`:3260-3270`). Client auto-answers the server’s `WILL GMCP` with
  `Core.Hello + Core.Supports.Set` listing `_DEFAULT_GMCP_MODULES`
  (`client.py:28-37,166-209`) and merges inbound into `ctx.gmcp_data`
  (`client.py:223-230`).
- Gap: no enum, no `SB` handler, no JSON framing in telnet_cs.

### 9b. MSDP — MUD Server Data Protocol, opt 69 (`0x45`)

- Payload: `VAR(01) name VAL(02) value` with `TABLE_OPEN(03)…TABLE_CLOSE(04)`
  and `ARRAY_OPEN(05)…ARRAY_CLOSE(06)` nesting
  (`telopt.py:212-218`, `mud.py:101-218`: `msdp_encode` dict→bytes,
  `MsdpParser` state machine with `_read_string/_read_key/_parse_table/
  _parse_array/parse`, `msdp_decode` entry point).
- telnetlib3: `send_msdp` guard (`stream_writer.py:1169-1180`);
  `_handle_sb_msdp → msdp_decode → callback(dict)` (`:3272-3282`).
- Gap: none of the `01-06` sub-bytes, parser, or encoder exists in telnet_cs.

### 9c. MSSP — MUD Server Status Protocol, opt 70 (`0x46`)

- Payload: `VAR(01) name VAL(02) value`, repeated `VAL` for multi-valued keys
  (`mud.py:221-277`: `mssp_encode`/`mssp_decode`; single → `str`, multi →
  `list[str]`).
- telnetlib3: `send_mssp` guard (`:1182-1193`); `_handle_sb_mssp` decodes,
  fires callback, and stores `ctx.mssp_data` (`:3284-3294,1917-1920`).
  Fingerprinting waits up to `mssp_wait = 5.0 s` for it
  (default at `server_fingerprinting.py:443,507`, deadline helper
  `:1107-1114`).
- Gap: no MSSP support in telnet_cs; server-status crawlers see nothing.

### 9d. MSP — MUD Sound Protocol, opt 90 (`0x5A`)

- Payload: opaque bytes (sound-file paths/URLs, server-defined).
- telnetlib3: `handle_msp(data: bytes)` (`:1922`), `_handle_sb_msp` pops the
  option byte and forwards the raw payload (`:3296-3304`); empty `SB` allowed
  (`_EMPTY_SB_OK`). No `send_msp` (server→client only in practice).
  Fingerprinting probes it (`fingerprinting.py:340`).
- Gap: enum + handler both absent in telnet_cs.

### 9e. MXP — MUD eXtension Protocol, opt 91 (`0x5B`)

- Payload: opaque bytes (in-band HTML-like markup); empty `SB` allowed.
- telnetlib3: `handle_mxp` appends `ctx.mxp_data` (`:1926-1929`),
  `_handle_sb_mxp` (`:3306-3314`), `DO` arms `pending SB+MXP` (`:2106`) and
  `WILL` arms it too (`:2232`).
- Gap: absent in telnet_cs. Note: full MXP *rendering* is out of scope for
  both libs; telnetlib3 only frames/buffers the bytes.

### 9f. ZMP — Zenith MUD Protocol, opt 93 (`0x5D`)

- Payload: `command NUL arg1 NUL … NUL` (trailing NUL required)
  (`mud.py:280-314`: `zmp_encode(cmd,*args) = NUL-join + NUL`,
  `zmp_decode = split(NUL) minus trailing ""`).
- telnetlib3: `send_zmp` guard (`:1152-1167`); `_handle_sb_zmp → zmp_decode →
  callback(cmd, *args)` (`:3316-3327`); client auto-identifies
  (`setup_zmp/on_will_zmp/send_zmp_ident`, `client.py:184-263`) and stores
  `ctx.zmp_data[cmd] = args` (`:1931-1934`).
- Gap: absent in telnet_cs.

### 9g. ATCP — Achaea Telnet Client Protocol, opt 200 (`0xC8`)

- Payload: `package[ SP value]`, first-space split, no-space → `""`
  (`mud.py:317-331`).
- telnetlib3: `_handle_sb_atcp → atcp_decode → callback(package, value)` +
  `ctx.atcp_data.append` (`:3340-3350,1941-1944`). Client declines by default
  unless `always_will/do` (`:2076-2087,2208-2222`).
- Gap: absent in telnet_cs.

### 9h. Aardwolf protocol, opt 102 (`0x66`)

- Payload: 1–2 bytes `channel [data]`; channel map
  `100=status … 108=message` (`mud.py:334-365`: `aardwolf_decode →
  {channel, channel_byte, data_byte/data_bytes}`).
- telnetlib3: `_handle_sb_aardwolf → aardwolf_decode → callback(dict)` +
  `ctx.aardwolf_data.append` (`:3329-3338,1936-1939`).
- Gap: absent in telnet_cs.

### 9i. Also absent: TELOPT 92, COM_PORT (44), SUPPRESS_LOCAL_ECHO (45)

- `TELOPT_92 = bytes([92])` (`telopt.py:207`) has no handler (always refused)
  in telnetlib3 too — listed here only to pin the number.
- `COM_PORT_OPTION (44)` (`telopt.py:46`) is a full RFC 2217 serial-port
  implementation in telnetlib3: `request_comport_signature` (`1217-1234`),
  `_handle_sb_comport` (`3197-3258`), dispatch entry (`:2390`),
  `WILL`-accept (`:2188`) with auto-signature (`:2238-2239`). Probed as the
  sole `MUD_OPTIONS` entry (`fingerprinting.py:328`). telnet_cs enums
  `COMPortControl = 44` (`Options.cs:94`) with no handler, so this **is**
  a gap (serial-over-telnet peers get `WONT`/`DONT`).
- `SUPPRESS_LOCAL_ECHO (45)` (`telopt.py:47`, probed in `LEGACY_OPTIONS`,
  `fingerprinting.py:353`) has no `SB` handler in telnetlib3 either.
  telnet_cs enums `SuppressLocalEcho = 45` and `StartTls = 46`
  (`Options.cs:95-99`) with the same no-handler status, so 45/46/92 are
  **parity, not gaps**, and get no further section.

## 10. STARTTLS (option 46), AUTHENTICATION (37), ENCRYPT (38)

- STARTTLS: `Options.StartTls = 46` (`Protocol/Options.cs:99`) is enum-only in
  telnet_cs; never in `WeAgree`, no `SB FOLLOWS` handling. telnetlib3 likewise
  has no option-46 `SB` — both libs do *implicit* TLS instead (telnet_cs:
  `Transport/TlsSocket.cs:45-99`, `Client/TelnetClientOptions.cs:107-136`
  (`UseTls` at `:112`), `Server/TelnetServerOptions.cs:119-133`; telnetlib3:
  `open_connection(ssl=…)` at `client.py:669-679`, `create_server(ssl=…, tls_auto=…)` at
  `server.py:1193-1252`). The missing piece on *both* sides is in-band
  `STARTTLS` upgrade; it is listed here because telnetlib3 at least probes
  `TLS` in LEGACY fingerprinting (`fingerprinting.py:354` inside `:347-380`)
  while telnet_cs never sends `WILL/DO 46`.
- AUTHENTICATION (37): enum-only in telnet_cs (`Options.cs:82-83`); telnet_cs
  auth is plaintext `TryLoginAsync` / `AuthenticateAsync(login:/Password:,
  3 tries)` (`Client/Client.cs:17-32`,
  `Server/ServerSession.Negotiation.cs:386-413`,
  `MaxLoginAttempts = 3` in `TelnetServerOptions.cs:99`). telnetlib3 has
  `AUTHENTICATION = b"%"` (`telopt.py:42`) plus `XAUTH = b")"` but no RFC 2941
  exchange either — parity, except telnetlib3’s fingerprinting *detects* it.
- ENCRYPT (38): not even enumerated in telnet_cs (`Options.cs:82-85` jumps
  37→39); named in telnetlib3 (`telopt.py:41`) with no exchange. Parity (both
  refuse), noted to avoid confusion with transport TLS.

## 11. Custom 8-bit encodings: ATASCII, PETSCII, big5bbs, ATARIST (+ encoding-negotiation policy)

telnet_cs accepts any `System.Text.Encoding` via `TextEncoding`
(`Client/TelnetClientOptions.cs:70`, `Server/TelnetServerOptions.cs:105`,
`Transport/TcpByteStream.cs:74`, `IO/ByteStringConverter.cs:31-63`), defaulting
to Latin-1 8-bit-clean. What is missing is telnetlib3’s retro/BBS codecs and
the policy that binds them to BINARY/CHARSET/LANG negotiation:

- Codecs (`telnetlib3/encodings/__init__.py:19-67` registers via
  `codecs.register` + `_search_function`; `FORCE_BINARY_ENCODINGS =
  {atascii, atari8bit, petscii, cbm, …, atarist}` at `:52-65` auto-enables
  raw/binary mode):
  - **ATASCII** (`encodings/atascii.py:1-19,34-403`, aliases
    `atari8bit, atari_8bit`): Atari 400/800/XL/XE. `0x20-0x5F` + `0x61-0x7A`
    ASCII, `0x00-0x1F` graphics (heart/box/triangles/arrows), `0x60` diamond,
    `0x7B` spade, `0x7D` clear-screen, `0x7E` backspace, `0x7F` tab,
    `0x80-0xFF` inverse video (mostly `& 0x7F`, distinct block/bullet),
    `0x9B EOL → LF`. Encoder prefers `0x00-0x7F` + `LF → 0x9B`; incremental
    *encoder* handles split `CR/CRLF` via `pending_cr` (`:324-353`) — the
    incremental decoder is passthrough.
  - **PETSCII** (`encodings/petscii.py:1-18,35-359`, aliases
    `cbm, commodore, c64, c128`): C64 shifted/lowercase mode.
    `0x41-0x5A → a-z`, `0xC1-0xDA → A-Z`, `0x00-0x1F/0x80-0x9F`
    controls/colors/F-keys (`0x0D RETURN`, `0x8D shift-RETURN`), box/block/
    geometric approximations, `0xFF` pi. `ENCODING_TABLE =
    charmap_build(DECODING_TABLE)` — lossy on duplicates by design.
  - **big5bbs** (`encodings/big5bbs.py:1-27,37-173`, aliases
    `big5_bbs, big5_pcman, big5_pcmanx, big5_ptt`): Taiwan Ptt/DreamBBS hybrid. Decode:
    lead `A1-FE` + second `40-7E/A1-FE` + defined → big5, else lone lead →
    cp437; lone lead + other (ESC) → cp437; `< A1` → latin-1; incremental
    one-byte `_buf` lookahead. Encode per-char: big5 if encodable else cp437.
  - **ATARIST** (`encodings/atarist.py:1-5,49-329`, alias `atari`):
    `ATARIST.TXT`. `0x00-0x7F` ASCII, `0x80-0xFF` accented/Hebrew/Greek/math
    (`80 Ç, 9B ¢, C2 א, E0 α, F0 ≡`). Plain `charmap_build`.
- Negotiation policy (the part telnet_cs lacks entirely):
  - Per-direction BINARY gate: `inbinary = remote[BINARY]`,
    `outbinary = local[BINARY]` (`stream_writer.py:950-957`); without BINARY
    (and without `force_binary`) `encoding()` collapses to `US-ASCII`
    (server `server.py:378-426`, client `client.py:466-502`).
  - Auto-BINARY: `_force_binary_on_protocol` on `CHARSET ACCEPTED` both sides
    (`:2462,2467`), on server `on_environ` when `CHARSET`/`LANG` carries an
    encoding (`server.py:549-555`), on `_check_encoding` requesting
    `DO BINARY` when only outbinary holds (`server.py:711-736`).
  - `LANG` → `encoding_from_lang("en_US.UTF-8@misc") → "UTF-8"`
    (`accessories.py:48-69`); SyncTERM font escape `CSI … SP D →
    SYNCTERM_FONT_ENCODINGS[0..42 with gaps]` forces `environ_encoding + force_binary`
    (`client_base.py:253-274`, `server_fingerprinting.py:194-234`).
  - Client `--encoding=cp437/big5bbs …` + `--raw-mode` for serial-hooked BBSs
    (`README.rst:106-120`); `FORCE_BINARY_ENCODINGS` require raw/binary.
- telnet_cs gap: `rg -i 'atascii|petscii|big5|atarist' telnet_cs/` is empty.
  No `LANG`-parsing helper, no CHARSET hook (see §7), no per-direction
  ASCII-fallback, no `force_binary` concept (telnet_cs does negotiate
  `TransmitBinary` in `WeAgree:1116` but never gates `TextEncoding` on it).
  A `cp437` BBS connects, but the
  caller must know to set `TextEncoding = Encoding.GetEncoding("cp437")`
  manually — there is no `--encoding`-style CLI, no census-driven default,
  and no `SYNC…`-style auto-detect.
- Verification: `ByteStringConverter.cs:10-14,48-60,97-106` is Latin-1
  1:1 + `IAC`-doubling only; `Client/Client.Connect.cs:247-260` proactively
  sends `DO SGA`, never `DO BINARY` + `REQUEST CHARSET`.

## 12. Interactive client shell (POSIX + Win32) — `telnetlib3-client` TTY layer

telnet_cs ends at `ReadAsync/WriteAsync/WriteLineAsync/SendCommand`
(`Client/BaseClient.cs`, `Client/Client.cs:98-179`); there is no interactive
terminal program. telnetlib3 ships a full one
(`client_shell.py:27,40-1053`, `client_shell_win32.py:20-248`,
`client.py:699-1089`):

- `InputFilter(seq_xlat, byte_xlat)` (`client_shell.py:97-184`): longest-first
  escape-sequence translation with prefix buffering + `flush()` on
  `ESCDELAY` (default 0.35 s, `:53-62`). Retro maps
  (`_INPUT_XLAT`, `_INPUT_SEQ_XLAT`, `:40-94`): ATASCII `DEL/BS → 0x7E`,
  `CR/LF → 0x9B` plus arrows/`3~`/TAB sequences; PETSCII `DEL/BS → 0x14`
  plus arrows/`3~`/`H`(home)/`2~`(insert) sequences.
- `LinemodeBuffer(slctab, forwardmask, trapsig)` (`:277-361`): client-side
  line editing when server negotiates LINEMODE EDIT — `TRAPSIG →
  IAC IP/ABORT/SUSP/EOF/BRK/AYT`; `EC`/`EL`/`EW` edit the local buffer
  (`\b \b` pop, clear, VWERASE); forwardmask-triggered flush, `CR/LF` line
  send.
- Raw-mode engine (`_raw_event_loop`, `:472-596`): telnet read as a `2**24`
  task plus `stdin` via `make_reader_task`, `^]` escape closes (`:520-526`), LINEMODE-EDIT vs cooked
  forwarding (`:527-544`), `_send_stdin` software echo
  (`BS → \b \b`, `CR → \r\n`, `:387-426`), server output via
  `_transform_output` (ATASCII glyphs `U+1FB82/U+25E3 → \r/\n`, raw
  ` \n → \r\n`, cooked strip `\r`, `:364-384`) + `autoreply.feed` +
  `typescript` (`:561-587`).
- GA/EOR pacing (`:622-678`): `_on_ga_or_eor` sets `server_uses_ga` +
  `engine.on_prompt`, `_wait_for_prompt` 2 s timeout stored as
  `ctx.autoreply_wait_fn` — scripted `bin/client_wargame.py`-style shells wait
  for the prompt signal instead of sleeping.
- `Terminal` POSIX (`:755-1053`): `determine_mode` kludge/local/remote table
  (`:935-991`), `_make_raw` termios (`BRKINT/ICRNL/…` cleared, `VMIN=1`,
  `:843-871`), `_suppress_echo`, `check_auto_mode` (LINEMODE EDIT restores
  cooked; SGA → raw; ECHO-only → suppress, `:880-933`),
  `SIGWINCH → _send_naws` (`:776-794`), `connect_write_pipe(sys.stdin if
  istty)`. Win32 mirror via `blessed.Terminal()` + resize poll thread
  (`client_shell_win32.py:30-48,95-178,203-227`).
- Gap: none of this exists in telnet_cs — no `Terminal`, no `InputFilter`, no
  `LinemodeBuffer`, no `^]` handling, no SIGWINCH/NAWS auto-refresh on resize
  (telnet_cs has manual `RefreshWindowSizeAsync`,
  `Client/Client.WindowSize.cs:21-50`, only).
- **Scope decision:** implement `InputFilter` + retro keymaps and
  `LinemodeBuffer` as library classes (pure logic, unit-testable). Skip the
  raw-mode engine + POSIX/Win32 `Terminal`: that is an interactive console
  program needing raw-termios interop and an entry point, and this repo
  ships no executables.
  - Verified pure: `InputFilter.feed` (`client_shell.py:153-184`) /
    `flush` (`:137-151`) operate only on caller-supplied tables plus an
    internal prefix buffer; `has_pending` (`:132-135`) exists precisely so
    the *caller* owns the esc-delay timer — no syscalls, no asyncio, no
    I/O. `LinemodeBuffer.feed` (`:320-361`) is a pure
    char → `(echo, data?)` function; its only import is `telopt` command
    constants (`:297`), and the `TRAPSIG → IAC …` map is prebuilt bytes
    (`:303-310`). Porting note: `LinemodeBuffer` consumes an SLC table +
    forwardmask, so the port must define the adapter from telnet_cs's
    `LinemodeState`/`LinemodeProtocol` to that shape.
  - Verified skip: the POSIX engine lazily imports `termios` (`:749`) and
    installs `SIGWINCH` handlers on the asyncio loop (`:777-800`); the
    Win32 mirror requires the third-party `blessed` package
    (`client_shell_win32.py:35-38,244-245`). telnet_cs touches `Console`
    only for optional write/beep/dimensions
    (`IO/ByteStreamHandler.Reading.cs:112`, `IO/ByteStreamHandler.cs:636`,
    `Protocol/NawsProtocol.cs:50-54`) — no raw-mode interop exists. Both
    csprojs omit `OutputType` (default `Library`), so a `Terminal` port
    would also need a new executable project, not just new classes.
- **Implemented:** `Client/InputFilter.cs` (longest-first sequence tables +
  single-byte map, prefix hold-back, `Flush` literalizes held bytes,
  `EscapeDelay` default 350 ms; `CreateAtascii`/`CreatePetscii` factories
  carry the reference retro maps verbatim) and `Client/LinemodeBuffer.cs`
  (trapsig → `IAC IP/ABORT/SUSP/EOF/BRK/AYT`, local `EC`/`EL`/`EW` editing,
  forwardmask flush, `CR`/`LF` line send; `LinemodeEdit` selects the SLC
  func/value/level triplets, defaulting to `EC = 127`, `EL = 0x15`,
  `EW = 0x17`). Covered by `telnet_cs.Tests/Client/ShellInputTests.cs`.

## 13. Interactive server shell + PTY shell server

- **REPL shell** (`server_shell.py:37-481`, default `--shell`):
  `telnet_server_shell(reader, writer)` loop prints `Ready (secure: …)` vs
  `Ready.` (`:197-202`), `tel:sh> ` + `send_ga` unless `never_send_ga` +
  `readline_async` (`:205-213`), commands
  `quit/help/writer/reader/proto/version/slc/linemode/toggle [option|all]/
  dump […]` (`:218-284`); `readline` generator (`:299-322`),
  `readline_async` with `filter_ansi` (strips CSI/OSC/DCS/APC/PM via `wcwidth`
  `ZERO_WIDTH_PATTERN` + `SS3`, `:53-119`) and split-CRLF `LF/NUL` skip
  (`:324-354`); `_LineEditor` grapheme backspace with `wcswidth`
  (`:122-175`); `get_slcdata/get_linemode/do_toggle` introspection
  (`:357-481`, toggling
  echo/goahead/outbinary/inbinary/binary/xon-any/lflow/linemode/
  linemode-edit/linemode-trapsig live).   telnet_cs has no REPL — closest is
  the login helper (`ServerSession.Negotiation.cs:384-428`).
- **PTY shell** (`server_pty_shell.py:26-705`,
  `server.py:1275-1280,1332-1348,1395-1467`): `make_pty_shell(program, args,
  preexec_fn, raw_mode)` → `pty.fork + execvpe` with exec-error pipe
  (`:103-152`), `_build_environment` (`TERM` vt100/vtnt/vt52→ansi,
  `LINES/COLUMNS`, `LANG/LC_ALL`, `CHARSET→en_US.charset`,
  `DISPLAY/USER/COLORTERM/HOME/SHELL/LOGNAME/IPADDRESS`, `:174-208`),
  `_setup_child` (`TIOCSWINSZ`, raw: `ECHO+ICANON` off, `VERASE=^H`,
  `:216-262`), `_bridge_loop` (`add_reader(master_fd)` 256 K batch vs telnet
  4096, `FIRST_COMPLETED`, `:309-414`), `_write_to_pty` (`DEL 0x7F→0x08`,
  `:416-435`), `_write_to_telnet` BSU/ESU-gated flush
  (`\x1b[?2026h/l`, `:49-50,437-479`), `_flush_output` incremental decode +
  `_schedule_ga` (`:481-527`, `_GA_IDLE = 0.1 s` in code — the docstring at
  `:510` and `README.rst:133-134` still say 500 ms — skip when
  `raw_mode/remote-SGA/never_send_ga`), `_schedule/_fire_naws` debounce 0.2 s
  + `TIOCSWINSZ+SIGWINCH` (`:32,273-307`), `cleanup`
  (`SIGHUP/CONT/INT→KILL`, 0.1 s, `:569-601`). CLI:
  `telnetlib3-server --pty-exec /bin/bash -- --login` (raw default) or
  `--line-mode` for cooked (  `README.rst:68-74`). Gated by `PTY_SUPPORT`
  (`__init__.py:50-55`). telnet_cs has no PTY layer at all (no `fork`,
  no `TIOCSWINSZ`, `rg -i pty telnet_cs/telnet_cs/` empty).
- **Scope decision:** implement the REPL shell only (as a `ServerSession`
  helper; no shell-delegate concept exists yet, but a static method fits
  without structural changes). Skip the PTY shell server: .NET has no
  `forkpty`, and a `Process`-redirect bridge would lack job control and a
  real TTY, diverging from the reference behavior it would claim to match.
  - Verified: `Server/` contains no shell concept (`rg Shell` is empty) —
    `TelnetServer.AcceptSessionAsync` hands back a bare `ServerSession`
    after the opening preset (`Server/TelnetServer.cs:94-129`). The closest
    existing helper is `AuthenticateAsync`
    (`Server/ServerSession.Negotiation.cs:401-428`: `LoginUserPrompt` /
    `LoginPasswordPrompt` loop, `MaxLoginAttempts = 3` at
    `Server/TelnetServerOptions.cs:126`).
  - REPL port notes: the prompt loop maps onto `WriteAsync` +
    `TerminatedReadAsync` (overload families on both roles:
    `Client/Client.cs:182-375`,
    `Server/ServerSession.Negotiation.cs:207-375`) plus per-prompt GA via
    `SendCommand(Commands.GoAhead)`
    (`Server/ServerSession.Negotiation.cs:126-144`) — but `SendCommand`
    transmits unconditionally (`:147-152`) with no SGA suppression, unlike
    the reference `send_ga` (false under SGA). So the REPL should consume
    the §19 `SendGaAsync` helper rather than calling `SendCommand`
    directly (ordering: §19 before §13a).
  - Verified skip: no `fork`/`Process`/PTY layer exists in the library
    (only substring false positives such as "started").
- **Implemented:** `Server/ServerShells.cs` (`RunReplAsync`) — `Ready.`
  banner, `tel:sh> ` prompt, per-prompt `SendGaAsync` unless
  `NeverSendGa`, and the `quit/help/version/negotiation/stats/environ`
  commands (the reference `toggle`/`dump`/`slc`/`linemode` introspection is
  covered by `negotiation`, which dumps the agreed/refused option states).
  Covered by `telnet_cs.Tests/Server/ReplTests.cs`.

## 14. Relay (proxy) server example

`relay_server.py:22-107` — `relay_shell(client_reader, client_writer)` as
`--shell telnetlib3.relay_server.relay_shell`: `readline` generator,
passcode `867-5309` (3 tries), next-hop `1984.ws:23`, per-char `read(1)`
password prompt, `open_connection(next_host, next_port, cols/rows from
writer)`, bidirectional `make_reader_task` + `asyncio.wait(FIRST_COMPLETED)`
  forwarding. telnet_cs has no proxy/relay helper; forwarding must be hand-rolled
  from `ServerSession` + `Client`.
- **Scope decision:** skipped for now (deferred; no technical blocker — pure
  forwarding between a `ServerSession` and a `Client`).
  - Verified composable: `Client.ConnectAsync(host, port, options, ct,
    timeout)` (`Client/Client.Connect.cs:125-144`) plus symmetric
    `ReadAsync(timeout, ct)` (`Client/Client.cs:365`,
    `Server/ServerSession.cs:93`), `WriteAsync(string|byte[], ct)`, and
    `TerminatedReadAsync` families on both roles (`Client/Client.cs:182-375`,
    `Server/ServerSession.Negotiation.cs:207-375`) cover the data plane; a
    shared `CancellationTokenSource` cancelled by either direction's EOF
    reproduces the `FIRST_COMPLETED` forwarding shape. One porting wrinkle:
    the reference passcode gate reads single chars (`read(1)`), while
    telnet_cs reads return whatever arrived within the timeout, so the
    gate must split its own buffer — minor, no blocker.

## 15. Fingerprinting (client + server + terminal display)

The largest infra gap by line count. Two CLIs:
`telnetlib3-fingerprint` (probe a *server*) and
`telnetlib3-fingerprint-server` (probe connecting *clients*)
(`pyproject.toml:63-67`, `README.rst:76-83`).

- **Server-side (identify clients)** (`fingerprinting.py:108-1309`):
  probe tables `CORE` 14 opts (`BINARY…SNDLOC`, `LOGOUT` omitted, `:310-326`),
  `MUD` `COM_PORT` (`:328`), `EXTENDED` 10
  (`MCCP2/MCCP3/GMCP/MSDP/MSSP/MSP/MXP/ZMP/ AARDWOLF/ATCP`, `:334-345`),
  `LEGACY` 32 (`AUTH…NAOLFD`, `:347-380`);
  `ALL = CORE+MUD+LEGACY`, `QUICK = CORE+MUD` (`:382-383`).
  `probe_client_capabilities(writer, options, timeout=0.5)` bursts `IAC DO`,
  drains, polls 0.05 s → `WILL/WONT/timeout + already_negotiated`
  (`:456-518`); `probe_client_loop_detection` (default 0.3 s) re-`DO/WILL` →
  looped list (`:392-453`). `_create_protocol_fingerprint` anonymizes
  (`HOME/USER/SHELL/IP → True/None`, encoding from `LANG`, `TERM →
  None/SyncTerm/Yes-ansi/Yes`, charset, ttype-count, supported/refused,
  rejected-will/do/directional, slc, `:748-829`, sha256[:16] hash).
  Saves `DATA_DIR/client/<proto>/<probe>/<session>.json` (caps
  `MAX_FILES/MAX_FINGERPRINTS = 1000`, `:111-116,850-1129`).
  `_is_maybe_mud` (`MUD_TERMINALS mudlet/cmud/…` + GMCP/MSDP/MXP/MSP/ATCP/
  AARDWOLF, `:128-142,1132-1143`), `_is_maybe_ms_telnet`
  (`ttype1 == ANSI and ttype2 in ("", VT100)`, `:1146-1162` → reduced probe
  excluding `NEW_ENVIRON`).
  `fingerprinting_server_shell` runs the probe, Syncterm→Topaz quirk
  (`\x1b[0;40 D`, `:1185-1187`), `DONT LINEMODE` for PTY kludge, then execs
  the post-script over `pty_shell(raw_mode=True)` with `latin-1` for syncterm
  + `IPADDRESS` + `TELNETLIB3_INTERACTIVE_TERMINAL` (`:1165-1254`).
- **Client-side (identify servers)**
  (`server_fingerprinting.py:66-1302`): `fingerprinting_client_shell(…,
  scan_type=quick|full, mssp_wait=5.0, banner_quiet_time=2.0, banner_max_wait=8.0,
  max_bytes=65536, …)` (`:432-495`); settle 0.5 s + banner-quiet wait
  (`:80-85,525-543`); prompt loop ×5 with `_detect_yn_prompt`,
  ATASCII `_reencode_prompt`, utf-8/big5 switch (`:85,258-283,553-602`);
  `probe_server_capabilities` minus `_CLIENT_ONLY_WILL`
  (`TTYPE/TSPEED/NAWS/XDISPLOC/NEW_ENVIRON/LFLOW/LINEMODE/SNDLOC`, `:72,817-845`);
  wrong-direction probes (`DO NAWS/TTYPE + WILL ECHO → wrong-accept/
  correct-refuse`, `:76-78,697-752`); loop detection (`:755-814`);
  `_await_mssp_data` deadline (`:1107-1114`); ANSI handling
  (`_YN_RE`, `_COLOR/_ANSI_COLOR/_MENU_UTF8/_GB_BIG5/_CODEPAGE/_MORE/
  DSR `\x1b[6n→\x1b[1;colR`/DA/ENQ, `:91-185,286-336,1138-1302`),
  SyncTERM font detect (`CSI … SP D → SYNCTERM_FONT_ENCODINGS[0..42 with gaps:
  7/15/19/28 missing]`, `:194-234`); save `DATA_DIR/server/…` or `--save-json`, `--set-name`
  (`:673-678,991-1068`), `_print_json` culled + `jq -C` (`:342-370`).
- **Display** (`fingerprinting_display.py:41+`): `fingerprinting_post_script`
  runs `ucs-detect` (timeout 20 s, `:226`) + `screen-scrape` (timeout 30 s,
  `:274`), builds terminal fingerprint
  (software/colors/sixel/kitty/iterm2/DA/modes/xtgettcap),
  side-by-side `prettytable` + REPL (`t/l/s/u/h/q` plus `^L`, `:1421-1428`).
- Gap: `rg -i fingerprint telnet_cs/` is empty. No probe tables, no loop
  detection, no `DATA_DIR` corpus, no ANSI/DSR/CPR machinery in telnet_cs.
- **Scope decision:** skipped — fingerprinting is out of scope for this
  library (probe suites, JSON corpus persistence, and the interactive
  terminal-display REPL belong to analysis tooling, not the wire library).
  - Consistency note: probing presupposes generic named negotiation
    waiters, which do not exist yet — collection polling is a private
    `PollForResponseAsync(Func<bool> isDone, …)` per collector
    (`Server/ServerSession.Collectors.cs:719`, predicates e.g. `:440,472`).
    Skipping fingerprinting therefore blocks nothing: the §19 waiter work
    it would need is already scheduled independently, and a future probe
    suite would consume it.

## 16. Guard shells (anti-bot / overload gates)

`guard_shells.py:29-317` + wiring in `server.py:1469-1513`
(`--robot-check`, `--pty-fork-limit`):

- `ConnectionCounter(limit)` (`:75-106`): `try_acquire/release/count`.
- `robot_check(reader, writer, timeout=5)` (`:218-230`): `_measure_width(" ")`
  (`:189-215`, clear `\x1b[{x}G`) + `_get_cursor_position` (`DSR \x1b[6n +
  drain + _read_cpr_response until R`, regex `\x1b\[(\d+);(\d+)R`,
  `:39-40,146-186`) over `_latin1_reading` (`:43-64`); width == 1 →
  human-shaped terminal, else bot. `robot_shell` asks `Do robots
  dream…[yn]` + `windowmakers?`, logs answers, disconnects (`:254-287`).
- `busy_shell` (`:290-317`): `Machine is busy… + distant explosion`, 30 s
  reads, logs `input1/2` (`_MAX_INPUT = 2048`, `:37`).
- `run_server(robot_check/pty_fork_limit)` wraps the inner shell:
  `counter → busy_shell | robot_check → robot_shell | inner`
  (`server.py:1469-1513`).
- Gap: no equivalent in telnet_cs (no `DSR/CPR` helpers, no connection
  counter — `Backlog = 32` is only the TCP listen queue,
  `Server/TelnetServerOptions.cs:22`).
- **Scope decision:** skipped for now (deferred; `ConnectionCounter` and the
  DSR/CPR probe helpers would fit under `Server/` when wanted).
  - Verified deferrable: every primitive exists — DSR is plain bytes
    (`ESC[6n`) via `WriteAsync`; the CPR reply (`ESC[{row};{col}R`) is pure
    ASCII so the decoded string reads suffice, with timeouts on
    `ReadAsync(timeout, ct)` in both roles (`Client/Client.cs:365`,
    `Server/ServerSession.cs:93`) reproducing the 5 s `robot_check`
    budget; the default Latin-1 path is byte-transparent, matching the
    reference's `_latin1_reading`. Only genuinely new code is the counter
    (a semaphore-shaped atomic acquire/release) and the CPR regex.

## 17. Blocking/sync API + legacy `telnetlib` backport shim

- **Sync API** (`sync.py:48-920`, `bin/blocking_client.py`,
  `bin/blocking_echo_server.py`): `TelnetConnection(host, port=23, timeout,
  encoding=utf8)` (`:73-80`) — own event loop + daemon thread +
  `run_coroutine_threadsafe(_async_connect)` with `TelnetClient` (not
  Terminal, `:95-142`); `read(n, timeout)` (`EOF→EOFError`,
  `timeout→TimeoutError`, `:151-175`), `read_some`, `readline`,
  `read_until(match)`, `write` (`call_soon_threadsafe`), `flush` (`drain`),
  `close/_cleanup`, `get_extra_info(TERM/cols/rows/peername/LANG)`,
  `wait_for(remote/local/pending) → writer.wait_for` (`:177-404`);
  `BlockingTelnetServer(handler)` (`:446-577`: loop thread, `accept(timeout)`
  via queue, `serve_forever` thread-per-client, `shutdown`);
  `ServerConnection` miniboa-compat
  (`active/address/port/terminal_type/columns/rows/connect_time/
  last_input_time/send(\n→\r\n)/addrport/idle/duration/deactivate`,
  `:613-920`). telnet_cs has no public blocking facade: the only blocking
  calls are internal/sync-constructor shims — proactive-`DO SGA` plus the
  per-option `NegotiateOption` loop (`Client/Client.Connect.cs:100,107`,
  `GetAwaiter().GetResult()`), the sync `TcpClient(host, port) → Connect()`
  constructor (`Transport/TcpClient.cs:20-23,267-281`), and the `Client`
  constructor `are.WaitOne(2)` spin (`:82-86`). The library ships no
  executables (`csproj` has no `OutputType Exe`).
- **Legacy shim** (`telnetlib.py:1-753`, lightly modified CPython 3.12 +
  `test→main` for pytest): `Telnet(host, port, timeout)`, `open`,
  `write` (IAC-doubling), `read_until/read_all/read_some/read_very_eager/
  read_eager/read_lazy/read_very_lazy/read_sb_data`, `set_option_negotiation_
  callback` (default auto-`WONT`/`DONT`), `process_rawq` IAC/SB state machine,
  `interact/mt_interact/listener`, `expect(regex list)`, plus
  `import telnetlib` drop-in for Python ≥ 3.13 (`README.rst:37-39`). No
  equivalent in telnet_cs (no stdlib-compat layer).
- **Scope decision:** the library stays async-only — a blocking/sync facade
  will never be added (blocking on async code contradicts this repo's
  conventions; the existing sync constructors at the transport edges are the
  only precedent). The legacy `telnetlib` shim has no .NET port target and
  is dropped permanently.
  - Verified against the rule (`AGENTS.md:92`: "`async`/`await` end-to-end;
    never block on async code (`.Result`, `.Wait()`,
    `.GetAwaiter().GetResult()` …)"): the three existing blocks are all
    edges, not facades — the sync `TcpClient(host, port)` constructor
    (`Transport/TcpClient.cs:20-23`) delegates to `GetConnectedClient`
    (`:267-281`), which calls the genuinely blocking
    `System.Net.Sockets.TcpClient.Connect` (`:272`), i.e. sync-over-sync,
    not async-over-sync; the `Client` constructor spin (`are.WaitOne(2)`,
    `Client/Client.Connect.cs:85`) and the proactive-negotiation
    `GetAwaiter().GetResult()` calls (`:100,107`) run on
    `Task.Run(…)` thread-pool offload with a comment explaining the shape
    (`:96`). A public `TelnetConnection`/`BlockingTelnetServer` equivalent
    would need the forbidden pattern as its core mechanism, not as an
    edge — hence "never", not "later".

## 18. CLI tools and their options

Entry points (`pyproject.toml:63-67` — `[project.scripts]` at `:63`, server
first): `telnetlib3-server = server:main`, `telnetlib3-client = client:main`,
`telnetlib3-fingerprint-server = fingerprinting:fingerprint_server_main`,
`telnetlib3-fingerprint = client:fingerprint_main`. telnet_cs ships no
executables.

- **Client** (`client.py:699-1089`): `host port` +
  `--always-do/dont/will/wont` (named/numeric, repeatable, `:842-873`),
  `--ansi-keys`, `--ascii-eol`, `--compression/--no-compression` tri-state
  (`None` = passive, `:890-896`), `--connect-minwait 0 / --connect-maxwait 4.0
  / --connect-timeout 10`, `--encoding utf8`, `--encoding-errors
  replace|ignore|strict`, `--force-binary` (default True), `--gmcp-modules`,
  `--line-mode/--raw-mode` (exclusive; `FORCE_BINARY_ENCODINGS → raw`,
  non-ascii → force_binary, `:925-940,1027-1089`),
  `--logfile/mode/logfmt/loglevel`, `--send-environ TERM,LANG,…`,
  `--shell telnetlib3.telnet_client_shell`, `--speed 38400`,
  `--ssl/--ssl-cafile/--ssl-no-verify`, `--term $TERM`,
  `--typescript/mode`. `run_client` injects
  `always_*, environ_encoding, encoding_explicit, raw/ascii_eol/ansi_keys/
  typescript` wrappers (`:699-831`). `bin/client_wargame.py` shows the
  `--shell bin.client_wargame.shell` script pattern (`README.rst:62`).
- **Server** (`server.py:1264-1565`): `host localhost port 6023` +
  `--compression`, `--connect-maxwait 1.5` (CLI/CONFIG; `TelnetServer.__init__`
  defaults to 4.0), `--encoding utf8|false→False`,
  `--force-binary`, `--line-mode` (suppresses `WILL SGA/ECHO`, cooked PTY),
  `--never-send-ga` (MUD default *sends* GA; PTY sends after 100 ms output
  idle in code (`_GA_IDLE = 0.1`, `server_pty_shell.py:35`) — `README.rst:133-134`
  still says 500 ms — to avoid mid-stream injection),
  `--pty-exec PROGRAM -- args` (+ `--pty-fork-limit`, hidden `--pty-raw`),
  `--robot-check`, `--shell function_lookup`, `--ssl-certfile/keyfile →
  PROTOCOL_TLS_SERVER`, `--status-interval 20`, `--timeout 300` (idle
  disconnect, 0 disables), `--tls-auto [SECONDS]` (const 0.5,
  `:1381-1391`). telnet_cs `TelnetServerOptions` (`TelnetServerOptions.cs:1-144`)
  covers `Backlog/OfferEcho/OfferSuppressGoAhead/RequestTerminalType/
  RequestTerminalSpeed/RequestWindowSize/RequestEnvironment/RequestXDisplay/
  RequestLinemode + Login*/IsWriteConsole/Log + TextEncoding + TLS
  cert/protocols/ListenAddress` — no compression, GA policy, timeout, status,
  robot, PTY, or shell-lookup flags.
- **Fingerprint CLIs** (`client.py:1103-1361`,
  `fingerprinting.py:1281-1309`): `--banner-max-bytes 65536 / --banner-max-
  wait 8 / --banner-quiet-time 2`, `--data-dir` (client default `None`;
  server defaults to `DATA_DIR` / `$TELNETLIB3_DATA_DIR`),
  `--encoding ascii` (stream encoding, `cp037` EBCDIC),
  `--mssp-wait 5`, `--save-json`, `--scan-type quick|full`,
  `--send-env KEY=VALUE`, `--set-name`, `--silent`, `--ttype VT100`, plus
  SSL flags. No counterparts in telnet_cs.
- **Scope decision:** skipped — the repo ships no executables, and most CLI
  flags only make sense on top of the skipped interactive/PTY/fingerprint
  features above.
  - Verified: neither `telnet_cs.csproj` nor `telnet_cs.Tests.csproj` sets
    `OutputType` (both default to `Library`). CLIs would therefore require
    new console projects in the solution, not just new classes — and flag
    coverage confirms the dependency: client flags assume §12c
    (`--shell`, `--raw-mode`, `--typescript`), server flags assume §13b
    (`--pty-exec`, `--pty-fork-limit`), `--robot-check` assumes §16, and
    the fingerprint flags assume §15. With those skipped, the remaining
    portable flags (host/port/encoding/TLS/timeouts) do not justify
    executable projects on their own.

## 19. StreamReader/Writer runtime extras (timeouts, GA/EOR plumbing, waiters, typescript, session context)

- **Timeouts / idle / status**: telnetlib3 server `timeout = 300` idle
  disconnect (`server.py:66,99,148,162,186-201,428-465`:
  `set_timeout(duration)` armed on `connection_made`, reset on every
  `data_received`, `call_later(on_timeout → "Timeout." + close)`,
  0 disables) + `StatusLogger(interval = 20)` logging
  `ip:port(rx,tx,idle,tls)` on change (`server.py:1019-1088`); client
  `connect_minwait = 0 / connect_maxwait = 4.0` negotiation settle
  (`client_base.py:42-43,308-351,523-552`) + `connect_timeout → ConnectionError`
  (`client.py:681-691`; note `open_connection` default `connect_maxwait = 3.0`
  at `:564` differs from the CLI default); TLS-auto sniff `MSG_PEEK 0x16
  ClientHello → start_tls(server_side)`, 0.5 s (`server.py:739-884,1193-1252`). telnet_cs
  has per-read `ReadAsync(timeout)` (rolling + initial,
  `IO/ByteStreamHandler.Reading.cs:67-165`), socket
  `ReceiveTimeout/SendTimeout/NoDelay/KeepAlive`
  (`Transport/ISocket.cs:34-58`, `TcpClient.cs:41-86`,
  `TcpByteStream.cs:98-138`), and connect/TLS-handshake deadlines
  (`Client/Client.Connect.cs:147-230`) — but **no** server idle disconnect,
  **no** status logger, **no** TLS-auto sniff (explicit cert-or-plaintext
  only, `Server/TelnetServer.cs:66-129`), and **no**
  minwait/maxwait settle (opening preset is fire-and-forget).
- **GA / EOR plumbing**: telnetlib3 `send_ga` returns `False` when
  `local_option[SGA]` is enabled (i.e. WILL-state after `DO SGA`), else
  `IAC GA` (`stream_writer.py:1108-1121`), `send_eor` requires `DO EOR`
  (`:1123-1136`), `set_iac_callback(GA/EOR)` defaults (`:350-367`); server
  shell sends GA per prompt unless `never_send_ga`
  (`server_shell.py:209-210`), PTY after 100 ms idle in code
  (`_GA_IDLE = 0.1`) — `README.rst:133-134` still says 500 ms. telnet_cs surfaces `GA` via `GoAheadReceived`
  only when SGA is *not* in effect
  (`IO/ByteStreamHandler.cs:658-669`, `Client/BaseClient.cs:38-44`) and has
  no `send_ga/send_eor` helpers and no EOR path at all (§3).
- **Negotiation waiters**: telnetlib3
  `wait_for(remote/local/pending by name → option_from_name)` +
  `wait_for_condition(pred)` via `_waiters/_check_waiters/_cancel_waiters`
  (`stream_writer.py:551-646`); `get_extra_info` delegates to transport
  (`:910-922`). telnet_cs polling is `PollForResponseAsync(IsXDisplayDone/
  IsEnvironmentDone, timeout)` per collector
  (`Server/ServerSession.Collectors.cs:205-277`) — no generic named waiter.
- **Typescript / autoreply / session context**:
  `TelnetSessionContext(raw_mode/ascii_eol/input_filter/autoreply_engine/
  autoreply_wait_fn/typescript_file/gmcp_data/zmp/mssp/atcp/aardwolf/mxp/
  comport)` (`_session_context.py:11-62`, default `writer.ctx`,
  `stream_writer.py:274`); client shell wires `raw_mode/ascii_eol/
  input_filter/autoreply/typescript` through it
  (`client_shell.py:375-451,561-587`, `client.py:786-808`). telnet_cs has no
  session-context object, no typescript recorder, no autoreply engine hook.
- **Framing robustness**: telnetlib3 `_MAX_SUBNEGOTIATION = 1<<20` with
  overflow discard (`stream_writer.py:115,830-845`), `_ENVIRON_SB_MAX = 240`
  batching, `_EMPTY_SB_OK` set, `IAC IAC` escape in `SB`
  (`:769-776`), `TRACE` hexdumps (`accessories.py:104-124`). telnet_cs caps
  `SB` at 512 B (`IO/ByteStreamHandler.cs:18,770-790`) with the `STATUS`
  bare-`SE` special case (`:709-740`): empty `SB` is silently dropped (no
  `_EMPTY_SB_OK`), over-cap is silent (no warning), and there is no environ
  batching or TRACE hexdump.
- **Scope decision:** implement them all — server idle-disconnect timeout,
  status logger, `SendGaAsync`, generic named negotiation waiters (also the
  natural replacement for the per-collector `PollForResponseAsync`
  pattern), typescript recorder, session-context bag, and TLS-auto sniff
  as opt-in (default off, given the accept-path security surface).
  - Verified each gap is real (targeted `rg` over `telnet_cs/*.cs` finds
    only false positives: a "peek" comment and an "Idle delay between read
    polls" comment): no idle-disconnect timer, no status logger, no
    `SendGa`, no typescript, no session-context type, no `0x16`/peek TLS
    sniff. The accept path is strictly cert-or-plaintext today
    (`Server/TelnetServer.cs:94-129` — TLS wrapper iff
    `options.ServerCertificate is not null`), so TLS-auto sniffing slots
    in as a pre-handshake peek branch, safely default-off. Waiter shape is
    confirmed by the existing private pattern it generalizes:
    `PollForResponseAsync(Func<bool> isDone, timeout, ct)` with
    per-collector `Is*Done` predicates
    (`Server/ServerSession.Collectors.cs:719`, e.g. `:440,472`). GA
    plumbing confirmed asymmetric: inbound GA is already gated on agreed
    SGA (`IO/ByteStreamHandler.cs:823-833`, agreed-state signal at `:827`),
    while outbound `SendCommand(GoAhead)` is unconditional
    (`Server/ServerSession.Negotiation.cs:147-152`) — `SendGaAsync` is the
     missing guard (false when SGA agreed, else `IAC GA`), and §13a will
     consume it.
- **Implemented:** `Client/BaseClient.GaWaiters.cs` (`SendGaAsync` on both
  roles; `WaitForNegotiationAsync` + `WaitForOptionEnabledAsync` generic
  named waiters that pump short reads instead of spinning),
  `Server/TelnetSessionContext.cs` (connect/last-activity timestamps, idle
  span, rx/tx char counters, raw typescript recorder, property bag),
  `TelnetServerOptions.IdleTimeout` (default 300 s, latched per-session
  `IsIdleTimedOut` followed by a best-effort `Timeout.` notice and close),
  `TelnetServerOptions.StatusInterval` (default 20 s, endpoint/rx/tx/idle/
  tls lines via `options.Log`), and `TelnetServerOptions.TlsAutoDetect`
  (opt-in pre-handshake `0x16` peek that falls back to plaintext; default
  off). Covered by `telnet_cs.Tests/Server/SessionExtrasTests.cs`.

## 20. Accessories / diagnostics utilities

`accessories.py:12-174`: `TRACE = 5` log level, `encoding_from_lang`,
`name_unicode` (curses-free `unctrl`), `eightbits` (bitmask pretty-print),
`hexdump` (`hexdump -C` style), `make_logger` (loglevel/logfile/logfmt),
`repr_mapping` (`key=value …` with `shlex.quote`), `function_lookup`
(`module.fn` → callable for `--shell`), `make_reader_task`
(`reader.read(2**12)` task). telnet_cs logging is `Log(Action<string>)` /
`WriteLog` (`Server/TelnetServerOptions.cs:117`,
`IO/ByteStreamHandler.cs:245,353-366`) plus `Trace` statics
(`Client/Client.Connect.cs:40`, `IO/ByteStreamHandler.cs:280`,
`TelnetClientOptions.Log:85`) — no numeric TRACE level, hexdump, LANG
parser, or shell-lookup helper.

