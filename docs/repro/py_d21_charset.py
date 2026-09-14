"""D21: CHARSET receive leniency + empty discipline.

(a) REQUEST naming USASCII with the real client policy: truth's
    send_charset cannot resolve it and returns "" (its documented
    no-selection signal), but "" is not None, so the handler still
    answers ACCEPTED with an empty body; C# accepts the alias.
(b) Empty ACCEPTED: truth adopts '' and forces binary; C# takes the
    rejection path (pending cleared, CharsetRejected, no latch).
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
    feed(w, IAC + SB + CHARSET + b'\x01' + b' ' + b'USASCII' + IAC + SE)
    print('REQUEST USASCII -> writes=', writes_hex(t))

    w2, t2, p2 = make_writer(server=False, client=True)
    w2.set_ext_callback(cmd=CHARSET, func=lambda c: print('accepted cb:', repr(c)))
    try:
        feed(w2, IAC + SB + CHARSET + b'\x02' + IAC + SE)
        print('empty ACCEPTED -> no-raise environ=', repr(w2.environ_encoding),
              'force_binary=', p2.force_binary, 'writes=', writes_hex(t2))
    except Exception as e:
        print('empty ACCEPTED -> RAISED', type(e).__name__, str(e)[:100],
              'environ=', repr(getattr(w2, 'environ_encoding', 'n/a')))


if __name__ == '__main__':
    asyncio.run(main())
