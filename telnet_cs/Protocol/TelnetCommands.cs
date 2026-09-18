namespace telnet_cs.Protocol;

/// <summary>
/// Shared classification for standalone TELNET control commands (RFC 854,
/// plus EOF/SUSP/ABORT from RFC 1184 §2.5) sent via <c>SendCommand</c>.
/// </summary>
internal static class TelnetCommands
{
    /// <summary>
    /// Reports whether <paramref name="command"/> is a standalone control command
    /// (BRK, IP, AO, AYT, EC, EL, GA, NOP, EOF, SUSP, ABORT). Option negotiation
    /// verbs (DO, DONT, WILL, WONT, SB, SE, IAC) and EOR/DM return false.
    /// </summary>
    /// <param name="command">The command to classify.</param>
    /// <returns>True for standalone control commands.</returns>
    internal static bool IsStandaloneControl(Commands command) => command switch
    {
        Commands.Break => true,
        Commands.InterruptProcess => true,
        Commands.AbortOutput => true,
        Commands.AreYouThere => true,
        Commands.EraseCharacter => true,
        Commands.EraseLine => true,
        Commands.GoAhead => true,
        Commands.NoOperation => true,
        Commands.EndOfFile => true,
        Commands.Suspend => true,
        Commands.Abort => true,
        _ => false,
    };
}
