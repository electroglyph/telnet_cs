namespace telnet_cs.Client
{
    using System.Collections.Generic;
    using telnet_cs.IO;
    using telnet_cs.Protocol;
    using telnet_cs.Transport;

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

                    // Reference sends each type verbatim (no length cap).
                    (kept ??= []).Add(entry);
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

            return [resolved];
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
            handler.Mccp3StartSent = NoteMccp3StartSent;
            handler.EnableMudOptions = Settings.EnableMudOptions;
            handler.EnableGmcp = Settings.EnableGmcp;
            handler.EnableZmp = Settings.EnableZmp;
            handler.ZmpSupportedCommands = [.. Settings.ZmpSupportedCommands];
            handler.ZmpCheckHandler = Settings.ZmpCheckHandler;
            handler.ZmpIdentSent = zmpIdentSent;
            handler.EnableComPort = Settings.EnableComPort;
            handler.Linemode = linemodeState;
            handler.GoAheadReceived = OnGoAheadReceived;
            // RFC 727: a DO LOGOUT asks us to end the session. No
            // negotiation bytes go out; the hook closes the stream.
            // Close is idempotent, so repeat DOs are harmless.
            handler.LogoutRequested = () => ByteStream.Close();
            handler.SbResumeState = sbResumeState;
            handler.FramingState = framingState;
        }

        private TerminalTypeCycler? terminalTypeCycler;
        private bool mccp2Agreed;
        private bool mccp3Agreed;
        private bool zmpIdentSent;
        private MccpDecompressor? mccpStream;

        // Outbound MCCP3 compression (client role): null while the wire stays
        // plaintext, otherwise the compressing view every client and handler
        // write goes through. Published by NoteMccp3StartSent once our empty
        // SB start marker went out raw; reads, closes and urgent sends keep
        // using the raw ByteStream.
        private MccpWriteFilter? mccp3Filter;
        private readonly Lock mccp3Gate = new();

        /// <inheritdoc/>
        protected override IByteStream WriteStream => SelectWriteStream();

        private IByteStream SelectWriteStream()
        {
            lock (mccp3Gate)
            {
                if (mccp3Filter is not null && !Negotiation.IsEnabledByPeer((int)Options.Mccp3))
                {
                    // The peer took compression back: later writes go out
                    // raw; bytes already compressed stay that way — the wire
                    // cannot un-compress mid-stream.
                    mccp3Filter.Dispose();
                    mccp3Filter = null;
                }

                return mccp3Filter ?? ByteStream;
            }
        }

        private void NoteMccp3StartSent()
        {
            lock (mccp3Gate)
            {
                if (mccp3Filter is not null || !Negotiation.IsEnabledByPeer((int)Options.Mccp3))
                {
                    return;
                }

                mccp3Filter = new MccpWriteFilter(ByteStream, new MccpCompressor());
            }
        }

        /// <summary>
        /// Releases the outbound MCCP3 filter (if any) before the base
        /// session teardown closes the raw stream.
        /// </summary>
        /// <param name="disposing"><c>true</c> to release managed resources.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                lock (mccp3Gate)
                {
                    mccp3Filter?.Dispose();
                    mccp3Filter = null;
                }
            }

            base.Dispose(disposing);
        }

        /// <summary>
        /// Subnegotiation continuation stashed by the last read, fed into the
        /// next per-read handler so a frame split across reads reassembles
        /// (telnetlib3 _sb_buffer parity).
        /// </summary>
        private (int Option, byte[] Payload, bool OverCap, bool SePending, bool IacPending, bool HeaderIacPending)? sbResumeState;
        private (bool PendingIac, int? PendingVerb, bool SawCr, int? Pushback) framingState;
    }
}
