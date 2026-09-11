# Achaea Telnet Client Protocol (ATCP)

> Source: <https://www.ironrealms.com/rapture/manual/files/FeatATCP-txt.html>
> (retrieved 2026-09-11; license UNKNOWN — third-party document, all rights
> remain with its authors; stored for reference only, do not redistribute.)

TELNET is the protocol used to connect to Rapture, however it is often not
expressive enough for some of the functionality that a server may want to
provide.

## Transport Protocol

This protocol needed to be compliant and invisible to existing TELNET standards
because the primary client of choice is still simply a TELNET-compatible one
(such as ZMud). So, the following format was designed:

### Enabling the Protocol

The transport layer must be enabled. This involves using standard TELNET
sub-option negotiation to determine if the server (and/or client) is capable of
ATCP communication. RFC 855
[http://www.faqs.org/rfcs/rfc855.html](http://www.faqs.org/rfcs/rfc855.html)
describes the process of sub-option negotiation. A summary is as follows. The
server asks the client whether it is capable of supporting ATCP (a 3 byte
sequence):

```text

IAC DO ATCP       (255 253 200)

```

Note, that the byte 200 is an arbitrary value chosen by Iron Realms, it is not
endorsed by any TELNET standards. It was chosen because it did not interfere
with any other registered TELNET options. The client then responds with another
3 byte code:

```text

IAC WILL ATCP     (255 251 200)

```

From this point on, the server knows that the client is ATCP capable (and is
expecting to receive ATCP messages). This will be reflected in the
[node[].atcp](https://www.ironrealms.com/rapture/manual/files/FeatBuiltinDB-txt.html#node[].atcp)
property which will now be set to true.

### Sending Messages

Once the ATCP option has been been enabled, a message may be sent to the client
or server using standard TELNET sub-option markers. For example, IAC SB ATCP
`<atcp message text>` IAC SE. The start and stop marker byte sequences are
defined as follows.

```text

*Char Sequence*  *Numeric Version*   *Description*
IAC SB ATCP      255 250 200         Start sequence
IAC SE           255 240             Stop sequence

```

The text between the two markers includes the entire ATCP message body, and can
be any text you desire (except, of course, the end marker sequence). You don’t
have to worry about this when sending messages from server to client, however,
because Rapture provides a very easy way to do it (see below).

### Sending Server -> Client Messages (the EASY way)

Rapture makes sending messages over ATCP from server to client extremely easy.
Simply call `<atcp_msg>` () with the message contents when you’re ready. It’ll
even detect whether ATCP is enabled for a given node and only send the message
if it is. The specific specification is mentioned here to assist custom client
implementors.

The ATCP TELNET option has been enabled by the player’s client.
