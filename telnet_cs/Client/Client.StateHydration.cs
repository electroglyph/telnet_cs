namespace telnet_cs.Client
{
    using System.Collections.Generic;
    using telnet_cs.IO;
    using telnet_cs.Protocol;

    public partial class Client
    {
        private void WriteLog(string message)
        {
            Settings.Log?.Invoke(message);
            Trace?.Invoke(message);
            System.Diagnostics.Debug.WriteLine(message);
        }

        private IReadOnlyList<string> BuildTerminalTypes()
        {
            if (Settings.TerminalTypes.Count > 0)
            {
                List<string>? kept = null;
                foreach (string entry in Settings.TerminalTypes)
                {
                    if (string.IsNullOrEmpty(entry))
                    {
                        continue;
                    }

                    (kept ??= []).Add(
                      entry.Length > TerminalTypeCycler.MaxLength ? entry[..TerminalTypeCycler.MaxLength] : entry);
                }

                if (kept is not null)
                {
                    return kept;
                }
            }

            string resolved = Settings.TerminalType ?? TerminalType;
            if (string.IsNullOrEmpty(resolved))
            {
                return [TerminalTypeCycler.Unknown];
            }

            return [resolved.Length > TerminalTypeCycler.MaxLength ? resolved[..TerminalTypeCycler.MaxLength] : resolved];
        }

        private string EffectiveTerminalSpeed => Settings.TerminalSpeed ?? TerminalSpeed;

        private bool EffectiveIsWriteConsole => Settings.IsWriteConsole ?? IsWriteConsole;

        private bool EffectiveEnableBell => Settings.EnableBell ?? true;

        private void FeedHandler(ByteStreamHandler handler)
        {
            handler.Negotiation = Negotiation;
            IReadOnlyList<string> terminalTypes = BuildTerminalTypes();
            if (terminalTypeCycler is null || !terminalTypeCycler.Matches(terminalTypes))
            {
                terminalTypeCycler = new TerminalTypeCycler(terminalTypes);
            }

            handler.TerminalType = terminalTypes[0];
            handler.TerminalTypeProvider = terminalTypeCycler.Next;
            handler.TerminalSpeed = EffectiveTerminalSpeed;
            handler.IsWriteConsole = EffectiveIsWriteConsole;
            handler.AllowRemoteEcho = Settings.AllowRemoteEcho;
            handler.EnableBell = EffectiveEnableBell;
            handler.TextEncoding = Settings.TextEncoding;
            handler.WindowWidth = Settings.WindowWidth;
            handler.WindowHeight = Settings.WindowHeight;
            handler.NawsSizeSent = RecordNawsSize;
            handler.Log = Settings.Log;
            handler.EnvironmentUser = Settings.EnvironmentUser;
            handler.EnvironmentDisplay = Settings.EnvironmentDisplay;
            handler.EnvironmentUserVars = Settings.EnvironmentUserVars;
            handler.XDisplayLocation = Settings.XDisplayLocation;
            handler.SendLocation = Settings.SendLocation;
            handler.CharsetOffers = [.. Settings.CharsetOffers];
            handler.EnableMccp = Settings.EnableMccp;
            // MCCP is refused over TLS (CRIME/BREACH); agreement is
            // session-lived across per-read handlers (see ServerSession).
            handler.IsTlsActive = Settings.UseTls;
            handler.Mccp2Active = mccp2Agreed;
            handler.Mccp3Active = mccp3Agreed;
            handler.MccpStream = mccpStream;
            handler.MccpStateChanged = (mccp2, mccp3, stream) =>
            {
                mccp2Agreed = mccp2;
                mccp3Agreed = mccp3;
                mccpStream = stream;
            };
            handler.EnableMudOptions = Settings.EnableMudOptions;
            handler.EnableComPort = Settings.EnableComPort;
            handler.Linemode = linemodeState;
            handler.GoAheadReceived = OnGoAheadReceived;
            handler.SbResumeState = sbResumeState;
        }

        private TerminalTypeCycler? terminalTypeCycler;
        private bool mccp2Agreed;
        private bool mccp3Agreed;
        private MccpDecompressor? mccpStream;

        /// <summary>
        /// Subnegotiation continuation stashed by the last read, fed into the
        /// next per-read handler so a frame split across reads reassembles
        /// (telnetlib3 _sb_buffer parity).
        /// </summary>
        private (int Option, byte[] Payload, bool OverCap, bool SePending, bool IacPending)? sbResumeState;
    }
}
