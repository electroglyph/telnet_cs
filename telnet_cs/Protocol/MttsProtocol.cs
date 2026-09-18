namespace telnet_cs.Protocol;

/// <summary>
/// MUD Terminal Type Standard helpers (MTTS, layered on TTYPE 24). The
/// third TTYPE answer may be <c>"MTTS &lt;bitvector&gt;"</c>, the decimal sum
/// of the capability flags in <see cref="MttsCapabilities"/> (see
/// <c>docs/mud-protocols/mtts.md</c>). The vector is informational only: the
/// effective terminal type stays the second chain entry.
/// </summary>
public static class MttsProtocol
{
    /// <summary>
    /// Parses an <c>"MTTS &lt;bitvector&gt;"</c> chain entry into its numeric
    /// vector. Returns false for null/empty entries, a missing
    /// <c>"MTTS "</c> prefix, or a non-decimal vector.
    /// </summary>
    public static bool TryParseBitvector(string? entry, out int bitvector)
    {
        bitvector = 0;
        if (string.IsNullOrWhiteSpace(entry))
        {
            return false;
        }

        const string prefix = "MTTS ";
        var text = entry.Trim();
        if (!text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return int.TryParse(
            text.AsSpan(prefix.Length).Trim(),
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out bitvector) && bitvector >= 0;
    }
}
