"""D23: MSDP nested TABLE with stray bytes hangs the reference parser.

_parse_table loops while the head byte is not TABLE_CLOSE, but only
VAR advances the cursor -- any other stray byte (here b'XY') leaves
idx stuck and the loop never terminates. The top-level parse() has an
else-advance; _parse_table does not. C# ParseTable skips such bytes.
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
from telnetlib3.telopt import MSDP_VAR, MSDP_VAL, MSDP_TABLE_OPEN, MSDP_TABLE_CLOSE

PAYLOAD = (MSDP_VAR + b'K' + MSDP_VAL + MSDP_TABLE_OPEN
           + b'XY' + MSDP_TABLE_CLOSE)


def main():
    print('payload=', PAYLOAD.hex())
    box = {}
    th = threading.Thread(target=lambda: box.setdefault(
        'ret', msdp_decode(PAYLOAD)), daemon=True)
    th.start()
    th.join(timeout=3.0)
    if th.is_alive():
        print('RESULT: HANG -- msdp_decode did not return within 3s (stray '
              'bytes XY stall _parse_table forever)')
    else:
        print('RESULT: returned', box.get('ret'))


if __name__ == '__main__':
    main()
