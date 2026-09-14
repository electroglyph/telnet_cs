"""D17: Aardwolf 1-byte frame — truth omits data keys, C# yields empty DataBytes."""
import sys
import os
from pathlib import Path
_HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(_HERE / 'stubs'))
sys.path.insert(0, os.environ.get('TELNETLIB3_PATH', '/home/anon/telnetlib3'))
from telnetlib3.mud import aardwolf_decode


def main():
    print('1-byte ->', aardwolf_decode(bytes([0x41])))
    print('2-byte ->', aardwolf_decode(bytes([0x41, 0x07])))


if __name__ == '__main__':
    main()
