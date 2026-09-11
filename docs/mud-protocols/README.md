# MUD protocol reference copies

Local copies of third-party MUD client protocol specifications, stored as
ground truth for the `GMCP` / `MSDP` / `MSSP` / `MSP` / `MXP` / `ZMP` /
`ATCP` / `MTTS` / `MCCP` frame codecs in `telnet_cs/Protocol/MudProtocol.cs`
(see `feature.md` §§4, 8, 9).

> **License: UNKNOWN for every file below, except `zmp.md` (see note).**
> These are third-party documents fetched from the public web. No license
> grant is stated or implied (outside `zmp.md`); all rights remain with
> their respective authors. They are stored here for reference only
> (protocol numbers, byte layouts, examples) — do not copy their text
> into source files or redistribute them under this repo's license.
>
> `zmp.md` states its own license: Creative Commons BY-ND 2.0
> (`http://creativecommons.org/licenses/by-nd/2.0/`), with implementations
> explicitly not derivative works. The UNKNOWN rule still applies to the
> other eight files.

Retrieved 2026-09-11. Converted from the served HTML to Markdown with a
stdlib-only converter (verbatim `<pre>` blocks, tables where regular;
layout/nav markup dropped); each file links its source URL at the top.

| File | Protocol (option) | Source URL |
| --- | --- | --- |
| `mtts.md` | MTTS (§4) | <https://tintin.mudhalla.net/protocols/mtts/> |
| `mccp.md` | MCCP1/2/3 — 85/86/87 (§8) | <https://tintin.mudhalla.net/protocols/mccp/> |
| `gmcp.md` | GMCP — 201 (§9) | <https://www.gammon.com.au/gmcp> (redirects to `forum/threads/12834.html?id=12834`) |
| `msdp.md` | MSDP — 69 (§9) | <https://tintin.mudhalla.net/protocols/msdp/> |
| `mssp.md` | MSSP — 70 (§9) | <https://tintin.mudhalla.net/protocols/mssp/> |
| `msp.md` | MSP — 90 (§9) | <https://www.zuggsoft.com/zmud/msp.htm> |
| `mxp.md` | MXP — 91 (§9) | <https://www.zuggsoft.com/zmud/mxp.htm> |
| `zmp.md` | ZMP — 93 (§9) | <https://discworld.starturtle.net/external/protocols/zmp.html> |
| `atcp.md` | ATCP — 200 (§9) | <https://www.ironrealms.com/rapture/manual/files/FeatATCP-txt.html> |

Notes:

- Aardwolf-102 has no published spec page; its ground truth is the
  channel table in `telnetlib3/mud.py:335-365` (already ported).
- Fidelity: every phrase in the nine `.md` files was mechanically checked
  against a fresh fetch of its source page (word-stream containment, links
  included). Two benign classes of difference remain, both verified:
  Cloudflare `cdn-cgi/l/email-protection#…` links (in `msp.md`, `mxp.md`)
  carry a per-fetch rotating `#…` fragment — link text and target shape
  match, only the fragment rotates; and two tintin hrefs (`mnes`, `mslp`)
  are stored with a trailing slash the live pages omit (same resource).
- IETF options implemented in `feature.md` §§1–3, 5–8 (Logout, SNDLOC,
  EOR, LFLOW, New Environ, Charset, COM Port, XDisplay) are grounded in
  `docs/telnet-rfcs/rfc727/779/885/1096/1372/1571/1572/2066/2217.txt`.
- Lint: `markdownlint-cli2` is clean on all ten files except 102
  `MD013/line-length` remainders that cannot be wrapped without falsifying
  the reference: byte-exact wire examples in fenced blocks (93), table rows
  (5, `mtts.md`), one `mxp.md` element-definition heading, and three lines
  whose first 70+ columns are a single unsplittable link plus a glued `-`
  (splitting there would forge a bogus list item).
- Intentional deltas from lint compliance (all else is byte-faithful
  reflowing): six duplicate headings disambiguated with parenthetical
  scope (`mccp.md` ×5 `… (MCCP3)`, `msdp.md` ×1 `General (configurable)`);
  two spaced `#…` link fragments percent-encoded to valid URIs (`msp.md`,
  `mxp.md`, link text unchanged); one source typo restored verbatim
  (`mtts.md` `[MUSHclient](http://http://www.gammon.com.au)`, as served).
