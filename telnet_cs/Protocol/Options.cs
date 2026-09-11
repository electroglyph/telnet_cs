namespace telnet_cs.Protocol
{
  /// <summary>
  /// Telnet option numbers negotiated via WILL/WONT/DO/DONT.
  /// </summary>
  public enum Options
  {
    /// <summary>Transmit binary data (RFC 856, 0).</summary>
    TransmitBinary = 0,
    /// <summary>Echo input back to the sender (RFC 857, 1).</summary>
    Echo = 1,
    /// <summary>Reconnection handling (2).</summary>
    Reconnection = 2,
    /// <summary>Suppress go-ahead signals (RFC 858, 3).</summary>
    SuppressGoAhead = 3,
    /// <summary>Negotiate message size (4).</summary>
    MessageSize = 4,
    /// <summary>Report option status (RFC 859, 5).</summary>
    Status = 5,
    /// <summary>Timing mark for synchronization (RFC 860, 6).</summary>
    TimingMark = 6,
    /// <summary>Remote controlled transmission and echo (RFC 726, 7).</summary>
    RemoteTransEcho = 7,
    /// <summary>Negotiate output line width (8).</summary>
    LineWidth = 8,
    /// <summary>Negotiate output page size (9).</summary>
    PageSize = 9,
    /// <summary>Negotiate carriage-return disposition (10).</summary>
    NAOCRD = 10,
    /// <summary>Negotiate horizontal-tab-stop disposition (11).</summary>
    NAOHTS = 11,
    /// <summary>Negotiate horizontal-tab disposition (12).</summary>
    NAOHTD = 12,
    /// <summary>Negotiate form-feed disposition (13).</summary>
    NAOFFD = 13,
    /// <summary>Negotiate vertical-tab-stop disposition (14).</summary>
    NAOVTS = 14,
    /// <summary>Negotiate vertical-tab disposition (15).</summary>
    NAOVTD = 15,
    /// <summary>Negotiate line-feed disposition (16).</summary>
    NAOLFD = 16,
    /// <summary>Extended ASCII character set (17).</summary>
    ExtendASCII = 17,
    /// <summary>Log out the session (18).</summary>
    Logout = 18,
    /// <summary>Byte macro definitions (19).</summary>
    ByteMacro = 19,
    /// <summary>Data-entry terminal support (20).</summary>
    DataEntryTerminal = 20,
    /// <summary>SUPDUP display protocol (21).</summary>
    SUPDUP = 21,
    /// <summary>SUPDUP output stream (22).</summary>
    SUPDUPOut = 22,
    /// <summary>Send terminal location (23).</summary>
    SendLocation = 23,
    /// <summary>Terminal type identification (RFC 1091, 24).</summary>
    TerminalType = 24,
    /// <summary>End-of-record marker (25).</summary>
    EndOfRecord = 25,
    /// <summary>User identification (TACACS, 26).</summary>
    UserID = 26,
    /// <summary>Output marking for control flow (27).</summary>
    OutputMark = 27,
    /// <summary>Negotiate terminal number (28).</summary>
    TerminalNumber = 28,
    /// <summary>Telnet 3270 regime (29).</summary>
    Regime = 29,
    /// <summary>X.3 PAD parameters (30).</summary>
    PAD = 30,
    /// <summary>Negotiate window size / NAWS (RFC 1073, 31).</summary>
    WindowSize = 31,
    /// <summary>Terminal speed identification (RFC 1079, 32).</summary>
    TerminalSpeed = 32,
    /// <summary>Remote flow control (33).</summary>
    RemoteFlowControl = 33,
    /// <summary>Line mode (RFC 1184, 34).</summary>
    LineMode = 34,
    /// <summary>X display location (35).</summary>
    XDisplay = 35,
    /// <summary>Environment variables, original form (36).</summary>
    OldEnvironment = 36,
    /// <summary>Authentication facilities (37).</summary>
    Authentication = 37,
    /// <summary>Environment variables, new form (RFC 1408, 39).</summary>
    NewEnvironment = 39,
    /// <summary>TN3270 enhancements (40).</summary>
    TN3270 = 40,
    /// <summary>XAUTH authentication (41).</summary>
    XAUTH = 41,
    /// <summary>Character set selection (42).</summary>
    CharacterSet = 42,
    /// <summary>Remote serial port control (43).</summary>
    RemoteSerialPort = 43,
    /// <summary>COM port control (44).</summary>
    COMPortControl = 44,
    /// <summary>Suppress local echo (45).</summary>
    SuppressLocalEcho = 45,
    /// <summary>Start TLS within the session (46).</summary>
    StartTls = 46,
    /// <summary>KERMIT file transfer (47).</summary>
    KERMIT = 47,
    /// <summary>Uniform resource locator (48).</summary>
    URL = 48,
    /// <summary>Forward X Window System (49).</summary>
    ForwardX = 49,
    /// <summary>Pragma logon (138).</summary>
    PragmaLogon = 138,
    /// <summary>SSPI logon (139).</summary>
    SspiLogon = 139,
    /// <summary>Pragma heartbeat (140).</summary>
    PragmaHeartbeat = 140,
    /// <summary>Extended options list (255).</summary>
    ExtendedOptions = 255
  }
}
