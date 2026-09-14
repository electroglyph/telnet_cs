"""Fresh probe D8: TSPEED field order.

telnetlib3 documents tspeed as (rx, tx): SEND is answered with "rx,tx"
(first field = rx) and IS is parsed first-field-as-rx.
Cs documents "<tx>,<rx>" (RFC 1079 order): the same wire string means
the opposite assignment.
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

IAC, SB, SE = telnetlib3.IAC, telnetlib3.SB, telnetlib3.SE
TSPEED = telnetlib3.TSPEED


def main():
    got = []
    w, t, _ = make_writer(server=False, client=True)
    w.set_ext_send_callback(cmd=TSPEED, func=lambda: (111, 222))  # (rx, tx)
    w.set_ext_callback(cmd=TSPEED, func=lambda rx, tx: got.append((rx, tx)))
    feed(w, IAC + SB + TSPEED + b'\x01' + IAC + SE)  # SEND
    print('SEND -> writes=', writes_hex(t))
    clear(t)
    feed(w, IAC + SB + TSPEED + b'\x00' + b'111,222' + IAC + SE)  # IS
    print('IS "111,222" -> callback(rx, tx)=', got, 'writes=', writes_hex(t))


if __name__ == '__main__':
    main()
