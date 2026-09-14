"""D15: SNDLOC explicit empty send — truth returns '', C# send API throws."""
import sys
import os
from pathlib import Path
_HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(_HERE / 'stubs'))
sys.path.insert(0, os.environ.get('TELNETLIB3_PATH', '/home/anon/telnetlib3'))
sys.path.insert(0, str(_HERE))
import telnetlib3
from h import make_writer


def main():
    w, _, _ = make_writer(server=False, client=True)
    print('handle_send_sndloc() ->', repr(w.handle_send_sndloc()))


if __name__ == '__main__':
    main()
