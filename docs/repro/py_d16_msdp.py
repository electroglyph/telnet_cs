"""D16: MSDP tuple value — truth str()s it, C# arrays non-list enumerables."""
import sys
import os
from pathlib import Path
_HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(_HERE / 'stubs'))
sys.path.insert(0, os.environ.get('TELNETLIB3_PATH', '/home/anon/telnetlib3'))
from telnetlib3.mud import msdp_encode


def main():
    print('tuple ->', msdp_encode({'K': ('a', 'b')}).hex())
    print('list ->', msdp_encode({'K': ['a', 'b']}).hex())


if __name__ == '__main__':
    main()
