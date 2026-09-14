"""D10: ECHO — server WILL raises in truth; client DO answers WILL once."""
import sys
import os
from pathlib import Path
_HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(_HERE / 'stubs'))
sys.path.insert(0, os.environ.get('TELNETLIB3_PATH', '/home/anon/telnetlib3'))
sys.path.insert(0, str(_HERE))
import telnetlib3
from h import make_writer, feed, writes_hex

IAC, WILL, DO, ECHO = telnetlib3.IAC, telnetlib3.WILL, telnetlib3.DO, telnetlib3.ECHO


def main():
    ws, ts, _ = make_writer(server=True, client=False)
    try:
        feed(ws, IAC + WILL + ECHO)
        print('server WILL ECHO -> no-raise writes=', writes_hex(ts))
    except Exception as e:
        print('server WILL ECHO -> RAISED', type(e).__name__, str(e)[:100])

    wc, tc, _ = make_writer(server=False, client=True)
    feed(wc, IAC + DO + ECHO)
    print('client DO ECHO #1 writes=', writes_hex(tc))
    feed(wc, IAC + DO + ECHO)
    print('client DO ECHO #2 writes=', writes_hex(tc))


if __name__ == '__main__':
    main()
