"""D8: malformed/unsolicited subnegotiation — truth raises, C# logs and ignores.

Covers LINEMODE (unknown sub, empty MODE, bad byte after DO), LFLOW
(trailing bytes tolerated, bad mode raises), CHARSET TTABLE verbs.
(CHARSET illegal verb, STATUS SEND without WILL, unsolicited WILL TM are
already proven in py_d4_raisevssilent.log.)
"""
import sys
import os
from pathlib import Path
_HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(_HERE / 'stubs'))
sys.path.insert(0, os.environ.get('TELNETLIB3_PATH', '/home/anon/telnetlib3'))
sys.path.insert(0, str(_HERE))
import telnetlib3
from h import make_writer, feed, writes_hex

IAC, SB, SE, DO = telnetlib3.IAC, telnetlib3.SB, telnetlib3.SE, telnetlib3.DO
LINEMODE, LFLOW, CHARSET = telnetlib3.LINEMODE, telnetlib3.LFLOW, telnetlib3.CHARSET


def probe(label, data, **kw):
    lflow_on = kw.pop('lflow_on', False)
    w, t, _ = make_writer(**kw)
    if lflow_on:
        w.local_option[LFLOW] = True
    try:
        feed(w, data)
        print(label, '-> no-raise writes=', writes_hex(t))
    except Exception as e:
        print(label, '-> RAISED', type(e).__name__, str(e)[:110], 'writes=', writes_hex(t))


def main():
    probe('LINEMODE-unknown-sub', IAC + SB + LINEMODE + b'\x09' + IAC + SE)
    probe('LINEMODE-empty-MODE', IAC + SB + LINEMODE + b'\x01' + IAC + SE)
    probe('LINEMODE-badbyte-after-DO', IAC + SB + LINEMODE + DO + b'\x09' + IAC + SE)
    probe('LFLOW-trailing-unnegotiated', IAC + SB + LFLOW + b'\x01extradata' + IAC + SE)
    probe('LFLOW-trailing-agreed', IAC + SB + LFLOW + b'\x01extradata' + IAC + SE, lflow_on=True)
    probe('LFLOW-badmode-agreed', IAC + SB + LFLOW + b'\x09' + IAC + SE, lflow_on=True)
    probe('CHARSET-TTABLE-IS', IAC + SB + CHARSET + b'\x04' + IAC + SE)
    probe('CHARSET-TTABLE-REJECTED', IAC + SB + CHARSET + b'\x05' + IAC + SE)


if __name__ == '__main__':
    main()
