# RFC coverage

Verbatim reference copies live in `telnet-rfcs/` (RFCs 726, 854, 855,
856–860, 1073, 1079, 1091, 1143, 1184, 1408). This matrix states what the
library implements for each, with the pinning test classes.

| RFC | Subject | Implementation |
| --- | ------- | -------------- |
| 854 | Base protocol, NVT, commands, Synch | IAC framing/parsing, IAC doubling, `IAC SB ... IAC SE`, control commands (`SendCommand` allow-list), Synch via TCP urgent + DM (`SendSynchAsync` / `ReceiveUrgentAsync`). See `wire.md`. |
| 855 | Option negotiation framework | WILL/WONT/DO/DONT intercept; negotiation verbs rejected from `SendCommand` and routed to the RFC 1143 API. |
| 1143 | Q-method state machine | `Protocol.NegotiationState`: per-option `us`/`him` states plus the single-entry queue and refusal memory; replies never repeat across reads. Pinned by `NegotiationStateTests`. |
| 856 | Transmit-Binary | Option number defined; data path passes bytes through; binary-mode interplay pinned by `BinaryModeTests`. |
| 857 | Echo | `WILL ECHO`/`DO ECHO` handling, `AllowRemoteEcho` opt-in, received-data echo-back with IAC escaping, infinite-bounce guard. Pinned by `EchoTests`. |
| 858 | Suppress-Go-Ahead | Proactive `IAC DO SuppressGoAhead` on connect (suppressible per instance), `OfferSuppressGoAhead` server preset. |
| 859 | Status | `StatusProtocol`: snapshot rendering (`BuildIsPayload`) plus the bare-`SE` `STATUS IS` frame (`FrameStatusIs`). Pinned by `StatusTimingMarkTests`. |
| 860 | Timing mark | `SendTimingMarkAsync` (`IAC DO TIMING-MARK`) round-trip. Pinned by `StatusTimingMarkTests`. |
| 1073 | Window size (NAWS) | `NawsProtocol` build/parse with console auto-detect and clamping; `RefreshWindowSizeAsync` resends on change. Pinned by `NawsRefreshTests`. |
| 1079 | Terminal speed | `TerminalSpeedProtocol.Normalize` (digit check, standard-rate rounding); client answers SEND, server requests/parses. Pinned by `TerminalTypeSpeedTests`. |
| 1091 | Terminal type | `TerminalTypeCycler` (ordered list, repeat-terminated, 40-char cap); client answers SEND, server walks the chain. Pinned by `TerminalTypeSpeedTests` and `ServerSessionTests`. |
| 1184 | Linemode | `LinemodeState` (MODE intersect/union + ACK rules, 31-entry SLC table per §5.5) and `LinemodeProtocol` constants; client import/export and server MODE/forward-mask/SLC ops. Pinned by `LinemodeTests`. |
| 1408 | Environment (new) | `EnvironmentProtocol` build/parse (ESC resolution, Latin-1), ENVIRON INFO push on change, server requesters. Pinned by `EnvironmentTests`. |
| 726 | Remote controlled transmission and echo | Option number (`Options.RemoteTransEcho`) defined and negotiable; no dedicated behavior beyond the RFC 1143 machinery. |

Out of scope: encryption/TLS (no STARTTLS handshake despite the option
number), authentication frameworks (option 37 — `AuthenticateAsync` is a
plain login/password helper), TN3270, X display, COM-port control.
