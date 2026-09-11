# fix_plan.md — RFC coverage additions + deviation fixes + missing-feature implementations

Baseline: green tree, 429/429 tests pass. All new tests are byte-exact, hermetic via
`ScriptedStream` unless noted. Every behavior change cites its RFC section and is pinned by a
test (repo wire-discipline rule). All file:line refs below were verified against the tree
(second audit pass; corrections from that pass are marked AUDIT).

Public negotiation API (there is no public `Initiate*`):
`RequestEnable/RequestDisable/OfferEnable/OfferDisable` and
`ReceivedWill/ReceivedWont/ReceivedDo/ReceivedDont` (`Protocol/NegotiationState.cs:139-304`);
queries `IsEnabledByPeer/IsEnabledByUs/WasRefusedByPeer/WasRefusedByUs/GetStates` (`:56-127`).
No public outstanding/queued indicator; no queue-off toggle (`:46-47`, `:114-127`).

## 0. Architecture constraint (AUDIT — shapes §4)

There is no local line discipline anywhere in this library: no console-input path, no
EDIT-mode input buffer, no keystroke→function translation. `LocalEchoEnabled` is
`Console.Write` of *received* text (`IO/ByteStreamHandler.Reading.cs:102-111`), and
`DecodeResult` (`:136-144`) returns the caret-mapped string (`"^C"`/`"^D"` at
`IO/ByteStreamHandler.cs:336-341`), which is also the `Read()` return value (data).
`LinemodeState` is consulted only on the negotiation-reply path
(`ReplyLinemodeAsync`, `ByteStreamHandler.cs:780-819`). Consequences:
- Anything requiring EDIT-mode local buffering/forwarding (FORWARDMASK acceptance) or
  typed-input echo rendering (SOFT_TAB/LIT_ECHO) has no hook point. Accepting such MODE
  bits or FORWARDMASK would promise behavior that cannot execute — dishonest negotiation.
  These are documented non-goals (§5), not deferred work. The current refuse/drop behavior
  is already pinned and stays.
