# Divergences from telnetlib3 (verified by execution)

Date: 2026-09-14. Baseline: `dotnet test telnet_cs.sln -c Release` →
Passed 1296/1296, 0 failed. Every divergence below was re-proven live on
both sides during this pass; nothing is carried over on authority.

Method: fresh probes only, kept in [`repro/`](repro/) next to this
document — Python in `repro/py_d*.py` (real `telnetlib3` classes from a
local checkout, `wcwidth` stubbed; set `TELNETLIB3_PATH` if yours lives
elsewhere), C# in [`repro/csdiv/Program.cs`](repro/csdiv/Program.cs) (console app referencing
[`telnet_cs/telnet_cs.csproj`](../telnet_cs/telnet_cs.csproj), `MemStream` implements the real
`IByteStream`). Logs sit next to the scripts (`repro/*.log`).
C# paths below are relative to the repository root; the leading
`telnet_cs/` source-root segment is omitted where the file name is
unambiguous.

Update 2026-09-14 (second pass): entries D6–D21 below, same method — fresh
probes only, Python in
`repro/py_d{6,7,8,10,11,12,13,14,15,16,17,18,19,20,21}_*.py`
(plus the earlier [`repro/py_d4_raisevssilent.py`](repro/py_d4_raisevssilent.py)), C# in
[`repro/cs_wire.log`](repro/cs_wire.log) (labeled `read`/`readS` runs of
[`repro/csdiv`](repro/csdiv), rebuilt against current sources) and the named xUnit
pins, all executed. Baseline: `dotnet test telnet_cs.sln -c Release` →
Passed 1317/1317, 0 failed. Four former divergences were fixed rather than
documented (ungated EOR surfacing, ZMP handler-OR-list answers, masked SLC
import levels, empty-`CHARSET ACCEPTED` rejection path); the intentional
remainder of those areas is covered in D19/D21. Existing entries D2/D5 had
stale line numbers after those fixes and were re-anchored; D1/D3/D4 were
re-checked unchanged.

## D1 — withdrawing our own enable request is queued instead of sent (wire)

- Proof: [`repro/py_d1_usdisable.py`](repro/py_d1_usdisable.py) →
  [`repro/py_d1_usdisable.log`](repro/py_d1_usdisable.log): a client sending `WILL SGA` then
  `WONT SGA` gets `-> True` on each line with
  `writes= ['fffb03', 'fffc03']` (both bytes go out immediately).
  [`repro/csdiv`](repro/csdiv) → [`repro/cs_negqueue.log`](repro/cs_negqueue.log):
  `offerEnable => reply=251`, `offerDisable => reply=null us=WantYes`
  (the WONT is swallowed into the queue). The negotiation machine is
  identical for servers and clients, so both roles behave this way.
