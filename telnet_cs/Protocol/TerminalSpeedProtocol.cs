namespace telnet_cs.Protocol;

/// <summary>
/// RFC 1079 terminal-speed payload handling. The wire/store path is
/// verbatim: the sender's only duties are the <c>"&lt;tx&gt;,&lt;rx&gt;"</c>
/// decimal shape (no leading zeros, no spaces), and the receiver stores
/// what it got. Rounding to a standard rate (RFC 1079 section 5) is a
/// receiver-local concern for padding decisions, so it lives in
/// <see cref="RoundForPadding"/> and never touches the wire.
/// </summary>
internal static class TerminalSpeedProtocol
{
    private static readonly int[] StandardRates =
    [
      50, 75, 110, 134, 150, 300, 600, 1200, 1800, 2400, 4800, 7200,
  9600, 14400, 19200, 28800, 38400, 57600, 115200,
];

    /// <summary>
    /// Validates a speed for the wire/store path without rounding: returns
    /// the <c>"&lt;tx&gt;,&lt;rx&gt;"</c> shape (leading zeros stripped for
    /// RFC 1079 §4 send compliance, values otherwise preserved), or null
    /// when the shape is unusable (not exactly two comma-separated,
    /// non-empty, all-ASCII-digit parts).
    /// </summary>
    internal static string? Validate(string? configured)
    {
        string[]? parts = configured?.Split(',');
        if (parts is null || parts.Length < 2)
        {
            return null;
        }

        string? tx = NormalizeRate(parts[0].Trim());
        string? rx = NormalizeRate(parts[1].Trim());
        if (tx is null || rx is null)
        {
            return null;
        }

        return $"{tx},{rx}";
    }

    /// <summary>
    /// Rounds a rate to the nearest standard rate for local padding
    /// decisions (RFC 1079 section 5: nearest allowed rate, rounding up
    /// when used for padding). Ties round up. Never applied to wire bytes.
    /// </summary>
    internal static int RoundForPadding(int rate)
    {
        return Round(rate);
    }

    /// <summary>
    /// Normalizes one rate for the wire/store path: all-ASCII-digit input
    /// with leading zeros stripped (<c>"000"</c> normalizes to
    /// <c>"0"</c>), or null when empty or non-decimal. String-level on
    /// purpose: rates are opaque decimal text on the wire (RFC 1079 §4),
    /// so values wider than <see cref="int"/> pass through instead of
    /// overflowing into a silent reject.
    /// </summary>
    private static string? NormalizeRate(string text)
    {
        if (text.Length == 0)
        {
            return null;
        }

        foreach (char c in text)
        {
            if (!char.IsAsciiDigit(c))
            {
                return null;
            }
        }

        var stripped = text.TrimStart('0');
        return stripped.Length == 0 ? "0" : stripped;
    }

    private static int Round(int rate)
    {
        int best = StandardRates[0];
        var bestDiff = Math.Abs((long)best - rate);
        foreach (int candidate in StandardRates)
        {
            var diff = Math.Abs((long)candidate - rate);
            if (diff < bestDiff || (diff == bestDiff && candidate > best))
            {
                best = candidate;
                bestDiff = diff;
            }
        }

        return best;
    }
}
