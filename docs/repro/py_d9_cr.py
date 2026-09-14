"""Fresh probe D9: CR / LF / NUL layering in the data path.

Feeds A <seq> B for CR NUL, CR LF, bare CR, bare LF, bare NUL and prints
the inband data bytes telnetlib3 delivers for each.
"""
import sys
import os
from pathlib import Path
_HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(_HERE / 'stubs'))
sys.path.insert(0, os.environ.get('TELNETLIB3_PATH', '/home/anon/telnetlib3'))
sys.path.insert(0, str(_HERE))
import telnetlib3
from h import make_writer, feed

CASES = {
    'CR NUL': b'A\r\x00B',
    'CR LF': b'A\r\nB',
    'bare CR': b'A\rB',
    'bare LF': b'A\nB',
    'bare NUL': b'A\x00B',
}


def main():
    for label, blob in CASES.items():
        w, t, _ = make_writer(server=True, client=False)
        data = bytearray()
        for i in range(len(blob)):
            if w.feed_byte(blob[i:i + 1]):
                data += blob[i:i + 1]
        print(f'{label}: data={bytes(data).hex()} writes={[x.hex() for x in t.writes]}')


if __name__ == '__main__':
    main()
