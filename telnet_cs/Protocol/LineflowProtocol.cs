namespace telnet_cs.Protocol;

/// <summary>
/// Remote flow-control (LFLOW, option 33; RFC 1372) mode bytes carried in
/// <c>IAC SB LFLOW &lt;mode&gt; IAC SE</c> (no SEND/IS verbs).
/// </summary>
public static class LineflowProtocol
{
    /// <summary>No flow control (0).</summary>
    public const byte Off = 0;

    /// <summary>Flow control on (1).</summary>
    public const byte On = 1;

    /// <summary>Restart output on any character (2).</summary>
    public const byte RestartAny = 2;

    /// <summary>Restart output only on XON (3).</summary>
    public const byte RestartXon = 3;

    /// <summary>
    /// Gets whether <paramref name="mode"/> is a defined LFLOW mode byte (0-3).
    /// </summary>
    /// <param name="mode">The candidate mode byte.</param>
    public static bool IsDefined(byte mode)
    {
        return mode <= RestartXon;
    }
}
