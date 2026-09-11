namespace telnet_cs.Protocol
{
    /// <summary>
    /// Telnet protocol command bytes (RFC 854).
    /// </summary>
    public enum Commands
    {
        /// <summary>End of file (EOF, 236; RFC 1184 §2.5).</summary>
        EndOfFile = 236,
        /// <summary>Suspend the current process (SUSP, 237; RFC 1184 §2.5).</summary>
        Suspend = 237,
        /// <summary>Abort or terminate the process (ABORT, 238; RFC 1184 §2.5).</summary>
        Abort = 238,
        /// <summary>End of a subnegotiation block (SE, 240).</summary>
        SubnegotiationEnd = 240,
        /// <summary>No operation (NOP, 241).</summary>
        NoOperation = 241,
        /// <summary>Data mark for the Synch signal (DM, 242; sent out-of-band, never in-band).</summary>
        DataMark = 242,
        /// <summary>Break / attention (BRK, 243).</summary>
        Break = 243,
        /// <summary>Interrupt process (IP, 244).</summary>
        InterruptProcess = 244,
        /// <summary>Suspend, interrupt or abort output (AO, 245).</summary>
        AbortOutput = 245,
        /// <summary>Are you there (AYT, 246).</summary>
        AreYouThere = 246,
        /// <summary>Erase character (EC, 247).</summary>
        EraseCharacter = 247,
        /// <summary>Erase line (EL, 248).</summary>
        EraseLine = 248,
        /// <summary>Go ahead (GA, 249).</summary>
        GoAhead = 249,
        /// <summary>Begin a subnegotiation block (SB, 250).</summary>
        Subnegotiation = 250,
        /// <summary>Offer to perform an option (WILL, 251).</summary>
        Will = 251,
        /// <summary>Refuse to perform an option (WONT, 252).</summary>
        Wont = 252,
        /// <summary>Request the peer perform an option (DO, 253).</summary>
        Do = 253,
        /// <summary>Request the peer stop performing an option (DONT, 254).</summary>
        Dont = 254,
        /// <summary>Interpret the next byte as a command (IAC, 255).</summary>
        InterpretAsCommand = 255
    }
}
