# GMCP Protocol

> Source: <https://www.gammon.com.au/gmcp> (retrieved 2026-09-11; license
> UNKNOWN — third-party document, all rights remain with its authors; stored for
> reference only, do not redistribute.)

Posted by Nick Gammon on Wed 08 Apr 2015 01:19 AM — 2 posts, 42,555 views.

This page can be quickly reached from the link:
[http://www.gammon.com.au/gmcp](http://www.gammon.com.au/gmcp)

GMCP protocol

This page adapted from the standard documented at the now-defunct
mudstandards.org domain.

Original post by Mike Potter (Zugg)
[http://www.zuggsoft.com](http://www.zuggsoft.com) with comments and suggestions
by others such as myself (Nick Gammon).

 The acronym GMCP stands for **G**eneric **M**UD **C**ommunication **P**rotocol.

General concepts

The intention of GMCP (and its predecessors such as ATCP) is to allow "out of
band" information to be sent from the MUD server to the client, or the client to
the server. In this context "out of band" means "invisible to the player". The
messages are enclosed with Telnet escape sequences, and as such clients (that
support these sequences) accept these messages but do not display them directly
on the screen.

Thus, a server can send details like room numbers, available exits, player
health and mana, changes to inventory and quests, and other details. This
greatly simplifies client design, because instead of clients having to match and
parse very specific textual elements (like "prompt" lines) which can be subject
to change, or suppression, the client can instead interpret the GMCP data which
should not change, even if the player alters their configuration.

It also allows clients to easily display some things (like chat messages, combat
information) in separate windows, or panes of an existing window, without there
being any ambiguity about what a particular message means.

Protocol Negotiation

## *Constants*

```text

`
// TELNET escape sequences  - see RFC 854 (Except for 0xC9)

enum { 
       SE   = 0xF0, // end of subnegotiation
       SB   = 0xFA, // start of subnegotiation
       WILL = 0xFB, 
       WONT = 0xFC,
       DO   = 0xFD,
       DONT = 0xFE,
       IAC  = 0xFF,  // Interpret As Command
       GMCP = 0xC9   // GMCP sequence (decimal 201)
      };
`

```

Normal Telnet Option negotiation protocol is used with a telnet option number of
201 (un-registered).

 This is generally done once (ie. after connecting to the MUD).

 Server sends:

```text

`
IAC WILL GMCP   (ie. 0xFF 0xFB 0xC9)
`

```

 If the client supports GMCP, it responds with:

```text

`
IAC DO GMCP    (ie. 0xFF 0xFD 0xC9)
`

```

 If not:

```text

`
IAC DONT GMCP  (ie. 0xFF 0xFE 0xC9)
`

```

Protocol Transport

The Telnet out-of-band data (IAC SB) is used for sending GMCP messages. This
transport is bi-directional. Either the Server or the Client can send GMCP data,
as required by the specific GMCP modules.

 The syntax is:

```text

`
    IAC SB GMCP Package[.SubPackages].Message <data> IAC SE  

That is:

    0xFF 0xFA 0xC9 package.message <data> 0xFF 0xF0
`

```

where [.SubPackages] indicate optional multiple sub-package names (without the
brackets). `<data>` indicates the generic data for the message (format described
below).

The Package and Subpackage names are alphanumeric with underscores and hyphens
allowed. As a convention, server-specific sub-packages or messages should begin
with an underscore character.

 Package names are **not** case-sensitive. JSON names **are** case-sensitive.

 Example regular expression:

```text

`
[A-Za-z_][A-Za-z0-9_-]*(?:\.[A-Za-z_][A-Za-z0-9_-]*)+
`

```

The `<data>` part is optional. If it is present, then it is separated from the
Package.Message name by a space.

MUSHclient does not impose any limitations on the size of the packet, I cannot
speak for other clients. Any characters may appear inside the packet (eg.
carriage returns, tabs, newlines, and the IAC character itself provided it is
doubled as IAC IAC - however see tip below).

The syntax for `<data>` is similar to a JavaScript variable assignment but
without the equal-sign (=), where Package.Message is the "variable name" and
`<data>` is the "value". Normal JavaScript syntax applies to data values (see
Data Format below) but without any function calls or other scripting. This
syntax is typically implemented on the Client-side using a JavaScript Object
Notation (JSON) parser.

Data Format

Normal JavaScript syntax applies to the data format allowing four primitive
types (strings, numbers, booleans, and null) and two structured types (objects
and arrays). The two structured types use standard JSON syntax (see below),
while the four primitive types use the syntax of the corresponding "Values"
within the JSON specification.

## Implementation Note

Some JSON parsers allow primitive Value data outside of arrays and objects, but
some do not. If your JSON parser does not allow primitive Values outside of
arrays or objects, this can be easily solved by adding "[" and "]" characters
around the `<data>` value, then calling the JSON parser on this new array
string, then extracting the 0th element of the returned JSON array data.

JSON

JSON can represent four primitive types (strings, numbers, booleans, and null)
and two structured types (objects and arrays).

Insignificant whitespace is allowed before or after any token. The whitespace
characters are: tab (\t), line feed (\n), carriage return (\r), and space (' ').
Whitespace is not allowed **within** any token except strings.

- A **string** is a sequence of zero or more Unicode characters (UTF-8 for GMCP)
  inside double quotes, eg. `"Nick Gammon"`

- A **number** is a decimal sequence of digits (optionally with a leading minus
  sign, floating point and/or exponent) similar to most programming languages.
  Note that hex, octal, binary, etc. numbers are not allowed. Nor are
  "non-numbers" like "NaN" or "Inf".

Note that the JSON spec does not seem to allow for a leading "+" sign for a
number. However you can use "+" or "-" before the exponent (if present). It also
insists on a digit before the decimal place (eg. 0.5, not .5).

 For example, allowable numbers would be: `42, 123.45, 62e5, -22, 567E-10`

- A **boolean** is one of the two values: `true` / `false`

- A **null** is the value: `null`

- An **object** is an keyed collection of zero or more **name**/**value** pairs
  (in no particular sequence), *separated by commas*, where a **name** is a
  string (followed by a colon) and a **value** is a string, number, boolean,
  null, object, or array. Objects are contained inside curly braces. Because
  values can be objects or arrays, therefore objects can contain nested objects
  or arrays.

 For example: `{ "name": "Gandalf", "class": "wizard" }`

- An **array** is an ordered sequence of zero or more **values** (string,
  number, boolean, null, object, or array), *separated by commas*. Arrays are
  contained inside square brackets. Because values can be objects or arrays,
  therefore arrays can contain nested objects or arrays.

 For example: `[ 1, 2, 3, "foo", "bar" ]`

The JSON types are readily represented in Lua. Strings and numbers are the
equivalent types in Lua. The **null** value becomes **nil** in Lua. Objects and
arrays can be represented as Lua tables. Objects as the normal key/value pairs,
and arrays as numerically-keyed tables.

Note that in JSON the following (unquoted) words are reserved: **false / null /
true**

## *String Data:*

Text is enclosed within double quotes ("). The backslash (\) character acts as
an "escape" character for embedding quotes within the string.

 The following characters must be escaped with a preceding backslash, like this:

```text

`
\"   (double-quote)
\\   (backslash)
\n   (newline)
\r   (carriage-return)
\b   (backspace)
\f   (form-feed)
\t   (tab)
`

```

Although JSON allows for multiple Unicode encodings, for GMCP all text within
the quotes is encoded in UTF-8 format. We do not recommend the \uXXXX notation
for Unicode characters, as there was some debate about their representation.

## *Binary Data:*

The GMCP protocol is not intended to be a binary data transport. Since GMCP
messages interrupts the normal text data being displayed by the server, it is
recommended that GMCP be used to send a URL for binary data files, and that the
client transfer the binary data file via a separate HTTP socket.

However, each GMCP package and message is allowed to encode its data in any way
that is compatible with the transport: escaped " and \ characters and UTF-8
encoding.

## *Tip:*

Note that the hex byte 0xFF is not allowed within a UTF-8 data stream, and 0xFF
bytes would also need to be doubled since the data occurs within a Telnet IAC SB
message.

In other words, we do not expect to see 0xFF inside the packet, as it would not
be valid UTF-8, doubled or not.

If a message needs to send small amounts of binary data, it is recommended that
the package choose a standard ASCII encoding mechanism for the data, such as
BASE64 or UUENCODE to avoid the issues of escaped characters and 0xFF bytes.

## *Examples from Aardwolf:*

 (assume all examples are inside IAC SB GMCP ... IAC SE)

```text

`
room.info { "num": 32519,
"name": "Whitewind Avenue",
"zone": "aylor",
"terrain": "city",
"details": "",
"exits": { "n": 32518, "s": 32520 },
"coord": { "id": 0, "x": 30, "y": 20, "cont": 0 }
}

comm.repop { "zone": "aylor" }

comm.tick {}

comm.channel { "chan": "newbie",
"msg": "[Newbie] Adrirabaen: hello",
"player": "Adrirabaen" }

`

```

## *Examples from the original GMCP spec:*

 Message without any data:

```text

`
SomePackage.Message
`

```

 Message with null data (null is a literal value of 4 characters):

```text

`
SomePackage.Message null
`

```

 Primitive numeric data:

```text

`
SomePackage.Message 12345
`

```

 Primitive floating-point data:

```text

`
SomePackage.Message 99.95
`

```

 Primitive boolean data (true and false are literal values):

```text

`
SomePackage.Message true
`

```

 Primitive string data:

```text

`
SomePackage.Message "Hello World"
`

```

 Multi-line string data (using \n escaped newline):

```text

`
SomePackage.Message "Hello World\nThis is a test"
`

```

 Array of data (that is, a vector without keys):

```text

`
SomePackage.Message ["Item1", "Item2", 123, 456, false, "Another item"]
`

```

 Keyed object data:

```text

`
SomePackage.Message {"name": "Zugg", "race": "dwarf", "class": "fighter"}
`

```

 Mixed structured data:

```text

`
SomePackage.Message
{"char": {"name": "Zugg", "level": 20},
"class": {"name": "fighter", "attr": "whatever"},
"hp": 123, "wizard": false, "array": [123,456,"text"]}
`

```

 Using a sub-package (string data):

```text

`
SomePackage.SubPackageName.Message "Hello world"
`

```

 Using a non-standard (server-specific) sub-package extension (string data):

```text

`
SomePackage._Extension.Message "Hello world"
`

```

 Server disconnect:

```text

`
Core.Goodbye "Goodbye, adventurer"
`

```

## *Sending messages from the client to the server*

 Initial connection:

```text

`
Core.Hello { "client": "MUSHclient", "version": "4.97" }
`

```

 Tell the server what we support:

```text

`
Core.Supports.Set [ "Char 1", "Comm 1", "Room 1" ]
Core.Supports.Set [ "Char 1", "Char.Skills 1", "Char.Items 1" ]
`

```

 Login:

```text

`
Char.Login { "name": "somename", "password": "somepassword" }
`

```

 (Example from Aardwolf): Request refresh of information:

```text

`
request char
request room
request area
request quest
request group
`

```

In the case of Aardwolf these messages request the server to re-send information
(eg. if a client plugin was reloaded).

## *Note:*

The above messages ("request area" etc.) do not conform to the GMCP standard for
two reasons:

1. GMCP expects a minimum of package.message (eg. Core.Request)
2. The words "area" / "room" / "char" etc. are not quoted.

Lasher has indicated that these messages are intended, not as GMCP messages, but
as a simple way for the client to request that certain GMCP messages are
re-sent.

 (Example from IRE): Request information about skills, groups, items, etc.

```text

`
Char.Skills.Get
Char.Skills.Groups
Char.Skills.List
Char.Skills.Info { "skill": "Firelash" }
Char.Items.Inv
`

```

References

## *JSON and UTF-8*

1. JSON RFC 4627:
   [http://www.ietf.org/rfc/rfc4627.txt](http://www.ietf.org/rfc/rfc4627.txt)
2. JSON information: [http://www.json.org](http://www.json.org)
3. UTF-8:
   [http://en.wikipedia.org/wiki/UTF-8](http://en.wikipedia.org/wiki/UTF-8)

## *Servers*

The pages below document many messages used in Aardwolf and IRE (Iron Realms
Entertainment) games. To save reinventing the wheel developers of new MUDs are
recommended to use as much of the ones documented there as possible. In
particular, this would make porting the MUSHclient graphical mapper to a new MUD
much easier.

- [http://www.aardwolf.com/wiki/index.php/Clients/GMCP](http://www.aardwolf.com/wiki/index.php/Clients/GMCP)
- [http://www.ironrealms.com/gmcp-doc](http://www.ironrealms.com/gmcp-doc)
- [https://github.com/keneanung/GMCPAdditions](https://github.com/keneanung/GMCPAdditions)

## *Clients*

- [GMCP in CMUD](http://forums.zuggsoft.com/modules/mx_kb/kb.php?page=&mode=doc&k=3049)
- [GMCP in Mudlet](http://forums.mudlet.org/viewtopic.php?f=7&t=1547)
- [GMCP in TinTin++](http://tintin.sourceforge.net/board/viewtopic.php?p=4651)
- [GMCP in TinyFugue](http://mikeride.chaosnet.org/abelinc/scripts/telopt.html)
- [GMCP in MUSHclient on Aardwolf](http://www.aardwolf.com/wiki/index.php/Clients/MushclientGMCP)

ATCP

Previously GMCP was known as ATCP2 where ATCP is the Achaea Telnet Client
Protocol. It is documented at
[Achaea Telnet Client Protocol (ATCP)](http://www.ironrealms.com/rapture/manual/files/FeatATCP-txt.html)
.

The protocol number for ATCP was 200 (not 201), otherwise it is quite similar.
There is an ATCP plugin supplied with MUSHclient (ATCP_NJG.xml) which could be
adapted to handle GMCP (change 200 to 201 inside the plugin).

Example of using JSON module in Lua

This brief example shows how you can decode JSON in Lua, using the json.lua
module (and associated files) that ship with MUSHclient.

```text

`
require "json"

params = ' { "foo": [ 22, 33, 44 ] , "bar" : [ "the", "quick", "fox" ] } '

result = assert (json.decode (params))

require "tprint"

if type (result) == "table" then
  tprint (result)
end -- if
`

```

 Output:

```text

`
"bar":
  1="the"
  2="quick"
  3="fox"
"foo":
  1=22
  2=33
  3=44
`

```

Notice that the JSON arrays start at 1, when parsed by this module, the same as
in Lua.

 Here is an example of a GMCP-parsing plugin for MUSHclient:

 To save and install the **GMCP_handler_NJG** plugin do this:

1. Go to the GitHub page:
   [GMCP_handler_NJG.xml](https://raw.githubusercontent.com/nickgammon/plugins/master/GMCP_handler_NJG.xml)
2. Select all the page and copy it to the Clipboard
3. Open a text editor (such as Notepad) and paste the plugin into it
4. Save to disk on your PC, preferably in your plugins directory, as:
   `**GMCP_handler_NJG.xml**`
The "plugins" directory is usually under the "worlds" directory inside where you
installed MUSHclient.
5. Go to the MUSHclient File menu -> Plugins
6. Click "Add"
7. Choose the file `**GMCP_handler_NJG.xml**` (which you just saved in step 4)
   as a plugin
8. Click "Close"
9. Save your world file, so that the plugin loads next time you open it.

The main GitHub page for this plugin is at:
[https://github.com/nickgammon/plugins/blob/master/GMCP_handler_NJG.xml](https://github.com/nickgammon/plugins/blob/master/GMCP_handler_NJG.xml)
.

There you will find the
[commit history](https://github.com/nickgammon/plugins/commits/master/GMCP_handler_NJG.xml)
and other information.

Reworked and simplified version of the one by Lasher and Fiendish, from
Aardwolf.

The plugin uses the OnPluginTelnetSubnegotiation callback, which MUSHclient
calls whenever a IAC SB xx ... IAC SE sequence arrives.

---

 Example of use in your own plugin:

 To save and install the **GMCP_message_receiver_test** plugin do this:

1. Go to the GitHub page:
   [GMCP_message_receiver_test.xml](https://raw.githubusercontent.com/nickgammon/plugins/master/GMCP_message_receiver_test.xml)
2. Select all the page and copy it to the Clipboard
3. Open a text editor (such as Notepad) and paste the plugin into it
4. Save to disk on your PC, preferably in your plugins directory, as:
   `**GMCP_message_receiver_test.xml**`
The "plugins" directory is usually under the "worlds" directory inside where you
installed MUSHclient.
5. Go to the MUSHclient File menu -> Plugins
6. Click "Add"
7. Choose the file `**GMCP_message_receiver_test.xml**` (which you just saved in
   step 4) as a plugin
8. Click "Close"
9. Save your world file, so that the plugin loads next time you open it.

The main GitHub page for this plugin is at:
[https://github.com/nickgammon/plugins/blob/master/GMCP_message_receiver_test.xml](https://github.com/nickgammon/plugins/blob/master/GMCP_message_receiver_test.xml)
.

There you will find the
[commit history](https://github.com/nickgammon/plugins/commits/master/GMCP_message_receiver_test.xml)
and other information.

---

The simpler plugin does **not** decode the JSON text. The reason for this is
that you may want to only know what has changed, and the earlier plugin I had
posted accumulated all entries. This was particularly unhelpful for things like
room exits, because the room.exits table gradually accumulated exits for all the
directions you could ever go to.

 An example of receiving the plugin broadcast is below:

```text

`
function gotCharacterVitals (vitals)
 -- example:
  print ("HP is now:", tonumber (vitals.hp))
end  -- gotCharacterVitals

function gotCharacterStatus (status)

end  -- gotCharacterStatus

handlers = {
  ["char.vitals"]           = gotCharacterVitals,
  ["char.status"]           = gotCharacterStatus,

  } -- end of handlers

function OnPluginBroadcast (msg, id, name, text)
 if id == "74f8c420df7d59ad5aa66246" then  -- GMCP_handler_NJG

   -- pull out GMCP message, plus the data belonging to it
   message, params = string.match (text, "([%a.]+)%s+(.*)")

   -- no match? oops!
   if not message then
      return
   end -- if

   -- ensure we have an array or object
   if not string.match (params, "^[%[{]") then
      params =  "[" .. params .. "]"  -- JSON hack, make msg first element of an array.
   end -- if 

   -- decode it
   result = assert (json.decode (params))

   -- find a handler for this message type
   local handler = handlers [message:lower ()]

   -- warn if not found
   if not handler then
     ColourNote ("red", "", "Warning: No handler for: " .. message)
     return
   end -- no handler

   -- call the handler, pass in whatever we got
   handler (result)
  end -- if GMCP message
end -- OnPluginBroadcast
`

```

The function OnPluginBroadcast looks for a broadcast from the GMCP_handler_NJG
plugin. If found it breaks it into message and data.

It calls json.decode to turn the JSON data into a Lua table. Then it looks up
the message type in a table of known messages, and if found passes the decoded
table to that handler.
