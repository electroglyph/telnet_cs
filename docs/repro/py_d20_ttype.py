"""D20: TTYPE chain hygiene — truth stores empties/dups and keeps overwriting.

C# drops empty answers, strips the terminating repeat, and freezes past
the loop cap. Drives the real TelnetServer.on_ttype with stubbed I/O edges;
request_ttype is a recorder, so the log also proves when the cycle stops
(no further SEND requests) versus continues.
"""
import sys
import os
from pathlib import Path
_HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(_HERE / 'stubs'))
sys.path.insert(0, os.environ.get('TELNETLIB3_PATH', '/home/anon/telnetlib3'))
from types import SimpleNamespace
from telnetlib3.server import TelnetServer


def make_server(calls):
    s = TelnetServer.__new__(TelnetServer)
    s._extra = {}
    s._ttype_count = 0
    s._environ_requested = False
    s.writer = SimpleNamespace(request_ttype=lambda: calls.append(1))
    s.get_extra_info = lambda k, d=None: s._extra.get(k, d)
    s._negotiate_echo = lambda: None
    s._negotiate_environ = lambda: None
    return s


def main():
    s = make_server(calls := [])
    s._ttype_count = 1
    s.on_ttype('XTERM')
    print('fresh answer stored ->', s._extra, 'sends=', len(calls))

    s = make_server(calls := [])
    s._ttype_count = 1
    s.on_ttype('')
    print('empty ttype1 stored ->', s._extra, 'sends=', len(calls))

    s = make_server(calls := [])
    s._ttype_count = 1
    s.on_ttype('XTERM')
    calls.clear()
    s._ttype_count = 2
    s.on_ttype('XTERM')
    print('dup cycle stored ->', s._extra, 'sends=', len(calls))

    s = make_server(calls := [])
    s._extra = {'ttype8': 'A'}
    s._ttype_count = 9
    s.on_ttype('B')
    print('past-loopmax stored ->', s._extra, 'sends=', len(calls))


if __name__ == '__main__':
    main()
