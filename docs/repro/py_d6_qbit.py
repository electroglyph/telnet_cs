"""D6: Q-bit collision handling — forget-and-resend vs RFC 1143 queue.

Him-side: after our DONT, an inbound WILL makes truth resend DO immediately
(forgetting the DONT); C# ReceivedWill in WantNo settles silently to No.
Us-side race: WILL out, WONT out (outstanding), inbound DO. Truth stays
silent only because the stale WILL-pending flag suppresses the re-WILL
(and latches local False); C# answers the stale DO with WONT (refuse).
Both directions pinned C#-side by NegotiationStateTests.
"""
import sys
import os
from pathlib import Path
_HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(_HERE / 'stubs'))
sys.path.insert(0, os.environ.get('TELNETLIB3_PATH', '/home/anon/telnetlib3'))
sys.path.insert(0, str(_HERE))
import telnetlib3
from h import make_writer, feed, writes_hex

IAC, WILL, WONT, DO, DONT = telnetlib3.IAC, telnetlib3.WILL, telnetlib3.WONT, telnetlib3.DO, telnetlib3.DONT
SGA = telnetlib3.SGA


def main():
    w, t, _ = make_writer(server=False, client=True)
    print('WILL SGA ->', w.iac(WILL, SGA))
    print('WONT SGA ->', w.iac(WONT, SGA))
    try:
        feed(w, IAC + DO + SGA)
        print('DO after outstanding WONT -> no-raise writes=', writes_hex(t))
    except Exception as e:
        print('DO after outstanding WONT -> RAISED', type(e).__name__, str(e)[:120])

    w2, t2, _ = make_writer(server=False, client=True)
    print('DONT SGA ->', w2.iac(DONT, SGA))
    try:
        feed(w2, IAC + WILL + SGA)
        print('WILL after DONT -> no-raise writes=', writes_hex(t2))
    except Exception as e:
        print('WILL after DONT -> RAISED', type(e).__name__, str(e)[:120])


if __name__ == '__main__':
    main()
