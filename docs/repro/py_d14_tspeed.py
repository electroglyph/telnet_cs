"""D14: TSPEED signed rates — truth int() accepts, C# Validate rejects."""
import sys
import os
from pathlib import Path
_HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(_HERE / 'stubs'))
sys.path.insert(0, os.environ.get('TELNETLIB3_PATH', '/home/anon/telnetlib3'))
sys.path.insert(0, str(_HERE))
import telnetlib3
from h import make_writer, feed

IAC, SB, SE, TSPEED = telnetlib3.IAC, telnetlib3.SB, telnetlib3.SE, telnetlib3.TSPEED


def main():
    for label, body in [('signed-plus', b'\x00+9600,9600'),
                        ('signed-minus', b'\x00-1,0'),
                        ('plain', b'\x009600,9600')]:
        got = []
        w, t, _ = make_writer(server=False, client=True)
        w.set_ext_callback(cmd=TSPEED, func=lambda rx, tx: got.append((rx, tx)))
        try:
            feed(w, IAC + SB + TSPEED + body + IAC + SE)
            print(label, '-> no-raise callback=', got)
        except Exception as e:
            print(label, '-> RAISED', type(e).__name__, str(e)[:100])


if __name__ == '__main__':
    main()
