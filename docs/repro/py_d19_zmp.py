"""D19: ZMP list-backed support — truth consults only the check handler.

A client with zmp_supported_commands={'look'} but no handler answers
no-support in truth; C# answers support (handler OR list).
"""
import asyncio
import sys
import os
from pathlib import Path
_HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(_HERE / 'stubs'))
sys.path.insert(0, os.environ.get('TELNETLIB3_PATH', '/home/anon/telnetlib3'))
sys.path.insert(0, str(_HERE))
import telnetlib3
from h import make_writer


async def main():
    c = telnetlib3.TelnetClient(term='unknown', zmp_supported_commands={'look'})
    w, _, _ = make_writer(server=False, client=True)
    c.writer = w  # no connection in this probe; drive the real on_zmp logic
    sent = []
    c.writer.send_zmp = lambda *a: sent.append(a)
    c.on_zmp('zmp.check', 'look')
    print('check listed, no handler ->', sent)
    sent.clear()
    c.on_zmp('zmp.send-support', 'look')
    print('send-support listed, no handler ->', sent)

    c2 = telnetlib3.TelnetClient(term='unknown', zmp_check_handler=lambda cmd: True)
    w2, _, _ = make_writer(server=False, client=True)
    c2.writer = w2
    sent2 = []
    c2.writer.send_zmp = lambda *a: sent2.append(a)
    c2.on_zmp('zmp.check', 'look')
    print('check handler-true, unlisted ->', sent2)


if __name__ == '__main__':
    asyncio.run(main())
