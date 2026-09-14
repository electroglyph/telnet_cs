"""Fresh probe D6: repeat-refusal resend + client size defaults.

Truth resends WONT for every repeated DO (no refused memory), and the
TelnetClient default window is cols=80, rows=25.
"""
import inspect
import re
import sys
import os
from pathlib import Path
_HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(_HERE / 'stubs'))
sys.path.insert(0, os.environ.get('TELNETLIB3_PATH', '/home/anon/telnetlib3'))
sys.path.insert(0, str(_HERE))
import telnetlib3
from h import make_writer, feed, writes_hex


def main():
    w, t, _ = make_writer(server=True, client=False)
    feed(w, telnetlib3.IAC + telnetlib3.DO + b'\x64')
    print('after DO100:', writes_hex(t))
    feed(w, telnetlib3.IAC + telnetlib3.DO + b'\x64')
    print('after repeat DO100:', writes_hex(t))
    src = inspect.getsource(telnetlib3.TelnetClient.__init__)
    print([line for line in src.splitlines()
           if 'winsize' in line or 'cols' in line.lower() or 'rows' in line.lower()][:8])


if __name__ == '__main__':
    main()