- Feasible linemode work is network-side only: SLC import reply shape, FLUSH actions on
  the send path, FORW2 receipt validation. (In-tree evidence, not just inference:
  `ReplyForwardMaskAsync` refuses because "this client never forwards buffered input"
  (`:823-826`, `:842`); `NawsProtocol` documents "the client sends them and never parses
  inbound NAWS" (`:4-6`).)

## 1. Library fixes: spec deviations → match the RFC + test

### 1.1 GA gating (RFC 858 §5)

Today `IO/ByteStreamHandler.cs:521-527` swallows inbound `IAC GA` unconditionally (same arm
as NOP/DM/SE), with no read of negotiation state — the comment even claims "a NOP while
Suppress-GA holds", but nothing checks that it holds.
Fix: swallow only when the peer suppresses GA on its transmit path, i.e.
`Negotiation.IsEnabledByPeer((int)Options.SuppressGoAhead)` (`Options.cs:15` = 3, him-side
`== SideState.Yes` per `NegotiationState.cs:56-62`). When not agreed, raise a dedicated
notification instead of embedding text in the data stream (DECIDED: user chose a clean API
over a text marker). Design: event/callback on the shared handler (e.g. `GoAheadReceived`),
surfaced through `Client` and `ServerSession`, so consumers implement turn-taking without
parsing `Read()` output. Raise it synchronously on the read path — the handler has no
callback threading model, so don't invent one. Rationale: GA is a control-plane flow signal, unlike the
`[BRK]`/`[EOF]` data-path markers (`:501-519`); embedding it as text forces every consumer
to filter it. One fix covers client and server (shared handler). The check
is him-side because agreed SGA governs the *peer's* transmission; our own WILL SGA is
irrelevant to inbound bytes. RFC 858 demands each direction be suppressed independently
(`rfc858.txt:79-81`), so reading the him-side (our DO accepted → peer's WILL) is the
correct half, not an arbitrary pick.
Why fix instead of pin: under default NVT (SGA never negotiated) GA is the turn-taking
signal (RFC 854: server sends GA when blocked waiting for keyboard input). Swallowing it
deletes the signal existing peers send; RFC 858 treats a received GA as NOP only *when
suppression is in effect*. Risk is contained: sessions with SGA agreed (what this library
negotiates proactively) behave byte-identically to today.
Existing test to modify (AUDIT — will break, not "verify"): `SwallowedCommands_StaySilent`
(`Tests/Client/ControlSignalTests.cs:161-166`) runs a fresh handler with no negotiation
(`:21-28`), so its `InlineData(249)` case (`:156-160`) expects silence for an *unsuppressed*
GA. Split GA out of that theory; add `GaUnsuppressed_RaisesNotification` (event fires,
output empty, no writes) and `GaSuppressedWhenSgaAgreed_Silent` (agree SGA first, then GA
silent, no event). SE/NOP/DM cases stay untouched — stray DM without urgent indication
stays silent (consistent with §3). DECIDED: dedicated event, not a `[GA]` text marker.

### 1.2 TTYPE case-insensitivity (RFC 1091 §5–6, type names are case-insensitive)

RFC text (`rfc1091.txt:135-136`): "Within this string, upper and lower case are
considered equivalent."
Case-sensitive sites, all verified: `Protocol/TerminalTypeCycler.cs:38`
(`SequenceEqual(..., StringComparer.Ordinal)`), `Server/ServerSession.Collectors.cs:272`
(duplicate-terminator `==`), `:113-115` (end-of-list strip `==`),
`Client/Client.StateHydration.cs:57` (inherits cycler `Matches`). Fix: all four to
`OrdinalIgnoreCase`. No `OrdinalIgnoreCase` exists anywhere in the TTYPE path today.
Why fix instead of pin: end-of-list detection breaks against any peer that varies case
(`xterm` vs `XTERM` never compares equal), so duplicate termination never fires and the
server keeps SENDing forever — an interop livelock, not a cosmetic difference.
Existing same-case tests stay green: `TypeSend_WalksListAcrossReads`
(`Tests/Protocol/TerminalTypeSpeedTests.cs:53`), `TypeSend_SameTwiceThenWraps` (`:71`),
`RequestTerminalTypesAsync_CollectsChainUntilRepeat`
(`Tests/Server/ServerSessionTests.cs:315`, pins `aaa,bbb,bbb` → `aaa,bbb` at `:272-276`).
Tests: mixed-case walk termination (`Xterm,xterm`); server mixed-case double
(`AAA,bbb,BBB` terminates — the meaningful triple-repeat-compat case, since the server
structurally stops at the first double and a classic triple can never arrive); cycler
`Matches` mixed-case. `UnescapesIacInValue` (`:326`) and `TimesOutEmpty` (`:337`)
unaffected.

### 1.3 STATUS SEND gating (RFC 859 §5: only the WILL-sender may send IS)

Today `ReplySendAsync` answers *any* `SB STATUS SEND` with IS regardless of role
(`ByteStreamHandler.cs:686-688`, doc at `:760-764` admits it). Fix: answer IS only when
our side is the agreed WILL-sender (`Negotiation.IsEnabledByUs(STATUS)`, i.e. us `== Yes`);
otherwise reply `WONT` — symmetric with the existing stray-IS→WONT (`:650-654`, pinned by
`StatusSend_StrayIs_GetsWont` at `Tests/Protocol/StatusTimingMarkTests.cs:92-96`).
`StatusProtocol.BuildIsPayload` (`Protocol/StatusProtocol.cs:27-48`, WantYes→WILL/DO mapping
at `:36-46`) needs no change: the us-side WantYes arm goes quiet only because SENDs no
longer earn IS answers there, and the him-side outstanding mapping is still required.
DECIDED: strict `Yes`-only (lenient `WantYes` answers rejected — pre-agreement leak).
Why fix instead of pin: WILL/DO is a permission grant; answering SEND unrequested leaks
full option state to unordered peers and contradicts the stray-IS→WONT half already
implemented.
Existing tests to rewrite (AUDIT: blast radius is eight, not six — every SEND-based test
below agrees SGA-or-other options but never STATUS itself, so under strict gating each
SEND now earns WONT; each rewrite prepends `DO STATUS` (`255,253,5`) so us-STATUS is `Yes`
before the SEND, adding one `WILL STATUS` (`255,251,5`) write. The two the audit missed:
`StatusSend_AnsweredFromSessionState` (server) and `StatusSnapshot_ReportsAgreedEcho`
(handler-level ECHO); both found by failing, both fixed the same way. Full SEND-site
inventory: grep `250, 5, 1` in tests.)
`StatusSend_NoAgreements_ReturnsBareIs` (`:31-36`) → expect `IAC WONT STATUS`
(`255,252,5`) instead of bare-SE `255,250,5,0,240` (bare terminator per `FrameStatusIs`,
`StatusProtocol.cs:54-59`); `StatusSend_AfterDoSga_ReportsWillSga` (`:39-46`),
`StatusSend_AfterWillSga_ReportsDoSga` (`:49-56`), `StatusSend_RefusedOption_Omitted`
(`:59-67`), `StatusSend_AfterRevoke_OmitsAgain` (`:70-78`),
`StatusSend_BothSides_ReportsBoth` (`:81-89`) → same IS expectations with the extra
`WILL STATUS` write in position. Also correct the stale `ECHO is agreed since P9` comment
(`:61`): this harness uses empty configure, nothing agrees ECHO, and the expectations
show no ECHO entries.
New tests: stray SEND pre-agreement → WONT; SEND while us-WantYes → WONT (strict).
AUDIT: also audit the setups of `StatusSend_ReportsOutstandingDo` (`:163`), `StatusSend_ReportsOutstandingWill`
(`:178`), `StatusSend_SeOptionByte_Doubled` (`:197`) during implementation — any SEND answered while
us≠Yes becomes WONT.

### 1.4 SLC import reply shape (RFC 1184 §2.4 func-0 rule + §5.9 reply table)

RFC text (`docs/telnet-rfcs/rfc1184.txt:370-381`): only the client may send func-0
triplets; on `(0,DEFAULT,0)` the server must send its table, rendering unsupported editing
functions as `XXX,DEFAULT,0` rather than NOSUPPORT "so that the client may choose to use
its own values"; on `(0,VALUE,0)` it sends current settings. Today:
- `TryConsumeLinemodeImport` (`Server/ServerSession.Linemode.cs:138-159`) consumes only the
  exact `(0,DEFAULT,0)` shape and answers `ExportTriplets()`, which *omits* NOSUPPORT rows
  (`Protocol/LinemodeState.cs:199-206`) and goes silent when empty (`:150-154`, pinned by
  `InboundImportRequest_EmptyTable_IsSilent` at `Tests/Server/ServerSessionTests.cs:685`).
- Client-side inbound func-0 triplets fall through to `ReplySlcAsync`
  (`ByteStreamHandler.cs:851-879`) → `ApplySlc` returns `(DEFAULT,0)` for func 0
  (`LinemodeState.cs:129-134`), echoing the import request back — a peer-violation path
  today, but the echo can ping-pong against a buggy server (echoed import → full table
  export → …). No test pins the echo (verified).
Fix:
1. Add import-mode export (e.g. `ExportTriplets(forImport:true)`, third `ExportTriplets`
   caller alongside `ServerSession.Linemode.cs:82`, `:149` and
   `Client/Client.Linemode.cs:45`): every NOSUPPORT row in 1..30 renders as
   `[func,DEFAULT,0]`. All-rows (not editing-subset): a DEFAULT triplet is inert by
   construction ("use your own value"), so no function allowlist or new constants are
   needed and nothing can be wrongly disabled. Use it only for `(0,DEFAULT,0)` answers;
   `PublishSpecialCharactersAsync`/`ExportSpecialCharactersAsync` keep omitting NOSUPPORT
   (pinned by `PublishSpecialCharactersAsync_EmptyTable_SendsNothing` at
   `ServerSessionTests.cs:636` and `ExportSpecialCharacters_EmptyTable_Silent` at
   `Tests/Protocol/LinemodeTests.cs:274`).
2. Empty table + `(0,DEFAULT,0)` → send the 30-triplet all-DEFAULT frame, not silence
   (the RFC says "send all those special characters"; silence leaves the requester
   hanging). Rewrite `InboundImportRequest_EmptyTable_IsSilent` → `..._SendsDefaultTable`.
3. Extend the session hook to consume `(0,VALUE,0)` → normal export (omit NOSUPPORT,
   silent-if-empty: current settings, not defaults). Client-side `ApplySlc` func-0 →
   null (drop silently). Net matrix: server exact-import → DEFAULT table; server
   send-current → current table; client inbound func-0 (peer violation) → silent.
No "switch to system defaults" reset: the configured table IS the system default; there
is nothing to switch (stating this so it is not filed as a gap later).
Why fix instead of pin: omission forces the peer to guess; NOSUPPORT would force it to
disable functions it supports; silence violates the SEND/answer exchange. All three
currently observable behaviors contradict the cited paragraphs.
Func>30 → `(DEFAULT,0)` refusal stays (`SlcUnknownFunction_RefusedAsDefault`,
`LinemodeTests.cs:213-217`); same-level-ACK silent adopt stays (`SlcAckedChange_SwitchesSilently`,
`:182-196`); malformed triplets stay silent (`InboundSlc_MalformedTriplets_AreSilent`,
`ServerSessionTests.cs:695`).

## 2. New tests, no behavior change (maximal sweep)

- **Q-method toggle-twice cancels queue** (RFC 1143 §5), verified at
  `NegotiationState.cs:402-404` (enable clears `WantYes`-queued) and `:424-426` (disable
  clears `WantNo`-queued), reached via `Request*/Offer*` (`:236`,`:258`,`:281`,`:304`).
  Existing queue tests (`Tests/Protocol/NegotiationStateTests.cs:127-159`) cover only
  drain-on-complete and drop-on-refuse. Add: `RequestEnable` while him-`WantYes`-queued →
  null + cleared (repeat sends nothing); `OfferDisable` while us-`WantNo`-queued mirror.
- **Cross-side independence** (RFC 1143 §1/§7), verified: `ReceivedWill` touches only
  him/refusedByPeer (`:140-151`), `ReceivedDo` only us/refusedByUs (`:185-196`). Add:
  inbound WILL while us-side `WantYes`-outstanding transitions him only (`GetStates`
  confirms, no reply bytes); `ReceivedDo`-vs-him mirror.
- **HT passthrough** (RFC 854 NVT): VT/FF→`Environment.NewLine` is pinned
  (`ByteStreamHandlerProtocolTests.cs:52-66`) but there is no HT(9) case and no `case 9`
  in the handler (`:333-334` STX→TAB, `:369-371` VT/FF) — HT falls to default and emits a
  literal tab. Add `InlineData(9, "\t")` pinning the passthrough. (AUDIT: v1 wrongly
  claimed HT covered.)
- **TSPEED SEND leniency**: verified the handler ignores `payload[1..]`
  (`ByteStreamHandler.cs:673-682`), so SEND with trailing garbage is answered identically
  to bare SEND. Add byte-exact test pinning that (deliberate leniency, not an accident).
  The test must configure a valid speed: with null/malformed `TerminalSpeed` the handler
  takes the silent no-reply path (`:674-679`), which would pin the wrong behavior.
  Server-side unsolicited-IS→unconsumed→WONT (`Collectors.cs:292-295`) and
  malformed-solicited→stored-null-no-reply (`:300-301`): verify against existing TSPEED
  cases during implementation, add only what is unpinned.
- **Q-method table exhaustion** (RFC 1143 §7). Him-side uncovered rows: WILL in
  WANTNO-EMPTY (`NegotiationState.cs:338-340`) and WANTNO-OPPOSITE (`:334-337`);
  local-enable in YES / WANTNO-OPPOSITE / WANTYES-OPPOSITE (`:402-406`); local-disable in
  NO / WANTNO-EMPTY(redundant) / WANTYES-OPPOSITE (`:421-428`). Us-side uncovered mirrors:
  DO in WANTNO-EMPTY/OPPOSITE and WANTYES-EMPTY; DONT in WANTNO-EMPTY/OPPOSITE and
  WANTYES-OPPOSITE; local-enable everywhere except U-NO;   local-disable everywhere except U-WANTYES-EMPTY (enumerate; each asserts exact reply or
  silence plus resulting `GetStates`).
- **Simultaneous-open ack is silent** (RFC 1143 §7: identical simultaneous requests ack each
  other). Him-`WantYes`-EMPTY + inbound WILL(agree) → null + `Yes` (no reply bytes — replying
  would oscillate). Us-side mirror with DO. Name: `SimultaneousOpen_AckOfAck_Silent`.
- **Mid-SB verbs abort framing** (verified `ByteStreamHandler.cs:609-610`: `IAC` + non-SE/IAC
  inside SB gives up; `:631-638` hook / `:640+` dispatch never run for the partial).
  Test: `SB STATUS` + `IAC WONT ECHO` + `IAC SE` + trailing text → trailing text surfaces,
  zero negotiation replies. Over-cap resync (`:564-568`, `:613-629`) already covered by
  existing tests — add only the verb-abort case.
- **ENVIRON empty-vs-undefined** (RFC 1408 §2): no test covers `ParseEntries`
  VALUE-then-type/end (defined-empty `""`) vs type-with-no-VALUE (undefined null).
  `ServerSessionTests.cs:435` pins undefined + ESC only (body `:435-444`, no empty-string
  case). Add `ParseEntries_EmptyValue_DistinctFromUndefined`. (AUDIT CORRECTION with
  reasoning: this bullet previously also claimed "no test covers SEND with a bare type and
  no name" and proposed `EnvironSend_BareType_ReturnsAllOfType`. That claim was written
  from test *names* without reading bodies — the exact failure mode behind this audit.
  Reading `EnvironmentTests.cs:64-144` end-to-end shows `EnvironSend_VarRequest_ExcludesUserVars`
  (`:74-80`) sends bare `SEND,VAR` with no name (`255,250,36,1,0,255,240`) and
  `EnvironSend_UserVarOnly_OmitsWellKnown` (`:83-88`) sends bare `SEND,USERVAR` — both
  bare-type shapes are already pinned, so the proposed test is dropped and only the
  ParseEntries gap remains.)
- **NAWS matrix pins** (no behavior change): server zero-dims stored verbatim (advisory;
  clamp is client-side `NawsProtocol.cs:21-29` only); client inbound NAWS IS → WONT
  (falls to `:650-654` — client never parses NAWS; pin with width<256 so `payload[0]`
  (width-high) ≠ 1, else the frame hits the SEND arm and goes log-only); SEND → log-only. Verify against
  `NawsRefreshTests` (`:24`,`:46`,`:63`,`:77`,`:103`) and `ServerSessionTests.cs:404,415,425`
  during implementation; add only what is unpinned.

## 3. xterm-usable features (implement + test)

### 3.1 NAWS: pin, don't change
Server matrix (verb-first `:241-244`, bare `:246-249`, malformed-ignored `:251-253`,
pre-agreement accepted, zero stored verbatim) is RFC-defensible advisory behavior; client
never parses inbound NAWS. See §2 NAWS pins. No wire change.

### 3.2 X-DISPLAY-LOCATION (RFC 1408 §5 recency rule; AUDIT CORRECTION — option 35, not an ENVIRON var)
RFC 1408 (`rfc1408.txt:187-196`) contrasts the DISPLAY *environment variable* with "the
Telnet X-DISPLAY-LOCATION option[4]" (RFC 1096, option 35): the conflicting value arrives
via `IAC SB 35 IS <display> IAC SE`, NOT inside ENVIRON. (AUDIT: a previous correction
named option 49 — wrong; 49 is the unrelated Forward-X-Window-System slot. RFC 1096
assigns XDISPLOC = 35, matching `Options.XDisplay`.) An earlier draft told the
implementation to "store it like any other var" — wrong layer entirely. Reality: the
library holds only the enum (`Options.cs:78-79`, the sole `XDisplay` reference repo-wide)
and `WeAgree` (`ByteStreamHandler.cs:928-939`) agrees to 9 options, not 35 — so today
WILL/DO 35 is refused and any `SB 35` dies as stray-IS→WONT / SEND log-only. Implement
as a TTYPE mirror (server DO → client WILL → server SEND → client IS; confirm wire roles
against RFC 1096 during implementation — not vendored in `docs/`): (1) add `XDisplay` to
`WeAgree`; (2) server parses `SB 35 IS` (Latin-1 display string, role-gated like TTYPE)
into dedicated `clientXDisplay` storage sharing an arrival sequence with ENVIRON DISPLAY
writes; (3) client answers `SB 35 SEND` from its configured display value; (4)
effective-display getter (DECIDED) resolves last-arrived-wins across the two sources —
NOT inside `ClientEnvironment` (that dict holds ENVIRON vars only). Tests: option-35 IS
before and after ENVIRON DISPLAY in both orders (effective flips), interleaved unrelated
vars, unsolicited-IS handling, SEND-side role gate.

### 3.3 Synch-receive (RFC 854 Synch; DECIDED: probe with fallback)
Core (hermetic, always delivered): discard-until-`IAC DM` scan; interesting signals pass
(IP cancels reads / AYT proof / AO log — existing `:479-492` handlers plug in), all other
Telnet commands pass, EC/EL do NOT pass (RFC 854 excludes them); end-of-urgent-before-DM
keeps discarding; post-DM urgent starts a new Synch. Unit-test the core via an internal
hook. Trigger (best-effort): Transport probe (e.g. `Poll`-based urgent check on
`TcpByteStream`/`ISocket`, `:210-230` / `:68-82`) wired into the read loop, plus a public
client receive wrapper mirroring the server's (`ServerSession.Linemode.cs:119`;
same `is TcpByteStream` gate pattern inside, symmetric public surface). Loopback tests
(extend `SocketIntegrationTests.cs:104-117` pattern) prove it live; if the probe proves
unworkable cross-platform, the scan-core unit tests are the fallback deliverable and the
gap stays documented. Stray DM with no urgent indication stays silent (no conflict with
`SwallowedCommands_StaySilent` DM case).

## 4. Linemode network-side work (implement + test)

### 4.1 FLUSHIN/FLUSHOUT on the send path (RFC 1184 §5.8)
RFC trigger is "whenever this function is sent" (`rfc1184.txt:363-368`) — not SLC
agreement. Today flags are stored-only (`LinemodeState.cs:137,146,155-156`, no
Synch/TIMING-MARK emission in `ReplySlcAsync` `:851-879`). Implement: hook the `SendCommand`
path (client + server equivalents); after writing `IAC <func>`, look up the SLC table entry
for the mapped function and fire its flush actions. Needs: SLC function constants (none
exist; verified codes `rfc1184.txt:100-110` (SYNCH=1 through EL=11) and `:124-125`
(FORW1=17, FORW2=18)) plus a `Commands`→function map (BRK/IP/AO/AYT/ABORT/
SUSP/EOF/EC/EL — `Abort = 238`, `Suspend = 237` verified at `Commands.cs:11-13`;
GA/NOP have no SLC function and are excluded; SYNCH travels the urgent path, never
`SendCommand`). (AUDIT CORRECTION with reasoning: the draft excluded EC/EL claiming
they "have no SLC functions" — false, and contradicted by the same sentence's own
cite: the table just read lists SLC_EC=10, SLC_EL=11 (`rfc1184.txt:109-110`).
Excluding them would silently drop agreed FLUSH behavior on functions the peer
negotiated; the trigger sentence (§5.8 "whenever this function is sent") draws no
editing-function exception, so EC/EL map like every other row.) FLUSHOUT → emit `IAC DO
TIMING-MARK` via the negotiation send (works on any stream; client has
`SendTimingMarkAsync` at `Client.Negotiation.cs:60-63`, server uses generic
`RequestEnableAsync`). Gate first on each side's existing LINEMODE-agreement check
(client `IsEnabledByUs(LineMode)` as in `Client.Linemode.cs:20,40`; server
`IsEnabledByPeer(LineMode)` as in `ServerSession.Linemode.cs:102`) — without it, every
`SendCommand` on a non-linemode session would emit `DO TIMING-MARK` unrequested. FLUSHIN →
urgent DM behind the existing `is TcpByteStream` gate (AUDIT CORRECTION: an earlier draft
put urgent-send on the `IByteStream` abstraction — rejected because `IByteStream`
(`Transport/IByteStream.cs:10-88`) is a minimal read/write surface and a new member breaks
every implementer including all test fakes; the deliberate library pattern at
`Client.cs:138-144`, `ServerSession.Negotiation.cs:152-158` and
`ServerSession.Linemode.cs:111-124` is fail-loud behind `TcpByteStream`, so reusing that
gate IS the maintainable choice — differing only by log-and-continue instead of throw,
since an automatic side-effect must not break sends on fakes/pipes). Consequence: the
urgent-DM half is loopback-tested only (same as `SendSynchAsync_DeliversDmOutOfBand`);
hermetic tests pin the TIMING-MARK bytes plus non-TCP no-throw-and-log.

### 4.2 FORW2 receipt validation (RFC 1184 §5.5, `rfc1184.txt:822-823`)
"The SLC_FORW2 character should only be used if SLC_FORW1 is already in use."
(AUDIT CORRECTION with reasoning: the draft quoted this as "must only be used when" —
wrong modal, wrong conjunction. Found by reading `:819-826` after a verbatim-grep for the
remembered wording came back empty; the sentence sits at the tail of §5.5, just above the
§5.6 header at `:825`, so the §5.5 attribution stands. The softer "should" still justifies
refusing lone FORW2 — a peer violating even a SHOULD gets NOSUPPORT rather than silent
accept — so the design is unchanged, only the quote is now exact.) No constants or gating
exist (`ApplySlc` `:125-158`, `ReplySlcAsync` `:851-879`). Implement: add FORW1=17/FORW2=18
constants (shared with §4.1); inbound FORW2 while table FORW1 is NOSUPPORT → downgrade
refusal as NOSUPPORT (normal-exchange rule, §5.9 table). Export path leaves user-configured
data intact (documented — silently dropping user config is worse than sending it). Tests:
FORW2-alone → NOSUPPORT reply; FORW2-with-FORW1 → normal ACK flow.

## 5. Documented non-goals (architecturally blocked per §0, not deferred)
- FORWARDMASK acceptance (needs EDIT-mode input buffer; none exists). Current refuse-WONT
  stays pinned (`ForwardMaskProposal_RefusedWithWont`, `LinemodeTests.cs:126`).
- SOFT_TAB/LIT_ECHO behaviors (need typed-input echo rendering; echo shares the `Read()`
  data map). Subset-drop stays pinned (`ModeUnsupportedBits_AnsweredAsSubset`,
  `LinemodeTests.cs:93-101` — UNCHANGED by this plan).
- RCTE option 7 (only an enum value, `Options.cs:23`; full-duplex printer-control
  discipline, no hook point).

## 6. Execution order (DECIDED: phased full plan)
Phase 1 (§1: four deviation fixes + three test rewrites) → build + full suite green.
Phase 2 (§2 sweep) → green. Phase 3 (§3: NAWS pins, X-DISPLAY, Synch-receive) → green.
Phase 4 (§4: FLUSH, FORW2) → green. Bisectable; no phase starts red.

## Decisions record (user-answered, binding)
1. GA unsuppressed → dedicated event/callback (clean API over text marker).
2. STATUS → strict `Yes`-only IS answers.
3. Scope → phased full plan (§§1–4; §5 parked).
4. Synch-receive → Transport probe with scan-core-unit-test fallback.
5. FLUSH off-TCP → existing `TcpByteStream` gate; log + skip, no throw (AUDIT: overrides the
   earlier capability reading — NOT changing `IByteStream` is what keeps every fake/implementer intact).
6. X-DISPLAY → option-35 subnegotiation (WeAgree + server IS parser + client SEND answer) with
   effective-display getter across option-35 and ENVIRON DISPLAY (AUDIT: NOT both in
   `ClientEnvironment` — X-DISPLAY-LOCATION is option 35 (RFC 1096), not an ENVIRON var;
   an earlier draft said 49, which is the unrelated Forward-X slot).