namespace telnet_cs.Protocol;

using System;
using System.Text;

/// <summary>
/// Small wire-adjacent helpers: LANG parsing and hex dumps.
/// </summary>
public static class TelnetAccessories
{
    private const string HexDigits = "0123456789abcdef";

    /// <summary>
    /// Parses the encoding out of a LANG-style environment value such as
    /// <c>en_US.UTF-8@misc</c> (encoding between the first <c>.</c> and
    /// any <c>@</c> modifier). Returns null when the value carries no
    /// encoding suffix.
    /// </summary>
    public static string? EncodingFromLang(string? lang)
    {
        if (string.IsNullOrEmpty(lang))
        {
            return null;
        }

        var dot = lang.IndexOf('.');
        if (dot < 0)
        {
            return null;
        }

        var encoding = lang[(dot + 1)..];
        var at = encoding.IndexOf('@');
        var codeset = at < 0 ? encoding : encoding[..at];
        return codeset;
    }

    /// <summary>
    /// Formats bytes <c>hexdump -C</c> style: 16 bytes per row with the
    /// offset, two 8-byte hex groups, and printable ASCII on the right
    /// (<c>.</c> for anything outside 0x20-0x7E). Empty input yields the
    /// empty string.
    /// </summary>
    /// <param name="data">The bytes to dump.</param>
    /// <param name="prefix">Prepended to every row.</param>
    public static string Hexdump(ReadOnlySpan<byte> data, string prefix = "")
    {
        ArgumentNullException.ThrowIfNull(prefix);
        if (data.IsEmpty)
        {
            return string.Empty;
        }

        var lines = new StringBuilder();
        for (var offset = 0; offset < data.Length; offset += 16)
        {
            if (offset > 0)
            {
                lines.Append('\n');
            }

            var chunk = data.Slice(offset, Math.Min(16, data.Length - offset));
            lines.Append(prefix);
            lines.Append(offset.ToString("x8", System.Globalization.CultureInfo.InvariantCulture));
            lines.Append("  ");
            AppendHexHalf(lines, chunk, 0);
            lines.Append("  ");
            AppendHexHalf(lines, chunk, 8);
            lines.Append("  |");
            foreach (var b in chunk)
            {
                lines.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
            }

            lines.Append('|');
        }

        return lines.ToString();
    }

    private static void AppendHexHalf(StringBuilder lines, ReadOnlySpan<byte> chunk, int start)
    {
        int count = Math.Max(0, Math.Min(8, chunk.Length - start));
        for (var i = 0; i < count; i++)
        {
            if (i > 0)
            {
                lines.Append(' ');
            }

            byte b = chunk[start + i];
            lines.Append(HexDigits[b >> 4]);
            lines.Append(HexDigits[b & 0xF]);
        }

        // Matches the old half-builder PadRight(23): 8 bytes render exactly
        // 23 characters ("xx" pairs plus 7 separators), shorter halves pad.
        lines.Append(' ', count == 0 ? 23 : 23 - (count * 3 - 1));
    }
}
