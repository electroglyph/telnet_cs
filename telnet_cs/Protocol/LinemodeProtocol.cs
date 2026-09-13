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
        /// The server's initial MODE proposal (reference
        /// <c>default_linemode</c>): remoting on with literal echo, i.e.
        /// <c>REMOTE | LIT_ECHO = 0x10</c>.
        /// </summary>
        internal const byte DefaultServerMode = LiteralEcho;

        /// <summary>
        /// MODE bits this library honors. Retained for compatibility; MODE replies
        /// echo the suggested mask verbatim plus ACK, matching the reference
        /// behavior, so all bits round-trip even when local processing does not
        /// implement them.
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

        /// <summary>SLC function: SYNCH (1). Sent via the urgent Synch path, never <c>SendCommand</c>.</summary>
        internal const byte SlcSynch = 1;

        /// <summary>SLC function: BRK (2).</summary>
        internal const byte SlcBreak = 2;

        /// <summary>SLC function: IP (3).</summary>
        internal const byte SlcIp = 3;

        /// <summary>SLC function: AO (4).</summary>
        internal const byte SlcAbortOutput = 4;

        /// <summary>SLC function: AYT (5).</summary>
        internal const byte SlcAyt = 5;

        /// <summary>SLC function: ABORT (7).</summary>
        internal const byte SlcAbort = 7;

        /// <summary>SLC function: EOF (8).</summary>
        internal const byte SlcEof = 8;

        /// <summary>SLC function: SUSP (9).</summary>
        internal const byte SlcSuspend = 9;

        /// <summary>SLC function: EC (10).</summary>
        internal const byte SlcEraseCharacter = 10;

        /// <summary>SLC function: EL (11).</summary>
        internal const byte SlcEraseLine = 11;

        /// <summary>SLC function: RP (reprint, 13).</summary>
        internal const byte SlcReprint = 13;

        /// <summary>SLC function: LNEXT (literal-next, 14).</summary>
        internal const byte SlcLiteralNext = 14;

        /// <summary>SLC function: XON (15).</summary>
        internal const byte SlcXon = 15;

        /// <summary>SLC function: XOFF (16).</summary>
        internal const byte SlcXoff = 16;

        /// <summary>
        /// Builds the <c>DO FORWARDMASK</c> mask bytes the server sends after
        /// every SLC block (RFC 1184 §2.3): 32 zero bytes under BINARY, else
        /// 16. The reference generates its mask by snooping the SLC table for
        /// trigger values, but passes an empty callback map, so no row ever
        /// matches and the wire mask is unconditionally zeros (executed);
        /// this reproduces those exact bytes without the dead search.
        /// </summary>
        /// <param name="binaryMode">Whether BINARY is agreed (32-byte form).</param>
        internal static byte[] BuildForwardMask(bool binaryMode) => new byte[binaryMode ? 32 : 16];

        /// <summary>SLC function: FORW1 (17).</summary>
        internal const byte SlcForward1 = 17;

        /// <summary>SLC function: FORW2 (18). Only valid while FORW1 is in use (RFC 1184 §5.5).</summary>
        internal const byte SlcForward2 = 18;

        /// <summary>
        /// Maps a standalone control command to its SLC function code, or null
        /// when the command has none: GA/NOP are not SLC functions, and SYNCH/DM
        /// travels the urgent Synch path rather than <c>SendCommand</c>. EC/EL map
        /// to 10/11 — they are editing functions in the RFC 1184 code table, so a
        /// FLUSH flag negotiated on them fires like any other (§5.8 "whenever this
        /// function is sent").
        /// </summary>
        /// <param name="command">The control command about to be sent.</param>
        /// <returns>The SLC function code, or null for no SLC row.</returns>
        internal static byte? SlcFunctionForCommand(Commands command) => command switch
        {
            Commands.Break => SlcBreak,
            Commands.InterruptProcess => SlcIp,
            Commands.AbortOutput => SlcAbortOutput,
            Commands.AreYouThere => SlcAyt,
            Commands.Abort => SlcAbort,
            Commands.EndOfFile => SlcEof,
            Commands.Suspend => SlcSuspend,
            Commands.EraseCharacter => SlcEraseCharacter,
            Commands.EraseLine => SlcEraseLine,
            _ => null,
        };
    }
}
