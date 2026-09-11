namespace telnet_cs.Protocol
{
  /// <summary>
  /// RFC 1079 terminal-speed payload handling: validates the
  /// <c>"&lt;tx&gt;,&lt;rx&gt;"</c> decimal shape (no leading zeros) and
  /// rounds each rate to the nearest standard rate, ties up (RFC 1079
  /// section 5: nearest allowed rate, up when used for padding).
  /// </summary>
  internal static class TerminalSpeedProtocol
  {
    private static readonly int[] StandardRates =
    [
      50, 75, 110, 134, 150, 300, 600, 1200, 1800, 2400, 4800, 7200,
      9600, 14400, 19200, 28800, 38400, 57600, 115200,
    ];

    /// <summary>
    /// Normalizes a configured speed to a transmittable IS payload, or
    /// returns null when the shape is unusable (not two comma-separated
    /// decimal parts). Leading zeros are stripped; each rate is rounded
    /// to the nearest standard rate (ties round up).
    /// </summary>
    internal static string? Normalize(string? configured)
    {
      string[]? parts = configured?.Split(',');
      if (parts?.Length != 2)
      {
        return null;
      }

      if (!TryNormalizeRate(parts[0], out int tx) || !TryNormalizeRate(parts[1], out int rx))
      {
        return null;
      }

      return $"{Round(tx)},{Round(rx)}";
    }

    private static bool TryNormalizeRate(string text, out int rate)
    {
      rate = 0;
      if (text.Length == 0)
      {
        return false;
      }

      foreach (char c in text)
      {
        if (!char.IsAsciiDigit(c))
        {
          return false;
        }
      }

      return int.TryParse(text.TrimStart('0') is { Length: > 0 } stripped ? stripped : "0", out rate);
    }

    private static int Round(int rate)
    {
      int best = StandardRates[0];
      foreach (int candidate in StandardRates)
      {
        if (Math.Abs(candidate - rate) < Math.Abs(best - rate) ||
          (Math.Abs(candidate - rate) == Math.Abs(best - rate) && candidate > best))
        {
          best = candidate;
        }
      }

      return best;
    }
  }
}
