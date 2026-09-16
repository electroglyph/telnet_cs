"""D26: pre-CHARSET MSDP/MSSP decode tries ASCII first, mojibakes UTF-8.

The MUD subnegotiation handlers decode with
`self.environ_encoding or "utf-8"`, but environ_encoding defaults to
"ascii", so before any CHARSET negotiation a UTF-8 value (e' =
C3 A9) fails ASCII and falls back to latin-1, yielding 'Ã©' mojibake. C# decodes
MUD payloads UTF-8-first (strict UTF-8, latin-1 fallback), yielding 'e''
with no negotiation needed.
"""
import sys
import os
from pathlib import Path
_HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(_HERE / 'stubs'))
sys.path.insert(0, os.environ.get('TELNETLIB3_PATH', '/home/anon/telnetlib3'))
sys.path.insert(0, str(_HERE))
from telnetlib3.mud import msdp_decode
from telnetlib3.telopt import MSDP_VAR, MSDP_VAL
from h import make_writer

PAYLOAD = MSDP_VAR + b'K' + MSDP_VAL + 'é'.encode('utf-8')


def main():
    w, _, _ = make_writer(server=True)
    print('default environ_encoding=', repr(w.environ_encoding))
    got = msdp_decode(PAYLOAD, encoding=w.environ_encoding or 'utf-8')
    print('reference decodes C3A9 as:', repr(got.get('K')),
          '(mojibake)' if got.get('K') != 'é' else '(correct)')
    print('RESULT:', 'DIVERGES' if got.get('K') != 'é' else 'same')


if __name__ == '__main__':
    main()
