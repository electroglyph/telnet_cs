"""D11: WILL LOGOUT — truth fires a callback and sends nothing; C# server refuses."""
import sys
import os
from pathlib import Path
_HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(_HERE / 'stubs'))
sys.path.insert(0, os.environ.get('TELNETLIB3_PATH', '/home/anon/telnetlib3'))
sys.path.insert(0, str(_HERE))
import telnetlib3
from h import make_writer, feed, writes_hex

IAC, WILL, LOGOUT = telnetlib3.IAC, telnetlib3.WILL, telnetlib3.LOGOUT


def main():
    got = []
    w, t, _ = make_writer(server=True, client=False)
    w.set_ext_callback(cmd=LOGOUT, func=lambda cmd: got.append(bytes(cmd)))
    feed(w, IAC + WILL + LOGOUT)
    print('server WILL LOGOUT -> callback=', got, 'writes=', writes_hex(t))


if __name__ == '__main__':
    main()
