"""Fresh probe D3: Synch/DM path — truth only logs handle_dm, delivers data.

Feed b'AB' + IAC DM + b'CD' and show inband text both sides deliver.
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

IAC, DM = telnetlib3.IAC, telnetlib3.DM


def main():
    w, t, _ = make_writer(server=True, client=False)
    out = []
    for chunk in (b'AB', IAC + DM, b'CD'):
        for b in chunk:
            inband = w.feed_byte(bytes([b]))
            out.append((bytes([b]).hex(), inband))
    print('per-byte inband flags=', out)
    print('writes=', [x.hex() for x in t.writes])


if __name__ == '__main__':
    main()