- Code: withdrawing our own request (`OfferDisable`,
  [`telnet_cs/Protocol/NegotiationState.cs:322-327`](../telnet_cs/Protocol/NegotiationState.cs#L322-L327)) calls
  `InitiateDisableLocked` with `gateOnOutstanding: true`, so the
  `WantYes` branch ([`:449-479`](../telnet_cs/Protocol/NegotiationState.cs#L449-L479), documented [`:441-448`](../telnet_cs/Protocol/NegotiationState.cs#L441-L448)) sets
  `queued[option] = true` and returns null. Telling the peer to stop
  (`RequestDisable`, [`:253-260`](../telnet_cs/Protocol/NegotiationState.cs#L253-L260)) passes `gateOnOutstanding: false`, so a
  DONT goes out immediately like telnetlib3 — only withdrawals of our
  own outstanding request queue.
- Why it exists: the queue is the RFC 1143 single-entry opposite-queue
  shape, so telnetlib3 itself is non-compliant here — telnetlib3-vs-RFC
  conflict. Interop impact is negligible (a deferred WONT, never a wrong
  byte). Pinned by `NegotiationStateTests.UsSide_QueueMirrorsHimSide`; kept
  by design, change only on an owner decision.

## D2 — simultaneous CHARSET REQUEST answered REJECTED on server role (wire)

- Proof: [`repro/py_d2_charsetsim.py`](repro/py_d2_charsetsim.py) →
  [`repro/py_d2_charsetsim.log`](repro/py_d2_charsetsim.log): with `pending_option[SB+CHARSET]`
  set (own REQUEST outstanding) and the real
  `TelnetClient.send_charset` policy wired in, feeding
  `FF FA 2A 01 20 "UTF-8 LATIN-1" FF F0` yields
  `writes= ['fffa2a025554462d38fff0']` (ACCEPTED UTF-8 — no pending gate).
  Cs [`repro/cs_charsetsim.log`](repro/cs_charsetsim.log) (`charsetSim`: `IsServerRole=true`,
  `CharsetRequestPending=true`, same bytes): `writes=FFFA2A03FFF0`
  (REJECTED). A client in the same situation answers ACCEPTED like
  telnetlib3 ([`repro/cs_clientsim.log`](repro/cs_clientsim.log), `clientSim` mode:
  `writes=FFFA2A025554462D38FFF0`).
- Code: [`telnet_cs/IO/ByteStreamHandler.cs:2341-2348`](../telnet_cs/IO/ByteStreamHandler.cs#L2341-L2348)
  (`ReplyCharsetRequestAsync`: `if (CharsetRequestPending && IsServerRole)`
  → REJECTED). telnetlib3: `_handle_sb_charset`
  (`stream_writer.py:2441-2463`) answers via the send callback regardless
  of pending state. The Cs gate is server-role-only, so client-role Cs
  answers ACCEPTED like telnetlib3 (log above).
- Why it exists: Cs cites RFC 2066 §5 (server-wins on simultaneous
  REQUEST); telnetlib3 has no gate. Only reachable when both sides
  initiate CHARSET at once, and negotiation still converges (peer sees
  REJECTED and may retry vs immediate accept). Same telnetlib3-vs-RFC bucket
  as D1; pinned by
  `ExtendedCollectorsTests.SimultaneousCharsetRequest_WhileOursOutstanding_AnswersRejected`.

## D3 — urgent-Synch discard drops data (wire-visible data loss)

- Proof: [`repro/py_d3_synch.py`](repro/py_d3_synch.py) →
  [`repro/py_d3_synch.log`](repro/py_d3_synch.log): feeding `AB` + `IAC DM` + `CD` gives
  per-byte inband `True` for A, B, C, D (`writes= []`) — `handle_dm` only
  logs (`stream_writer.py:1547-1549`) and delivers everything.
  [`repro/cs_synch.log`](repro/cs_synch.log) (`synch` mode: bytes
  `41 42 FF F2 43 44` with `EnterSynchDiscard` entered): `data=4344`
  (`CD` only — `AB` discarded), `writes=` empty.
- Code: [`telnet_cs/IO/ByteStreamHandler.cs:130-218`](../telnet_cs/IO/ByteStreamHandler.cs#L130-L218) (`InSynchDiscard`,
  `EnterSynchDiscard`, `PollSynchTrigger` consuming one TCP urgent byte via
  `TcpByteStream.TryConsumeUrgentSignal`, `RetrieveSynchDiscardAsync` and
  the `RetrieveAndParseSynchDiscard` scan-until-`IAC DM`); remarked as deliberate in
  [`telnet_cs/Client/Client.Connect.cs:27-30`](../telnet_cs/Client/Client.Connect.cs#L27-L30) ("no opt-out").
- Why it exists: deliberate RFC 854 Synch extension — telnetlib3 never
  discards. Gated on a pending TCP-urgent byte, so rare in practice, but
  genuinely wire-visible when triggered. Pinned by `SynchDiscardTests`;
  kept by design, change only on an owner decision.

## D4 — NAWS size default 0,0 vs 80x25 (wire default content only)

- Proof: [`repro/py_d6_repeatrefuse.log`](repro/py_d6_repeatrefuse.log) (same run prints
  `TelnetClient.__init__` defaults): `cols: int = 80, rows: int = 25`.
  [`repro/cs_nawsdefault.log`](repro/cs_nawsdefault.log): `effective0x0=0x0`,
  `frame=FFFA1F00000000FFF0` (0,0 sent as-is).
- Code: [`telnet_cs/Protocol/NawsProtocol.cs:18-21`](../telnet_cs/Protocol/NawsProtocol.cs#L18-L21) (`GetEffectiveSize`
  clamps like telnetlib3's `max(min(65535,v),0)` but a 0 dimension is
  sent as-is, never mapped to a console probe); [`Client.Connect.cs:285`](../telnet_cs/Client/Client.Connect.cs#L285)
  `_terminalTypeDefault = "unknown"` and [`:300`](../telnet_cs/Client/Client.Connect.cs#L300)
  `_terminalSpeedDefault = "38400,38400"` already match telnetlib3's
  (`term='unknown'`, `tspeed=(38400,38400)` per
  [`repro/py_d5_defaults.log`](repro/py_d5_defaults.log)); [`TelnetClientOptions.cs:78-84`](../telnet_cs/Client/TelnetClientOptions.cs#L78-L84)
  `WindowWidth/WindowHeight` default 0.
- Why it exists: 0 is a legitimate wire value — RFC 1073 says a zero
  dimension means "no character width (or height) is being sent", leaving
  the assumed size OS-specific — so sending it as-is is honest; the rest of the old ENVIRON-volunteering finding was refuted
  (a real `TelnetClient` volunteers TERM/LANG/COLUMNS/LINES/COLORTERM and
  answers TTYPE/TSPEED just like Cs — the bare-writer `{}`/empty values
  that drove it are not what real peers see). Configurable both sides;
  kept as accepted variance.

## D5 — TSPEED fields assigned in opposite order (wire semantics)

- Proof: [`repro/py_d8_tspeed.py`](repro/py_d8_tspeed.py) →
  [`repro/py_d8_tspeed.log`](repro/py_d8_tspeed.log): with send policy `(rx, tx) = (111, 222)`,
  answering SEND yields `writes= ['fffa20003131312c323232fff0']`
  (IS "111,222", rx first); receiving IS "111,222" reports
  `callback(rx, tx)= [(111, 222)]` (first field parsed as rx), nothing
  sent. Cs with `TerminalSpeed="111,222"` answers the same SEND with
  byte-identical `writes=FFFA20003131312C323232FFF0`
  ([`repro/cs_tspeed.log`](repro/cs_tspeed.log), `tspeed` mode) — but Cs documents that
  string tx-first. Identical wire bytes, opposite meaning: a telnetlib3
  peer reads Cs's "111,222" as rx=111 while Cs meant tx=111.
- Code: telnetlib3 `_handle_sb_tspeed` (`stream_writer.py:2479-2515`):
  IS parses the first field into `rx_str` (`:2487`) and reports
  `_ext_callback[TSPEED](rx_int, tx_int)` (`:2506`); SEND emits
  `brx, b",", btx` from `(rx, tx)` (`:2508-2513`); the client documents
  `tspeed` as `(rx, tx)` (`client.py:604`) and wires
  `"tspeed": f"{tspeed[0]},{tspeed[1]}"` (`:115`). Cs documents
  `"<tx>,<rx>"` ([`TerminalSpeedProtocol.cs:4-6`](../telnet_cs/Protocol/TerminalSpeedProtocol.cs#L4-L6); `Validate` names
  `parts[0]` tx and `parts[1]` rx at [`:34-35`](../telnet_cs/Protocol/TerminalSpeedProtocol.cs#L34-L35); SEND-reply log at
  [`ByteStreamHandler.cs:1870-1878`](../telnet_cs/IO/ByteStreamHandler.cs#L1870-L1878); `ClientTerminalSpeed` doc at
  [`ServerSession.Collectors.cs:136-140`](../telnet_cs/Server/ServerSession.Collectors.cs#L136-L140)), matching RFC 1079 §4
  ("transmit and receive speeds ... separated by a comma").
- Why it exists: telnetlib3-vs-RFC conflict — telnetlib3 puts rx first
  against the RFC's transmit-first order, and Cs kept the RFC order.
  Invisible with the symmetric defaults (`38400,38400` on both sides, so
  the stock handshake agrees byte-for-byte); only asymmetric speeds cross.
  Kept by design, change only on an owner decision (matching telnetlib3
  would break RFC-reading peers, and vice versa).

## D6 — RFC 1143 collision queue vs forget-and-resend (negotiation state)

- Proof: [`repro/py_d6_qbit.py`](repro/py_d6_qbit.py) → [`repro/py_d6_qbit.log`](repro/py_d6_qbit.log):
  `WILL SGA` then `WONT SGA` both return `True` (bytes out immediately,
  D1's shape). Him-side: `DONT SGA` out, then inbound `WILL SGA` yields
  `writes= ['fffe03', 'fffd03']` — the `DO` is resent immediately, the
  `DONT` forgotten. Us-side race: after `WILL, WONT`, inbound `DO SGA`
  yields `writes= ['fffb03', 'fffc03']` — silent, but only because the
  stale `WILL`-pending flag suppresses the re-`WILL` while `local` is
  latched `False`. C# answers the same stale `DO` with `WONT`:
  `NegotiationStateTests.UsSide_QueueMirrorsHimSide` (`OfferEnable` →
  `Will`, `OfferDisable` → null/queued, `ReceivedDo` → `Wont`), and the
  him-side mirror is pinned by `QueuedDisable_DrainsWhenEnableCompletes`,
  `ReceivedWill_InWantNoEmpty_ClearsToNo`, and
  `ToggleTwice_EnableClearsQueuedDisable`.
- Code: [`telnet_cs/Protocol/NegotiationState.cs:360-363`](../telnet_cs/Protocol/NegotiationState.cs#L360-L363) (`WantNo` +
  queued swallows a positive into `Yes` silently), [`:367-370`](../telnet_cs/Protocol/NegotiationState.cs#L367-L370)
  (`WantYes` + queued answers the positive with the refuse verb),
  [`:364-366`](../telnet_cs/Protocol/NegotiationState.cs#L364-L366) (`WantNo` settles to `No` silently), [`:430-432`](../telnet_cs/Protocol/NegotiationState.cs#L430-L432)
  (re-enable behind an outstanding disable queues instead of sending).
   telnetlib3 `iac()` (`stream_writer.py:1052-1103`): `DO`/`WILL` set
  `pending` (`:1079`) and skip while pending; `DONT` sets
  `remote_option` false (`:1096`) and `WONT` sets `local_option` false
  (`:1099`) with no pending marker — nothing remembers the withdrawal,
  so the next inbound positive is answered from scratch (`:854`
  `local_option[opt] = handle_do(opt)`, agree branch in `handle_do`
  `:1982-2127`).
- Why it exists: the queue plus refuse-on-collision is the RFC 1143
  single-entry opposite-queue shape (same bucket as D1): answering a
  stale request against an outstanding opposite request resurrects an
  intent we just withdrew, and rapid toggles can ping-pong. telnetlib3
  converges too, but only after extra bytes and a window where each side
  believes the opposite. Interop impact is a deferred or withheld byte,
  never a wrong byte. Kept by design, change only on an owner decision.

## D7 — ENVIRON `ESC` escapes the full RFC 1408 set (wire decode)

- Proof: [`repro/py_d7_environ.py`](repro/py_d7_environ.py) →
  [`repro/py_d7_environ.log`](repro/py_d7_environ.log): `VAR K VALUE a ESC VALUE b` decodes
  to `{'K': 'a\x02\x01b'}` — the `ESC` is kept and the raw `VALUE`
  splits; trailing `ESC` is kept (`{'K': 'v\x02'}`); `_escape_environ`
  leaves `VALUE` (`01`) and `ESC` (`02`) bare while escaping `VAR` to
  `0200`. C# decodes the same value as `a\x01b` (escape consumed,
  literal byte) and drops a trailing `ESC`: pinned by
  `EnvironmentTests.ParseEntries_EscapedValue_StaysLiteral` (and the
  send side by `EnvironSend_ValueByte_Escaped`).
- Code: [`telnet_cs/Protocol/EnvironmentProtocol.cs:323-340`](../telnet_cs/Protocol/EnvironmentProtocol.cs#L323-L340) (escape
  `VAR`/`VALUE`/`ESC`/`USERVAR`), [`:244-273`](../telnet_cs/Protocol/EnvironmentProtocol.cs#L244-L273) (unescape; trailing `ESC`
  contributes no byte at [`:252`](../telnet_cs/Protocol/EnvironmentProtocol.cs#L252); only unescaped bytes delimit).
  telnetlib3 `_escape_environ` (`stream_writer.py:3540-3547`) replaces
  only `VAR`/`USERVAR`, `_unescape_environ` (`:3550-3557`) unescapes
  only those, and `_decode_env_buf` (`:3603-3631`) finds delimiters at
  `:3619` ignoring only `ESC`-before-`VAR`/`USERVAR`, then splits on raw
  `VALUE` at `:3631`.
- Why it exists: telnetlib3-vs-RFC conflict — RFC 1408 §2 requires
  escaping `VAR`/`VALUE`/`ESC`/`USERVAR`, and leaving `0x01`/`0x02` bare
  makes delimiter-vs-literal ambiguous in both directions for exotic
  values (send conflates, receive keeps stray escapes). Matching
  telnetlib3 would corrupt values containing those bytes. Kept by
  design; interop with conformant peers is unaffected (they escape).

## D8 — malformed/unsolicited subnegotiation is ignored, never thrown (robustness)

- Proof: [`repro/py_d8_malformed.py`](repro/py_d8_malformed.py) →
  [`repro/py_d8_malformed.log`](repro/py_d8_malformed.log): `LINEMODE` unknown sub, empty
  `MODE`, and bad byte after `DO` each raise `ValueError`; `LFLOW` with
  trailing bytes is silently adopted when agreed but a bad mode raises
  `ValueError`; `TTABLE-IS`/`TTABLE-REJECTED` raise
  `NotImplementedError`. Earlier [`repro/py_d4_raisevssilent.log`](repro/py_d4_raisevssilent.log):
  illegal `CHARSET` verb, `STATUS SEND` without `WILL`, and unsolicited
  `WILL TM` each raise `ValueError` with no bytes out. C# answers every
  one of these with `data=` and `writes=` empty ([`repro/cs_wire.log`](repro/cs_wire.log)
  runs `d8-linemode-unknown`, `d8-linemode-emptymode`,
  `d8-linemode-badbyte`, `d8-lflow-trailing`, `d8-charset-ttable`,
  `d8-status-send`): pinned by `LinemodeTests.UnknownSubcommand_Ignored`,
  `ModeTruncated_Ignored`, `ModeWithoutAgreement_Ignored`,
  `ExtendedOptionsTests.SbLineflow_TrailingBytes_IgnoredWithoutEvent`,
  `SbLineflow_UnknownMode_IgnoredWithoutEvent`,
  `SbCharsetIllegalVerb_IgnoredWithoutReplyOrEvent`,
  `SbCharsetTTableIs_IgnoredWithoutReply`,
  `SbCharsetTTableRejected_ClearsPendingWithoutReply`, and
  `StatusTimingMarkTests.StatusSend_NoAgreements_IgnoredSilently` (silent:
  no `IS`, no `WONT`).
- Code: [`telnet_cs/IO/ByteStreamHandler.cs:2303-2315`](../telnet_cs/IO/ByteStreamHandler.cs#L2303-L2315)
  (`ReplyLineflowAsync`: shape and agreement gates), [`:2635-2673`](../telnet_cs/IO/ByteStreamHandler.cs#L2635-L2673)
  (`ReplyLinemodeAsync` dispatch at [`:2635-2651`](../telnet_cs/IO/ByteStreamHandler.cs#L2635-L2651), `MODE` shape and
  agreement gates in `ReplyModeAsync`), [`:2422-2468`](../telnet_cs/IO/ByteStreamHandler.cs#L2422-L2468)
  (`ReplyCharsetAnswerAsync`: ACCEPTED/REJECTED, then table verbs
  logged and ignored at [`:2454-2468`](../telnet_cs/IO/ByteStreamHandler.cs#L2454-L2468), never answered), [`:1889-1898`](../telnet_cs/IO/ByteStreamHandler.cs#L1889-L1898)
  (`STATUS SEND` without agreement ignored). The one deliberate
  exception is the SLC triplet shape ([`ByteStreamHandler.cs:2784`](../telnet_cs/IO/ByteStreamHandler.cs#L2784)
  throws `InvalidDataException` on a non-multiple-of-3 buffer), which
  matches telnetlib3 (`stream_writer.py:2934-2935` raises `ValueError`
  on the same shape). telnetlib3: `_handle_sb_lflow`
  (`stream_writer.py:2677-2694`), `LINEMODE` dispatch (`:2816-2834`)
  and `MODE` missing-byte gate (`:2836-2846`), `STATUS` (`:2781-2812`,
  guard at `:2784`), `CHARSET` (`:2472-2477`).
- Why it exists: a 3-byte peer frame must never tear down the
  connection — exceptions out of the feed path turn malformed input
  into a remote kill switch, so the read loop logs and ignores instead.
  The SLC shape check is kept throwing because both sides agree and it
  signals a framing bug, not a peer value. Kept by design.

## D9 — unsolicited `WILL`/`WONT TIMING-MARK` ignored (robustness)

- Proof: [`repro/py_d4_raisevssilent.log`](repro/py_d4_raisevssilent.log): `WILL-TM-no-pending`
  raises `ValueError: cannot recv WILL TM, must first send DO TM`.
  telnetlib3 also raises on `WONT TM` without a prior `DO`
  (`stream_writer.py:2340`). C# stays silent ([`repro/cs_wire.log`](repro/cs_wire.log)
  run `d9-willtm`: `data=` `writes=` empty): pinned by
  `StatusTimingMarkTests.WillTimingMark_Unsolicited_IgnoredSilently`,
  `WontTimingMark_Unsolicited_IgnoredSilently`, and
  `WillTimingMark_WhileAgreedWithoutOutstanding_IgnoredSilently`.
- Code: [`telnet_cs/IO/ByteStreamHandler.cs:3002-3018`](../telnet_cs/IO/ByteStreamHandler.cs#L3002-L3018) (acts on
  `WILL`/`WONT TM` only with an outstanding `DO` — gate at [`:3002`](../telnet_cs/IO/ByteStreamHandler.cs#L3002) —
  else logs and ignores); telnetlib3 `:2249` (`WILL`) and `:2340` (`WONT`).
- Why it exists: same boundary as D8 — the halfway state (`DO` sent,
  `WILL` not yet seen) is normal on a live wire, and a stray `TM` is
  benign either way; raising turns it into a kill. The solicited path
  still records agreement (`WillTimingMark_Solicited_...`). Kept.

## D10 — `ECHO`: server-role `WILL` ignored, client opt-in gated (robustness)

- Proof: [`repro/py_d10_echo.py`](repro/py_d10_echo.py) →
  [`repro/py_d10_echo.log`](repro/py_d10_echo.log): server-role inbound `WILL ECHO`
  raises `ValueError: cannot recv WILL ECHO on server end`; client-role
  inbound `DO ECHO` is answered `WONT` every time (`writes=
  ['fffc01', 'fffc01']`, the 4.4BSD dodge). C# server-role `WILL ECHO`
  is silent ([`repro/cs_wire.log`](repro/cs_wire.log) run `d10-willecho-server`), and
  the bare-handler client `DO ECHO` also answers `WONT` (run
  `d10-doecho-twice`: `writes=FFFC01FFFC01`): pinned by
  `AuditProperNegotiationTests.Server_WillEcho_SilentWithoutStateChange`;
  `DoEcho_WithOptIn_RepliesWillAndTracksUs` /
  `DoEchoTwice_WithOptIn_RepliesWillOnce` pin the opt-in `WILL` path,
  and `WillEcho_AfterAgreedDoEcho_StaysSilent` pins the silent bounce
  guard (no `DONT` emitted).
- Code: [`telnet_cs/IO/ByteStreamHandler.cs:3045-3049`](../telnet_cs/IO/ByteStreamHandler.cs#L3045-L3049) (server-role
  `WILL ECHO` swallowed), [`:3342-3360`](../telnet_cs/IO/ByteStreamHandler.cs#L3342-L3360) (`AgreeEcho`: WILL answered
  unless we already echo; DO answered from `AllowRemoteEcho` unless the
  peer already echoes; `OfferEcho` defaults on in
  [`TelnetServerOptions.cs:29`](../telnet_cs/Server/TelnetServerOptions.cs#L29), wired at [`ServerSession.cs:453`](../telnet_cs/Server/ServerSession.cs#L453)).
  telnetlib3: `:2201` (server raise), `:2016-2022` (client answers
  `WONT`, unconditionally and unconfigurably).
- Why it exists: same boundary as D8/D9 for the server path — MUD
  clients send `WILL ECHO` benignly and killing on it is all cost, no
  gain. The client side additionally keeps a supported configuration
  (opt-in remote echo) that telnetlib3 hardcodes away; defaults
  interop-match (both refuse unless asked). Kept by design.

## D11 — server-role `WILL LOGOUT` refused with `DONT` (negotiation)

- Proof: [`repro/py_d11_logout.py`](repro/py_d11_logout.py) →
  [`repro/py_d11_logout.log`](repro/py_d11_logout.log): server-role inbound `WILL LOGOUT`
  fires the callback (`[b'\xfb']`) and sends nothing. C# sends `DONT`
  ([`repro/cs_wire.log`](repro/cs_wire.log) run `d11-willlogout-server`:
  `writes=FFFE12`): pinned by
  `ExtendedOptionsTests.WillLogout_ServerRole_RefusedWithDont`; the adjacent
  paths match telnetlib3's intent without raising — client-role `WILL`
  is swallowed (`WillLogout_ClientRole_IgnoredSilently`, cf. telnetlib3's
  client-side `ValueError` at `stream_writer.py:2255`) and server-role
  `DO` signals logoff and closes (`ServerSessionTests.DoLogout_ClosesStreamWithoutReply`;
  telnetlib3's live path only fires the callback at `:2048-2049` — its
  close-on-`DO` lives in `handle_logout` at `:1968-1970`, which is
  unwired dead code reached only by its own unit test).
- Code: [`telnet_cs/IO/ByteStreamHandler.cs:3021-3043`](../telnet_cs/IO/ByteStreamHandler.cs#L3021-L3043) (client-role
  `WILL`/`DO` handling, server-role `DO` close); a server-role `WILL`
  falls through to the generic table ([`:3051-3059`](../telnet_cs/IO/ByteStreamHandler.cs#L3051-L3059)), which refuses an
  unwanted offer, and the refusal goes out at [`:3095-3101`](../telnet_cs/IO/ByteStreamHandler.cs#L3095-L3101) (`DONT`).
  telnetlib3 `:2256` calls
  `_ext_callback[LOGOUT](WILL)`, sending nothing.
- Why it exists: negotiation rules require answering a mode-change
  request (RFC 854 rule b; RFC 1143 tracks the peer as `WantYes`) — an
  unwanted offer must be refused or the peer stays `WantYes` forever;
  silent-callback leaves negotiation dangling while looking alive. The
  client-side raise is still softened to silent-ignore per the D8
  boundary, and closing on `DO` fulfills the voluntary-logoff request
  the callback alone only announces. Kept by design.

## D12 — MCCP `START` gated on agreement, TLS state, and direction (security)

- Proof: [`repro/py_d12_mccp.py`](repro/py_d12_mccp.py) →
  [`repro/py_d12_mccp.log`](repro/py_d12_mccp.log): `IAC SB MCCP2 IAC SE` with no
  agreement activates immediately (`activated= True`,
  `callback= [True]`, `writes= []`). C# stays silent and plaintext
  ([`repro/cs_wire.log`](repro/cs_wire.log) run `d12-mccp2`): pinned by
  `MccpTests.MccpSb_WithoutAgreement_IsIgnored`,
  `NonEmptyMccpSb_WithoutAgreement_StaysPlaintext` (in
  `AuditProperMudTests`),
  `TlsMccpSb_IsIgnored`, `ServerRole_Mccp3GzipSb_RefusedWithDont`, and
  `ServerRole_PlaintextAfterMccp2Sb_ReadsRaw`.
- Code: [`telnet_cs/IO/ByteStreamHandler.cs:2235-2239`](../telnet_cs/IO/ByteStreamHandler.cs#L2235-L2239)
   (`MccpStartAllowed`: opted in, not over TLS, and agreed by either
  side — shared by the empty and padding-carrying forms), [`:1805-1836`](../telnet_cs/IO/ByteStreamHandler.cs#L1805-L1836)
  (direction: only a client inflates MCCP2, only a server MCCP3; wrong
  direction never arms), [`:1088-1106`](../telnet_cs/IO/ByteStreamHandler.cs#L1088-L1106) with deferred flush at
  [`:1263-1279`](../telnet_cs/IO/ByteStreamHandler.cs#L1263-L1279) (a corrupt stream is shut down with `WONT`/`DONT` so the
  peer is informed). telnetlib3 `_handle_sb_mccp2` (`stream_writer.py:
  3352-3363`) and `_handle_sb_mccp3` (`:3365-3375`) activate on the
  marker alone; negotiation-time refusal exists (`:2090-2093`,
  `:2225-2228` note compress-then-encrypt CRIME/BREACH), but the `SB`
  path re-opens it.
- Why it exists: starting inflation on an unauthenticated marker is a
  zlib-bomb without agreement, over TLS it is a CRIME/BREACH oracle,
  and the wrong direction only produces garbage — the gate is the
  negotiation precondition plus transport safety. The corrupt-path
  `WONT`/`DONT` is a harmless extension (informs the peer instead of
  leaving it believing compression is agreed). Kept by design.

## D13 — NAWS accepts the 5-byte verb-first shape (tolerance)

- Proof: [`repro/py_d13_naws.py`](repro/py_d13_naws.py) →
  [`repro/py_d13_naws.log`](repro/py_d13_naws.log): the strict 4-byte shape is accepted;
  the 5-byte verb-first shape raises `struct.error` out of `feed_byte`.
  C# accepts both and stays silent on the wire ([`repro/cs_wire.log`](repro/cs_wire.log)
  run `d13-naws5`): pinned by `AuditProperOptionsTests.NawsVerbFirst_Accepted`
  (size stored from the verb-first frame) and
  `ServerSessionTests.InboundNaws_BareShape_SetsClientWindowSize`
  (strict 4-byte shape).
- Code: [`telnet_cs/Server/ServerSession.Collectors.cs:1425-1448`](../telnet_cs/Server/ServerSession.Collectors.cs#L1425-L1448)
  (verb-first 5-byte is this stack's own shape; strict RFC 1073 4-byte
  also accepted). telnetlib3 `_handle_sb_naws`
  (`stream_writer.py:2649-2668`) unpacks unconditionally (`:2668`).
- Why it exists: our own stack emits verb-first, so strictness would
  drop real peer data and crash the reader on one surplus byte; the
  tolerance accepts both shapes. A fix belongs on the raising side.
  Kept by design.

## D14 — TSPEED validation is strict digit-only and string-preserving (wire honesty)

- Proof: [`repro/py_d14_tspeed.py`](repro/py_d14_tspeed.py) →
  [`repro/py_d14_tspeed.log`](repro/py_d14_tspeed.log): the `signed-plus` case (`+9600,9600`)
  reports `[(9600, 9600)]` and `signed-minus` (`-1,0`) reports
  `[(-1, 0)]` — `int()` accepts both. C# rejects both: pinned by
  `TerminalTypeSpeedTests.SpeedValidate_SignedRates_Rejected`; a
  rejected `IS` leaves `ClientTerminalSpeed` null (doc at
  `ServerSession.Collectors.cs:136-140`).
- Code: [`telnet_cs/Protocol/TerminalSpeedProtocol.cs:30-42`](../telnet_cs/Protocol/TerminalSpeedProtocol.cs#L30-L42)
  (`Validate`: at least two comma-separated parts, first two must be
  non-empty all-ASCII-digit, leading zeros stripped for RFC 1079 §4
  send compliance, values otherwise preserved) with `NormalizeRate` at [`:62-79`](../telnet_cs/Protocol/TerminalSpeedProtocol.cs#L62-L79). telnetlib3
  `:2500` parses with `int()` and reports at `:2506`. (Field order is
  D5, not this entry.)
- Why it exists: RFC 1079 §4 rates are unsigned decimals — `-1` is a
  meaningless speed, `+` is not in the grammar, and `int()` both hides
  malformed senders and loses the original spelling. String-preserving
  validation keeps the wire honest; only asymmetric exotic senders
  notice. Kept by design.

## D15 — SNDLOC: blank explicit-send throws, `SEND` never auto-answered (API safety)

- Proof: [`repro/py_d15_sndloc.py`](repro/py_d15_sndloc.py) →
  [`repro/py_d15_sndloc.log`](repro/py_d15_sndloc.log): `handle_send_sndloc()` returns `''`.
  C# throws on blank input: pinned by
  `ExtendedOptionsTests.SendLocationPayload_Blank_Throws`; the receive
  framing matches (raw ASCII, stored and hooked, never replied — pinned
  by `SbSendLocation_SurfacesRawLocation`), and an unconfigured location
  sends nothing (`DoSendLocation_WithoutConfiguredLocation_SendsNoSb`).
- Code: [`telnet_cs/IO/ByteStreamHandler.cs:2494-2499`](../telnet_cs/IO/ByteStreamHandler.cs#L2494-L2499)
  (`SendLocationPayloadAsync`: `ThrowIfNullOrWhiteSpace`), receive at
  [`:2285-2291`](../telnet_cs/IO/ByteStreamHandler.cs#L2285-L2291) plus [`ServerSession.Collectors.cs:1725-1732`](../telnet_cs/Server/ServerSession.Collectors.cs#L1725-L1732). telnetlib3
  `handle_send_sndloc` (`stream_writer.py:1834-1837`) returns `""`, received
  by `_handle_sb_sndloc` (`:2621-2625`); neither side auto-answers
  `SEND`.
- Why it exists: emitting an empty `SB SNDLOC` is wire noise — throwing
  turns a caller bug into a loud local error instead of a meaningless
  peer frame. No wire gain either way; the receive path already agrees.
  Kept by design.

## D16 — MSDP non-list enumerables use display form (shape stability)

- Proof: [`repro/py_d16_msdp.py`](repro/py_d16_msdp.py) →
  [`repro/py_d16_msdp.log`](repro/py_d16_msdp.log): `{'K': ('a', 'b')}` encodes to
  `014b02282761272c2027622729` — `VAR K VAL "('a', 'b')"`, the Python
  `repr` on the wire; `{'K': ['a', 'b']}` uses `ARRAY` framing
  (`...02050261026206`). C# encodes a ValueTuple as its display form
  `"(a, b)"`: pinned by `MudProtocolTests.MsdpEncode_ValueTuple_UsesDisplayForm`.
- Code: [`telnet_cs/Protocol/MudProtocol.cs:242-256`](../telnet_cs/Protocol/MudProtocol.cs#L242-L256) (non-enumerable
  values fall through to `Convert.ToString`; a non-list `IEnumerable`
  still takes `ARRAY`); lists agree (`ARRAY` both sides). telnetlib3
  `mud.py:101-128` (`dict` → table, `list` → array, everything else
  `str(value)`, framed at `:125-128`).
- Why it exists: neither `repr` is a wire spec — switching tuples to
  `ARRAY` unilaterally would flip string→structured type for existing
  peers. Keeping the scalar shape is the conservative choice; changing
  it needs a coordinated spec, not unilateral alignment. Kept.

## D17 — Aardwolf 1-byte frames yield empty `DataBytes` (API totality)

- Proof: [`repro/py_d17_aardwolf.py`](repro/py_d17_aardwolf.py) →
  [`repro/py_d17_aardwolf.log`](repro/py_d17_aardwolf.log): `[0x41]` decodes to
  `{'channel': '0x41', 'channel_byte': 65}` with no `data_byte` /
  `data_bytes` keys; `[0x41, 0x07]` adds both. C# yields `DataBytes`
  empty (never null): pinned by
  `MudDispatchTests.SbAardwolf_SingleByte_YieldsEmptyData`.
- Code: [`telnet_cs/Protocol/MudProtocol.cs:575-590`](../telnet_cs/Protocol/MudProtocol.cs#L575-L590) (length switch;
  1-byte carries channel only). telnetlib3 `mud.py:348-365` (keys
  present only by length).
- Why it exists: a non-null empty array keeps consumers
  (`Length`/`foreach`) total; only key-presence checks on exotic
  1-byte frames diverge. Info-grade. Kept by design.

## D18 — SyncTERM font id capped, scan continues (robustness)

- Proof: [`repro/py_d18_syncterm.py`](repro/py_d18_syncterm.py) →
  [`repro/py_d18_syncterm.log`](repro/py_d18_syncterm.log): `\x1b[0;10000 D` detects as
  `None`, and — worse — `\x1b[0;10000 D\x1b[0;0 D` also detects as
  `None`: first-match wins, so a poison id shadows the valid later
  reply (`0` alone → `cp437`). C# skips the oversize id and matches the
  later one: pinned by
  `PolicyAccessoriesTests.SyncTermFont_DetectEncoding_SkipsOversizedId`.
- Code: [`telnet_cs/Protocol/SyncTermFont.cs:121-137`](../telnet_cs/Protocol/SyncTermFont.cs#L121-L137) (accumulate,
  reject above 9999) with `DetectEncoding` at [`:68-101`](../telnet_cs/Protocol/SyncTermFont.cs#L68-L101) continuing the
  scan past a failed number. telnetlib3 `server_fingerprinting.py:190`
  (unbounded `\d+`) with first-match search and map lookup at
  `:251-255`.
- Why it exists: an unbounded first-match lets one poison id discard a
  valid later signal and risks huge-`int` parses; cap-and-continue is
  strictly more robust, and the fix belongs on the matching side. Kept.

## D19 — ZMP answers support from handler OR advertised list (config convergence)

- Proof: [`repro/py_d19_zmp.py`](repro/py_d19_zmp.py) →
  [`repro/py_d19_zmp.log`](repro/py_d19_zmp.log) (real `TelnetClient.on_zmp`, writer
  stubbed): with `zmp_supported_commands={'look'}` and no handler,
  both `zmp.check look` and `zmp.send-support look` answer
  `[('zmp.no-support', 'look')]`; with a handler returning true (and
  nothing listed), `zmp.check look` answers
  `[('zmp.support', 'look')]`. C# answers `support` in both listed
  cases: pinned by `MudDispatchTests.SbZmpCheck_HandlerApproved_SendsSupport`,
  `SbZmpCheck_ListedWithoutHandler_SendsSupport`,
  `SbZmpCheck_Unapproved_SendsNoSupport`,
  `SbZmpSendSupport_HandlerApproved_SendsSupport`,
  `SbZmpSendSupport_ListedWithoutHandler_SendsSupport`, and
  `SbZmpSendSupport_UnlistedWithoutHandler_SendsNoSupport`.
- Code: [`telnet_cs/IO/ByteStreamHandler.cs:2118-2176`](../telnet_cs/IO/ByteStreamHandler.cs#L2118-L2176) (approved is
  handler-true OR list-contained on both paths; bare `send-support`
  still advertises the sorted list). telnetlib3 `client.py:232-263`
  (`on_zmp` answers both paths via `_respond_zmp_support` at `:252`,
  which consults only `zmp_check` at `:259` — handler or false).
- Why it exists: the list is the documented "advertise these commands"
  configuration (ident-time `support` frames go out for it), so
  handler-only answers would make list-only setups lie — advertise then
  refuse. The OR-policy converges both setup styles; handler-only and
  list-only deployments each behave as documented. Kept by design.

## D20 — TTYPE chain hygiene: empties dropped, repeats stripped, loop capped (storage)

- Proof: [`repro/py_d20_ttype.py`](repro/py_d20_ttype.py) →
  [`repro/py_d20_ttype.log`](repro/py_d20_ttype.log) (real `TelnetServer.on_ttype`,
  `request_ttype` recorded): a fresh answer continues the cycle
  (`sends= 1`); empty `ttype1` is stored (`{'ttype1': ''}`) and stops
  the cycle (`sends= 0`); a `ttype1 == ttype2` duplicate is kept
  (`ttype2` stored, `TERM` overwritten) and stops it; past the loop
  max the over-cap answer is still recorded (`ttype9` stored, `TERM`
  overwritten again) before stopping. C# drops empties, strips the
  terminating duplicate repeat, keeps the third-slot `MTTS` vector, and
  records the over-cap answer once into the overflow slot before
  stopping: pinned
  by `ServerSessionTests.RequestTerminalTypesAsync_SkipsEmptyAnswers`,
  `RequestTerminalTypesAsync_CollectsChainUntilRepeat`,
  `RequestTerminalTypesAsync_NonConsecutiveRepeat_StopsAtFirstRepeat`,
  `RequestTerminalTypesAsync_BeyondLoopMax_StopsAtCap`,
  `RequestTerminalTypesAsync_OverflowPastCap_StopsAndReleasesEnviron`,
  and the `RequestTerminalTypesAsync_MttsThirdEntry_EffectiveTypeIsSecond`
  (`ExtendedCollectorsTests`) /
  `RequestTerminalTypesAsync_LowercaseMttsThird_StopsWithSecondEffective`
  (`ServerSessionTests`) variants.
- Code: [`telnet_cs/Server/ServerSession.Collectors.cs`](../telnet_cs/Server/ServerSession.Collectors.cs) (empty answers
  end the turn without advancing at [`:1552-1558`](../telnet_cs/Server/ServerSession.Collectors.cs#L1552-L1558); the returned list
  ends at the first repeat — first-entry loop or previous-entry repeat
  (terminator excluded) at [`:1600-1612`](../telnet_cs/Server/ServerSession.Collectors.cs#L1600-L1612) and [`:609-618`](../telnet_cs/Server/ServerSession.Collectors.cs#L609-L618) — while a
  third-slot `MTTS` vector stops the cycle but is kept in the chain;
  past-`TerminalTypeLoopMax` ([`:328`](../telnet_cs/Server/ServerSession.Collectors.cs#L328)) the over-cap answer is recorded
  once into the overflow slot and the cycle stops at [`:1576-1588`](../telnet_cs/Server/ServerSession.Collectors.cs#L1576-L1588)).
  telnetlib3 `server.py:602-653` stores every answer including `""`
  (`:611-614`) and stops the cycle on empty/dup/over-cap
  (`:630-648`), but keeps the stored dup/empty/over-cap entry (and the
  `TERM` overwrite for non-empty entries — an empty answer stores `''`
  without touching `TERM`), with `TTYPE_LOOPMAX = 8` at `:90`.
- Why it exists: storing `""`/dups/over-cap answers pollutes the chain
  (`TERM` selection, environ gating) — both sides stop the cycle, but
  only C# keeps the stored chain clean. Info-grade interop impact (same
  terminal discovered either way). Kept by design.

## D21 — CHARSET receive leniency with empty-name discipline (RFC 2066 §2)

- Proof: [`repro/py_d21_charset.py`](repro/py_d21_charset.py) →
  [`repro/py_d21_charset.log`](repro/py_d21_charset.log) (real client selection policy
  wired in): `REQUEST USASCII` is answered `ACCEPTED` with an
  **empty** body (`writes= ['fffa2a02fff0']` — unresolvable alias falls
   through to `""`, and `"" is not None` still selects); empty
  `ACCEPTED` is adopted (`accepted cb: ''`, `environ= ''`,
  `force_binary= True`, no raise). C# answers `ACCEPTED "USASCII"`
  ([`repro/cs_wire.log`](repro/cs_wire.log) run `d21-request-usascii`:
  `writes=FFFA2A0255534153434949FFF0`, `NegotiatedCharset=USASCII`),
  rejects empty/whitespace-only offers (`ExtendedOptionsTests.SbCharsetRequest_EmptyOffers_AnswersRejected`),
  and routes empty `ACCEPTED` to the rejection path (pending cleared,
  `CharsetRejected`, no binary latch — pinned by
  `ExtendedOptionsTests.SbCharsetAccepted_EmptyName_TakesRejectionPath`); alias and
  separator leniency are pinned in `MudProtocolTests`
  (`CharsetSelect_NormalizesNames`: `US ASCII`, `ISO-8859-01`;
  `CharsetSelect_WeakDefaultAcceptsFirstViable`: weak-default
  fallthrough).
- Code: [`telnet_cs/Protocol/CharsetProtocol.cs:48-71`](../telnet_cs/Protocol/CharsetProtocol.cs#L48-L71) (`BuildRequest`
  separator fallback), [`:96-123`](../telnet_cs/Protocol/CharsetProtocol.cs#L96-L123) (`ParseRequest` space re-split),
  [`:266-328`](../telnet_cs/Protocol/CharsetProtocol.cs#L266-L328) (alias superset incl. `USASCII` and `cpNNNN`);
  [`telnet_cs/IO/ByteStreamHandler.cs:2357-2366`](../telnet_cs/IO/ByteStreamHandler.cs#L2357-L2366) (selector, null →
  `REJECTED`), [`:2424-2437`](../telnet_cs/IO/ByteStreamHandler.cs#L2424-L2437) (empty `ACCEPTED` → rejection path),
  [`ServerSession.Collectors.cs:1756-1767`](../telnet_cs/Server/ServerSession.Collectors.cs#L1756-L1767) (server side, same rule).
  telnetlib3: `handle_send_client_charset` (`stream_writer.py:1946-1954`)
  returns `""` for unresolvable names, `_handle_sb_charset`
  (`:2441-2463`) treats any non-`None` as selection, `ACCEPTED`
  (`:2464-2469`) adopts unconditionally with forced binary.
- Why it exists: RFC 2066 §2 — `ACCEPTED` carries a charset identical
  to a requested name, so empty matches nothing; adopting `''` latches
  binary with an unresolvable encoding and propagates a meaningless
  name to the app (telnetlib3's own docstring says `""` means "no
  selection", but the handler treats it as one — internal
  contradiction). Receive leniency (separator/alias) accepts
  non-conformant senders without changing our conformant sends. Kept.
