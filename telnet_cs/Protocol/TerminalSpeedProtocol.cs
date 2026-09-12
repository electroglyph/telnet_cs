namespace telnet_cs.Protocol
{
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
            if (parts?.Length != 2)
            {
                return null;
            }

            if (!TryParseRate(parts[0], out int tx) || !TryParseRate(parts[1], out int rx))
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

        private static bool TryParseRate(string text, out int rate)
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
