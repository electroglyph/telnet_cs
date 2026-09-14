"""Fresh probe D5: defaults + tables that audits flagged, now re-checked.

- client defaults: term / tspeed / winsize
- SLC default table live-function count + IP row value (stable bytes)
- TTABLE-IS on truth raises NotImplementedError (nothing sent)
"""
import sys
import os
from pathlib import Path
_HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(_HERE / 'stubs'))
sys.path.insert(0, os.environ.get('TELNETLIB3_PATH', '/home/anon/telnetlib3'))
sys.path.insert(0, str(_HERE))
import telnetlib3
from telnetlib3 import slc as slcmod
from h import make_writer, feed, writes_hex

IAC, SB, SE = telnetlib3.IAC, telnetlib3.SB, telnetlib3.SE


def main():
    import inspect
    sig = inspect.signature(telnetlib3.TelnetClient.__init__)
    print('TelnetClient params=', {k: (v.default if v.default is not inspect.Parameter.empty else '<req>') for k, v in sig.parameters.items() if k in ('term', 'tspeed', 'winsize', 'encoding')})
    tab = slcmod.generate_slctab()
    live = [(f, bytes(tab[f])[:1].hex() if False else '') for f in range(1, 17)]
    print('slc funcs 1..16 present=', [k.hex() if isinstance(k, bytes) else repr(k) for k in sorted(tab.keys(), key=repr)][:22])
    ipkey = slcmod.SLC_IP
    row = tab.get(ipkey)
    print('slc IP row=', 'missing' if row is None
          else f"mask={bytes(row.mask).hex()} val={bytes(row.val).hex()}")
    w, t, _ = make_writer(server=False, client=True)
    try:
        feed(w, IAC + SB + telnetlib3.CHARSET + b'\x04' + IAC + SE)
        print('TTABLE-IS -> no-raise writes=', writes_hex(t))
    except Exception as e:
        print('TTABLE-IS -> RAISED', type(e).__name__, str(e)[:90], 'writes=', writes_hex(t))


if __name__ == '__main__':
    main()
