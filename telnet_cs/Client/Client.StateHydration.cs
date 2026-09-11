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
            handler.Linemode = linemodeState;
            handler.GoAheadReceived = OnGoAheadReceived;
        }

        private TerminalTypeCycler? terminalTypeCycler;
    }
}
