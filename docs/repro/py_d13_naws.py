"""D13: NAWS 5-byte verb-first shape — truth struct.error, C# accepts."""
import sys
import os
from pathlib import Path
_HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(_HERE / 'stubs'))
sys.path.insert(0, os.environ.get('TELNETLIB3_PATH', '/home/anon/telnetlib3'))
sys.path.insert(0, str(_HERE))
import telnetlib3
from h import make_writer, feed, writes_hex

IAC, SB, SE, NAWS = telnetlib3.IAC, telnetlib3.SB, telnetlib3.SE, telnetlib3.NAWS


def main():
    for label, body in [('4-byte', b'\x00\x50\x00\x19'),
                        ('5-byte-verb-first', b'\x00\x00\x50\x00\x19')]:
        w, t, _ = make_writer(server=False, client=True)
        try:
            feed(w, IAC + SB + NAWS + body + IAC + SE)
            print(label, '-> no-raise writes=', writes_hex(t))
        except Exception as e:
            print(label, '-> RAISED', f"{type(e).__module__}.{type(e).__name__}", str(e)[:100])


if __name__ == '__main__':
    main()
