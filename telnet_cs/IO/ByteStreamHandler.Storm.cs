namespace telnet_cs.IO;

using telnet_cs.Protocol;

public partial class ByteStreamHandler
{
    /// <summary>
    /// Gets or sets the session-owned storm guard. Null for
    /// directly-constructed handlers (no suppression, unchanged behavior).
    /// </summary>
    internal NegotiationStormGuard? StormGuard { get; set; }

    private bool ShouldSuppressStormRefusal()
    {
        return StormGuard is not null && StormGuard.IsOverThreshold;
    }

    private bool ShouldSuppressStormSbReply(int inputOption)
    {
        // TTYPE SEND answers are never suppressed: the TTYPE loop must
        // keep working even while a storm is being shed.
        return inputOption != (int)Options.TerminalType &&
            StormGuard is not null && StormGuard.IsOverThreshold;
    }
}
