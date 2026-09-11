namespace telnet_cs.Protocol
{
  /// <summary>
  /// Wire constants for the LINEMODE option (RFC 1184, option 34).
  /// Inside <c>IAC SB LINEMODE … IAC SE</c> the payload starts with its own
  /// subcommand byte (<see cref="Mode"/>, <see cref="ForwardMask"/>, or
  /// <see cref="SetLocalCharacters"/>); the FORWARDMASK accept/refuse verbs
  /// reuse the TELNET command codes because RFC 1184 §1 defines no alternates.
  /// </summary>
  internal static class LinemodeProtocol
  {
    /// <summary>LINEMODE subcommand: MODE mask confirm/request (1).</summary>
    internal const byte Mode = 1;

    /// <summary>LINEMODE subcommand: forward-mask negotiation (2).</summary>
    internal const byte ForwardMask = 2;

    /// <summary>LINEMODE subcommand: set local characters (3).</summary>
    internal const byte SetLocalCharacters = 3;

    /// <summary>MODE bit: client buffers and edits lines (1).</summary>
    internal const byte Edit = 1;

    /// <summary>MODE bit: map signals to TELNET equivalents (2).</summary>
    internal const byte TrapSignal = 2;

    /// <summary>MODE bit: this MODE reply acknowledges a request (4).</summary>
    internal const byte ModeAck = 4;

    /// <summary>MODE bit: expand HT to spaces (8). Unsupported here.</summary>
    internal const byte SoftTab = 8;

    /// <summary>MODE bit: echo non-printables literally (16). Unsupported here.</summary>
    internal const byte LiteralEcho = 16;

    /// <summary>
    /// MODE bits this library honors. EDIT/TRAPSIG are the only bits a
    /// programmatic client can meaningfully confirm; SOFT_TAB/LIT_ECHO
    /// describe local terminal processing we do not perform, so they are
    /// dropped from replies (a spec-legal subset, RFC 1184 §2.2).
    /// </summary>
    internal const byte SupportedModeBits = Edit | TrapSignal;

    /// <summary>SLC level: function unsupported (0).</summary>
    internal const byte LevelNoSupport = 0;

    /// <summary>SLC level: supported but fixed (1).</summary>
    internal const byte LevelCantChange = 1;

    /// <summary>SLC level: supported, value in the third octet (2).</summary>
    internal const byte LevelValue = 2;

    /// <summary>SLC level: use the peer default (3).</summary>
    internal const byte LevelDefault = 3;

    /// <summary>SLC modifier mask for the level bits (3).</summary>
    internal const byte LevelBits = 3;

    /// <summary>SLC modifier: this triplet acknowledges a request (128).</summary>
    internal const byte FlagAck = 128;

    /// <summary>SLC modifier: also send Telnet sync with the function (64).</summary>
    internal const byte FlagFlushIn = 64;

    /// <summary>SLC modifier: also flush output with the function (32).</summary>
    internal const byte FlagFlushOut = 32;

    /// <summary>Highest defined SLC function code (SLC_EEOL, 30).</summary>
    internal const byte MaxFunction = 30;
  }
}
