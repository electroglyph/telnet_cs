"""Fresh probe D7: STATUS IS pair content.

Server role: bring local SGA + remote BINARY up, then feed DO STATUS.
Reference must answer WILL STATUS + immediate IS; capture the IS pairs.
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
WILL, DO = telnetlib3.WILL, telnetlib3.DO
SGA, BINARY, STATUS = telnetlib3.SGA, telnetlib3.BINARY, telnetlib3.STATUS


def main():
    w, t, _ = make_writer(server=True, client=False)
    feed(w, IAC + WILL + BINARY)  # remote BINARY agreed
    feed(w, IAC + DO + SGA)       # local SGA agreed
    clear(t)
    feed(w, IAC + DO + STATUS)    # server answers WILL + immediate IS
    print('writes=', writes_hex(t))


if __name__ == '__main__':
    main()
