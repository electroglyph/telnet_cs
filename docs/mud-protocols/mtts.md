# MTTS (Mud Terminal Type Standard)

> Source: <https://tintin.mudhalla.net/protocols/mtts/> (retrieved 2026-09-11;
> license UNKNOWN — third-party document, all rights remain with its authors;
> stored for reference only, do not redistribute.)

```text

TTYPE            24

IS                0
SEND              1

```

```text

server - IAC DO TTYPE
client - IAC WILL TTYPE
server - IAC   SB TTYPE SEND IAC SE
server - IAC   SB TTYPE SEND IAC SE
server - IAC   SB TTYPE SEND IAC SE
server - IAC   SB TTYPE SEND IAC SE
client - IAC   SB TTYPE IS   "TINTIN++" IAC SE
client - IAC   SB TTYPE IS   "XTERM" IAC SE
client - IAC   SB TTYPE IS   "MTTS 137" IAC SE
client - IAC   SB TTYPE IS   "MTTS 137" IAC SE

```

|     | "DUMB"  | Terminal has no ANSI color or VT100 support.                                                                                                                                                                    |
| --- | ---     | ---                                                                                                                                                                                                             |
|     | "ANSI"  | Terminal supports the common [ANSI color](https://tintin.mudhalla.net/info/ansicolor) codes. Supporting blink and underline is optional.                                                                        |
|     | "VT100" | Terminal supports most [VT100](https://tintin.mudhalla.net/info/vt100) codes and ANSI color codes.                                                                                                              |
|     | "XTERM" | Terminal supports all VT100 and ANSI color codes, [256 colors](https://tintin.mudhalla.net/info/256color), mouse tracking, and all commonly used [xterm console codes](https://tintin.mudhalla.net/info/xterm). |

```text

           1 "ANSI"              Client supports all common [ANSI color](https://tintin.mudhalla.net/info/ansicolor) codes.
           2 "VT100"             Client supports all common [VT100](https://tintin.mudhalla.net/info/vt100) codes.
           4 "UTF-8"             Client is using UTF-8 character encoding.
           8 "256 COLORS"        Client supports all [256 color](https://tintin.mudhalla.net/info/256color) codes.
          16 "MOUSE TRACKING"    Client supports xterm mouse tracking.
          32 "OSC COLOR PALETTE" Client supports [OSC](https://tintin.mudhalla.net/info/xterm#OSC) and the [OSC color palette](https://tintin.mudhalla.net/info/ansicolor#PALETTE).
          64 "SCREEN READER"     Client is using a screen reader.
         128 "PROXY"             Client is a proxy allowing different users to connect from the same IP address.
         256 "TRUECOLOR"         Client supports [truecolor](https://tintin.mudhalla.net/info/truecolor) codes using semicolon notation.
         512 "MNES"              Client supports [the Mud New Environment Standard](https://tintin.mudhalla.net/protocols/mnes/) for information exchange.
        1024 "MSLP"              Client supports [the Mud Server Link Protocol](https://tintin.mudhalla.net/protocols/mslp/) for clickable link handling.
        2048 "SSL"               Client supports SSL for data encryption, preferably TLS 1.3 or higher.

The client should add up the numbers of all supported terminal capabilities and print it as ASCII in decimal notation. In the case that a client supports ANSI, UTF-8, as well as 256 COLORS, it should respond with "MTTS 13", which is the sum of 1, 4, and 8. The reporting of UTF-8 should be implemented as a user setting, unless the client is certain that a Unicode font is being used.

```

| "DUMB" | Terminal has no ANSI color or VT100 support. |
| ---    | ---                                          |

| "ANSI" | Terminal supports the common [ANSI color](https://tintin.mudhalla.net/info/ansicolor) codes. Supporting blink and underline is optional. |
| ---    | ---                                                                                                                                      |

| "VT100" | Terminal supports most [VT100](https://tintin.mudhalla.net/info/vt100) codes and ANSI color codes. |
| ---     | ---                                                                                                |

| "XTERM" | Terminal supports all VT100 and ANSI color codes, [256 colors](https://tintin.mudhalla.net/info/256color), mouse tracking, and all commonly used [xterm console codes](https://tintin.mudhalla.net/info/xterm). |
| ---     | ---                                                                                                                                                                                                             |

If 256 color detection for non MTTS compliant servers is a must it's an option
to report "ANSI-256COLOR", "VT100-256COLOR", or "XTERM-256COLOR". If -TRUECOLOR
is used to indicate [truecolor](https://tintin.mudhalla.net/info/truecolor)
support the client should also support 256 colors.

## Mud Terminal Type Standard

On the third TTYPE SEND request the client should return MTTS followed by a
bitvector. The bit values and their names are defined below.

## Cycling

RFC1091 allows for cycling through a list of terminal types in order to select
one. Implementing this behavior is optional, but the client must properly report
the end of the cycle.

At the fourth TTYPE SEND requests the client should repeat the previous
response, reporting MTTS followed by a bitvector. Receiving the same terminal
type twice indicates to the server that the end of the list of available
terminal types has been reached. If the server sends additional requests the
client can either continue to respond with MTTS `<bitvector>` , reset its
cycling state to the initial state and start over, or ignore the request.

If the server sends IAC DONT TTYPE the client's cycling state should be reset to
the initial state, as if the client just connected to the server. This behavior
allows recovering from a copyover.

To make the negotiation process faster it's recommended for the server to send
three IAC SB TTYPE SEND IAC SE requests at once.

## Proxies

It's suggested for proxies adding MTTS support to also implement the NEW-ENVIRON
telnet option as per [RFC1572](https://tintin.mudhalla.net/rfc/rfc1572) . Using
the NEW-ENVIRON option the server can send: IAC SB NEW_ENVIRON SEND VAR
"IPADDRESS" IAC SE. When receiving this send request the proxy should respond
with: IAC SB NEW_ENVIRON IS VAR "IPADDRESS" VALUE "`<user's ip address>`" IAC
SE. See the [MNES](https://tintin.mudhalla.net/protocols/mnes/) specification
for more information.

The quote characters mean that the encased word is a string, the quotes
themselves should not be send.

If you have any comments you can email me at <mudclient@gmail.com>.

## Clients

[Axmud](https://axmud.sourceforge.io)

[MUD Portal](http://mudportal.com) - Also supports NEW-ENVIRON IPADDRESS
reporting.

[MUSHclient](http://http://www.gammon.com.au)

[TinTin++ Mud Client](https://tintin.mudhalla.net)

## Codebases

[Lowlands](https://github.com/scandum/lowlands) - Rudimentary MTTS detection and
utilization

[NekkidMUD](https://github.com/scandum/nekkidmud) - MTTS detection.

[ZetaMUCK](https://code.google.com/p/zetamuck) - MTTS detection and utilization
of 16/256 colors and UTF-8.

[Evennia](http://www.evennia.com) - MTTS detection and utilization of 16/256
colors.

[WickedMUD](https://github.com/scandum/wickedmud) - MTTS detection.

## Discussion

[MUDhalla Discord channel for TELNET related discussion](https://discord.gg/m3wZeSq)

## Extensions

## Servers

mudhalla.grapevine.haus:4321

## Snippets

[Scandum's MUD Telopt Handler](https://github.com/scandum/mth) - Handles
CHARSET, EOR, MCCP2, MCCP3, MSDP, MSDP over GMCP, MSSP, MTTS, NAWS, NEW-ENVIRON,
TTYPE, and xterm 256 colors.

   **
