"""Shared helper for repros: drive a real telnetlib3 TelnetWriter with stub I/O."""
import sys
import os
from pathlib import Path
_HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(_HERE / 'stubs'))
sys.path.insert(0, os.environ.get('TELNETLIB3_PATH', '/home/anon/telnetlib3'))
import telnetlib3

class MockTransport:
    def __init__(self):
        self.writes = []
        self._closing = False
        self.extra = {}
    def write(self, data: bytes):
        self.writes.append(bytes(data))
    def is_closing(self): return self._closing
    def get_extra_info(self, name, default=None): return self.extra.get(name, default)
    def close(self): self._closing = True

class MockProtocol:
    def __init__(self, info=None):
        self.info = info or {}
        # Observable by TelnetWriter._force_binary_on_protocol, which sets
        # it True (guarded by hasattr); never read by any other writer path.
        self.force_binary = False
    def get_extra_info(self, name, default=None): return self.info.get(name, default)
    async def _drain_helper(self): pass
    def connection_lost(self, exc): pass

def make_writer(server=True, client=False):
    t = MockTransport()
    p = MockProtocol()
    w = telnetlib3.TelnetWriter(t, p, server=server, client=client)
    return w, t, p

def feed(w, data: bytes):
    """Feed bytes one at a time; return list of inband flags."""
    flags = []
    for b in data:
        flags.append(w.feed_byte(bytes([b])))
    return flags

def writes_hex(t):
    return [w.hex() for w in t.writes]

def clear(t):
    t.writes.clear()
