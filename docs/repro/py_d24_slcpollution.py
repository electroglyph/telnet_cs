"""D24: SLC tables share mutable entries with the global default tab.

TelnetWriter.default_slc_tab is the slc.BSD_SLC_TAB global itself, and
per-session tabs keep references to the same SLC objects (generate_slctab
copies the dict, not the entries; the SLC_DEFAULT reset path uses a
shallow dict() copy). Mutating one session's entry therefore rewrites
the global and every other session. C# keeps per-instance struct tables.
"""
import sys
import os
from pathlib import Path
_HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(_HERE / 'stubs'))
sys.path.insert(0, os.environ.get('TELNETLIB3_PATH', '/home/anon/telnetlib3'))
sys.path.insert(0, str(_HERE))
from telnetlib3 import slc
from h import make_writer


def main():
    w1, _, _ = make_writer(server=True)
    w2, _, _ = make_writer(server=True)
    print('class attr is global:', w1.default_slc_tab is slc.BSD_SLC_TAB)
    print('session entry is global entry:',
          w1.slctab[b'\x04'] is slc.BSD_SLC_TAB[b'\x04'])
    before = (slc.BSD_SLC_TAB[b'\x04'].mask, slc.BSD_SLC_TAB[b'\x04'].val)
    w1.slctab[b'\x04'].set_mask(slc.SLC_NOSUPPORT)
    after_global = (slc.BSD_SLC_TAB[b'\x04'].mask, slc.BSD_SLC_TAB[b'\x04'].val)
    after_other = (w2.slctab[b'\x04'].mask, w2.slctab[b'\x04'].val)
    print('global before:', before, '-> after session-1 mutate:', after_global)
    print('session-2 entry after session-1 mutate:', after_other)
    print('RESULT:', 'POLLUTED -- global + sibling session changed'
          if after_global != before or after_other != before else 'isolated')


if __name__ == '__main__':
    main()
