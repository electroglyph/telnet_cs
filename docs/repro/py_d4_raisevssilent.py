"""Fresh probe D4: error-policy split — truth raises, Cs logs-and-ignores.

Cases: WILL TM with no pending DO; STATUS SEND w/o WILL; illegal CHARSET
verb 0x09; client DO LOGOUT. Wire: neither side emits bytes in any of
these (except where noted).
"""
import sys
import os
from pathlib import Path
_HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(_HERE / 'stubs'))
sys.path.insert(0, os.environ.get('TELNETLIB3_PATH', '/home/anon/telnetlib3'))
sys.path.insert(0, str(_HERE))
import telnetlib3
from h import make_writer, feed, writes_hex, clear

IAC, SB, SE, WILL, DO = telnetlib3.IAC, telnetlib3.SB, telnetlib3.SE, telnetlib3.WILL, telnetlib3.DO
TM, LOGOUT, STATUS, CHARSET = telnetlib3.TM, telnetlib3.LOGOUT, telnetlib3.STATUS, telnetlib3.CHARSET


def probe(label, fn):
    w, t, _ = make_writer(server=True, client=False)
    try:
        fn(w)
        print(label, '-> no-raise writes=', writes_hex(t))
    except Exception as e:
        print(label, '-> RAISED', type(e).__name__, str(e)[:100], 'writes=', writes_hex(t))


def main():
    probe('WILL-TM-no-pending', lambda w: feed(w, IAC + WILL + TM))
    probe('STATUS-SEND-no-will', lambda w: feed(w, IAC + SB + STATUS + b'\x01' + IAC + SE))
    probe('CHARSET-badverb', lambda w: feed(w, IAC + SB + CHARSET + b'\x09' + IAC + SE))
    # client DO LOGOUT needs client role
    wc, tc, _ = make_writer(server=False, client=True)
    try:
        feed(wc, IAC + DO + LOGOUT)
        print('client-DO-LOGOUT -> no-raise writes=', writes_hex(tc))
    except Exception as e:
        print('client-DO-LOGOUT -> RAISED', type(e).__name__, str(e)[:100], 'writes=', writes_hex(tc))


if __name__ == '__main__':
    main()
