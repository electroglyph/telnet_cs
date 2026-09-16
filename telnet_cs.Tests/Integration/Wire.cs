// Byte-frame matchers for live wire pins: count/presence of exact
// IAC frames in a WireTap capture. Non-overlapping scan; the needle is
// an exact frame (e.g. [IAC, DO, 24]), so a longer SB frame never
// collides with a 3-byte verb probe.
namespace telnet_cs.Tests
{
    using System;

    internal static class Wire
    {
        internal const byte Iac = 255;
        internal const byte Will = 251;
        internal const byte Wont = 252;
        internal const byte Do = 253;
        internal const byte Dont = 254;
        internal const byte Sb = 250;
        internal const byte Se = 240;
        internal const byte Send = 1;

        internal static int CountFrames(byte[] wire, params byte[] frame)
        {
            ArgumentNullException.ThrowIfNull(wire);
            ArgumentNullException.ThrowIfNull(frame);
            if (frame.Length == 0 || wire.Length < frame.Length)
            {
                return 0;
            }

            int count = 0;
            for (int i = 0; i <= wire.Length - frame.Length;)
            {
                bool hit = true;
                for (int j = 0; j < frame.Length; j++)
                {
                    if (wire[i + j] != frame[j])
                    {
                        hit = false;
                        break;
                    }
                }

                if (hit)
                {
                    count++;
                    i += frame.Length;
                }
                else
                {
                    i++;
                }
            }

            return count;
        }

        internal static bool ContainsFrame(byte[] wire, params byte[] frame) =>
            CountFrames(wire, frame) > 0;
    }
}
