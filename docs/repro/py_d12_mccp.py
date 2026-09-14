"""D12: MCCP START without any agreement — truth activates, C# ignores."""
import sys
import os
from pathlib import Path
_HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(_HERE / 'stubs'))
sys.path.insert(0, os.environ.get('TELNETLIB3_PATH', '/home/anon/telnetlib3'))
sys.path.insert(0, str(_HERE))
import telnetlib3
from h import make_writer, feed, writes_hex

IAC, SB, SE = telnetlib3.IAC, telnetlib3.SB, telnetlib3.SE
MCCP2 = telnetlib3.MCCP2_COMPRESS


def main():
    got = []
    w, t, _ = make_writer(server=False, client=True)
    w.set_ext_callback(cmd=MCCP2, func=lambda on: got.append(on))
    feed(w, IAC + SB + MCCP2 + IAC + SE)
    print('SB MCCP2 unnegotiated -> no-raise writes=', writes_hex(t),
          'activated=', getattr(w, '_mccp2_activated', 'n/a'), 'callback=', got)


if __name__ == '__main__':
    main()
