"""Fresh probe D2: simultaneous CHARSET REQUEST while ours is outstanding.

Truth: answers ACCEPTED regardless of pending (no pending gate).
Cs server role with CharsetRequestPending answers REJECTED (RFC 2066 S5).

Wires the REAL client selection policy (TelnetClient.send_charset) into a
bare writer so the REQUEST path executes reference code end to end.
"""
import asyncio
import sys
import os
from pathlib import Path
_HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(_HERE / 'stubs'))
sys.path.insert(0, os.environ.get('TELNETLIB3_PATH', '/home/anon/telnetlib3'))
sys.path.insert(0, str(_HERE))
import telnetlib3
from h import make_writer, feed, writes_hex

IAC, SB, SE = telnetlib3.IAC, telnetlib3.SB, telnetlib3.SE
CHARSET = telnetlib3.CHARSET


async def main():
    client = telnetlib3.TelnetClient(term='unknown')
    w, t, _ = make_writer(server=False, client=True)
    w.set_ext_send_callback(cmd=CHARSET, func=client.send_charset)
    w.pending_option[SB + CHARSET] = True  # our own REQUEST outstanding
    feed(w, IAC + SB + CHARSET + b'\x01' + b' ' + b'UTF-8 LATIN-1' + IAC + SE)
    print('writes=', writes_hex(t))


if __name__ == '__main__':
    asyncio.run(main())
