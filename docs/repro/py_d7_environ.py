"""D7: ENVIRON ESC scope — RFC 1408 escapes VAR/VALUE/ESC/USERVAR.

C# escapes and unescapes all four; truth only handles VAR/USERVAR, so an
ESC VALUE pair on the wire is a literal 0x01 in C# but ESC+delimiter in
truth. Trailing ESC: dropped in C#, kept in truth.
"""
import sys
import os
from pathlib import Path
_HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(_HERE / 'stubs'))
sys.path.insert(0, os.environ.get('TELNETLIB3_PATH', '/home/anon/telnetlib3'))
sys.path.insert(0, str(_HERE))
import telnetlib3
from telnetlib3.stream_writer import _decode_env_buf, _escape_environ

VAR, VALUE, ESC, USERVAR = telnetlib3.VAR, telnetlib3.VALUE, telnetlib3.ESC, telnetlib3.USERVAR


def main():
    raw = VAR + b'K' + VALUE + b'a' + ESC + VALUE + b'b'
    print('ESC VALUE value ->', _decode_env_buf(raw))
    print('trailing ESC ->', _decode_env_buf(VAR + b'K' + VALUE + b'v' + ESC))
    print('escape(VALUE) ->', _escape_environ(VALUE).hex())
    print('escape(ESC) ->', _escape_environ(ESC).hex())
    print('escape(VAR) ->', _escape_environ(VAR).hex())


if __name__ == '__main__':
    main()
