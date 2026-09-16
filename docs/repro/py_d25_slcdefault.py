"""D25: SLC DEFAULT for an unsupported function raises AttributeError.

In _slc_change, the SLC_DEFAULT branch reads
self.default_slc_tab.get(func).mask with no fallback; func 19 (MCL) is
absent from BSD_SLC_TAB, so .get returns None and .mask raises. (The
next line already uses .get(func, SLC_nosupport()) defensively -- only
the .mask line is unguarded.) C# answers such a request with NOSUPPORT.
"""
import sys
import os
import traceback
from pathlib import Path
_HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(_HERE / 'stubs'))
sys.path.insert(0, os.environ.get('TELNETLIB3_PATH', '/home/anon/telnetlib3'))
sys.path.insert(0, str(_HERE))
from telnetlib3 import slc
from h import make_writer


def main():
    w, t, _ = make_writer(server=True)
    print('func 19 in default tab:', bytes([19]) in w.default_slc_tab)
    try:
        w._slc_change(bytes([19]), slc.SLC(slc.SLC_DEFAULT, 0))
        print('RESULT: no raise, writes=', [b.hex() for b in t.writes])
    except AttributeError as e:
        print('RESULT: RAISED AttributeError:', e)


if __name__ == '__main__':
    main()
