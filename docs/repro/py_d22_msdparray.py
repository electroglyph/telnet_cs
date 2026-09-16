"""D22: MSDP stalled array delimiter hangs the reference parser.

ParseArray loops while the head byte is an array element, but ParseValue
consumes nothing for a foreign close byte (here TABLE_CLOSE where a value
was expected) -- the loop never advances and the reader appends until OOM.
C# ParseArray ends the array so the outer frame can consume the byte.
"""
import sys
import os
import threading
from pathlib import Path
_HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(_HERE / 'stubs'))
sys.path.insert(0, os.environ.get('TELNETLIB3_PATH', '/home/anon/telnetlib3'))
sys.path.insert(0, str(_HERE))
from telnetlib3.mud import msdp_decode
from telnetlib3.telopt import MSDP_VAR, MSDP_VAL, MSDP_ARRAY_OPEN, MSDP_TABLE_CLOSE

PAYLOAD = (MSDP_VAR + b'K' + MSDP_VAL + MSDP_ARRAY_OPEN
           + MSDP_TABLE_CLOSE)


def main():
    print('payload=', PAYLOAD.hex())
    box = {}
    th = threading.Thread(target=lambda: box.setdefault(
        'ret', msdp_decode(PAYLOAD)), daemon=True)
    th.start()
    th.join(timeout=3.0)
    if th.is_alive():
        print('RESULT: HANG -- msdp_decode did not return within 3s (stalled '
              'delimiter in _parse_array forever)')
    else:
        print('RESULT: returned', box.get('ret'))


if __name__ == '__main__':
    main()
