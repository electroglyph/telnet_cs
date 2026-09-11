# Wire format notes

Byte-exact rules the implementation follows (see `telnet-rfcs/` for the
source texts). Every rule below has a wire-exact regression test — do not
"improve" framing without one.

- **Commands:** `IAC <verb>` two-byte frames for standalone verbs;
  `IAC WILL/WONT/DO/DONT <option>` three-byte negotiations.
- **Subnegotiation:** `IAC SB <option> <verb-first payload> IAC SE`.
  Embedded `IAC` payload bytes are doubled on send and collapsed on
  receive. Payloads are capped at 512 bytes (`MaxSubnegotiationBytes`);
  over-cap payloads resynchronize at the next `IAC SE`.
- **STATUS exception (RFC 859):** the `STATUS IS` reply escapes `SE`
  (not IAC) and ends with a **bare `SE`** — no `IAC` before it. See
  `StatusProtocol.FrameStatusIs` / `EnvironmentProtocol.FrameBareSe`.
- **Terminal type (RFC 1091):** SEND → IS chain; the client walks its
  list and sends the final entry twice to terminate; entries truncate at
  40 chars; unknown is `"UNKNOWN"`.
- **NAWS (RFC 1073):** `IAC SB NAWS <w-hi> <w-lo> <h-hi> <h-lo> IAC SE`,
  big-endian, clamped to 1–65535 with an 80×24 fallback.
- **LINEMODE (RFC 1184):** MODE masks carry MODE_ACK on reply; SLC rows
  are func/level+flags/value triplets under §5.5 rules 1–4; the forward
  mask is at most 32 bytes.
- **Synch (RFC 854):** TCP urgent notification whose urgent octet is DM
  (242). Never sent in-band: `Commands.DataMark` is rejected by
  `SendCommand` and ignored by the byte handler.
- **Data path:** Latin-1 by default (configurable `TextEncoding`); `BEL`
  handling and CR NUL/CRLF mapping live in the byte handler; reads use a
  rolling timeout with a 16 ms spin (`MillisecondReadDelay`).
