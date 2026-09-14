"""D18: SyncTERM font id cap — truth first-match wins (None), C# skips oversize."""
import sys
import os
from pathlib import Path
_HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(_HERE / 'stubs'))
sys.path.insert(0, os.environ.get('TELNETLIB3_PATH', '/home/anon/telnetlib3'))
from telnetlib3.server_fingerprinting import detect_syncterm_font


def main():
    print('10000 ->', detect_syncterm_font(b'\x1b[0;10000 D'))
    print('10000-then-0 ->', detect_syncterm_font(b'\x1b[0;10000 D\x1b[0;0 D'))
    print('0 ->', detect_syncterm_font(b'\x1b[0;0 D'))


if __name__ == '__main__':
    main()
