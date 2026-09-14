"""Fresh divergence probe D1: us-side disable queued behind outstanding enable.

Truth (telnetlib3 stream_writer.iac): WILL then WONT goes out immediately
both bytes. Cs NegotiationState.OfferEnable then OfferDisable queues.
"""
import sys
import os
from pathlib import Path
_HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(_HERE / 'stubs'))
sys.path.insert(0, os.environ.get('TELNETLIB3_PATH', '/home/anon/telnetlib3'))
import telnetlib3


def main():
    w, t, _ = make_writer(server=False, client=True)
    r1 = w.iac(telnetlib3.WILL, telnetlib3.SGA)
    r2 = w.iac(telnetlib3.WONT, telnetlib3.SGA)
    print('iac WILL SGA ->', r1)
    print('iac WONT SGA ->', r2)
    print('writes=', writes_hex(t))
    print('local SGA =', w.local_option.enabled(telnetlib3.SGA))


if __name__ == '__main__':
    # import after path setup above; h lives next to this probe
    sys.path.insert(0, str(_HERE))
    from h import make_writer, writes_hex, clear  # noqa: E402
    main()
