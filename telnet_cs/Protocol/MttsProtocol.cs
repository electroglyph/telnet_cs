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

/// <summary>
/// MTTS capability flags; a client's vector is the sum of its supported
/// flags (e.g. <c>"MTTS 13"</c> = Ansi | Utf8 | Colors256).
/// </summary>
[Flags]
public enum MttsCapabilities
{
    /// <summary>No capabilities reported.</summary>
    None = 0,

    /// <summary>Client supports common ANSI color codes (1).</summary>
    Ansi = 1,

    /// <summary>Client supports common VT100 codes (2).</summary>
    Vt100 = 2,

    /// <summary>Client is using UTF-8 character encoding (4).</summary>
    Utf8 = 4,

    /// <summary>Client supports 256 colors (8).</summary>
    Colors256 = 8,

    /// <summary>Client supports xterm mouse tracking (16).</summary>
    MouseTracking = 16,

    /// <summary>Client supports OSC color palette (32).</summary>
    OscColorPalette = 32,

    /// <summary>Client is using a screen reader (64).</summary>
    ScreenReader = 64,

    /// <summary>Client is a proxy for multiple users (128).</summary>
    Proxy = 128,

    /// <summary>Client supports truecolor (256).</summary>
    Truecolor = 256,

    /// <summary>Client supports MNES information exchange (512).</summary>
    Mnes = 512,

    /// <summary>Client supports MSLP clickable links (1024).</summary>
    Mslp = 1024,

    /// <summary>Client supports SSL/TLS encryption (2048).</summary>
    Ssl = 2048,
}
