namespace telnet_cs.Protocol;

using System;
using System.Collections.Generic;
using System.Text;

/// <summary>
/// SyncTERM/CTerm font-selection detection: servers announce a display
/// font with <c>CSI Ps1 ; Ps2 SP D</c> (ESC [ n ; m SP D), and the font
/// ID selects the byte encoding the rest of the session uses. Map and
/// sequence syntax mirror the reference SyncTERM/CTerm behavior: Ps1 is
/// the font page (ignored here) and Ps2 is the font ID looked up below.
/// </summary>
public static class SyncTermFont
{
    /// <summary>
    /// Gets the SyncTERM font ID to codec-name map. IDs 7, 15, 19, and
    /// 28 are unassigned and absent, like the reference table.
    /// </summary>
    public static readonly IReadOnlyDictionary<int, string> FontEncodings = new Dictionary<int, string>
    {
        [0] = "cp437",
        [1] = "cp1251",
        [2] = "koi8-r",
        [3] = "iso-8859-2",
        [4] = "iso-8859-4",
        [5] = "cp866",
        [6] = "iso-8859-9",
        [8] = "iso-8859-8",
        [9] = "koi8-u",
        [10] = "iso-8859-15",
        [11] = "iso-8859-4",
        [12] = "koi8-r",
        [13] = "iso-8859-4",
        [14] = "iso-8859-5",
        [16] = "iso-8859-15",
        [17] = "cp850",
        [18] = "cp850",
        [20] = "cp1251",
        [21] = "iso-8859-7",
        [22] = "koi8-r",
        [23] = "iso-8859-4",
        [24] = "iso-8859-1",
        [25] = "cp866",
        [26] = "cp437",
        [27] = "cp866",
        [29] = "cp866",
        [30] = "iso-8859-1",
        [31] = "cp1131",
        [32] = "petscii",
        [33] = "petscii",
        [34] = "petscii",
        [35] = "petscii",
        [36] = "atascii",
        [37] = "cp437",
        [38] = "cp437",
        [39] = "cp437",
        [40] = "cp437",
        [41] = "cp437",
        [42] = "cp437",
    };

    /// <summary>
    /// Scans raw inbound bytes for <c>CSI Ps1 ; Ps2 SP D</c> and returns
    /// the codec name for the Ps2 font ID, or null when no well-formed
    /// sequence — or no mapped ID — is present.
    /// </summary>
    public static string? DetectEncoding(ReadOnlySpan<byte> data)
    {
        for (var i = 0; i + 1 < data.Length; i++)
        {
            if (data[i] != 0x1B || data[i + 1] != (byte)'[')
            {
                continue;
            }

            var pos = i + 2;
            if (!TryReadNumber(data, ref pos, out _))
            {
                continue;
            }

            if (pos >= data.Length || data[pos] != (byte)';')
            {
                continue;
            }

            pos++;
            if (!TryReadNumber(data, ref pos, out var fontId))
            {
                continue;
            }

            if (pos + 1 < data.Length && data[pos] == 0x20 && data[pos + 1] == (byte)'D')
            {
                return FontEncodings.TryGetValue(fontId, out var name) ? name : null;
            }
        }

        return null;
    }

    /// <summary>
    /// Resolves a codec name from <see cref="DetectEncoding"/> to an
    /// <see cref="Encoding"/>, or null when the runtime has no such
    /// codec (unknown name or missing code page).
    /// </summary>
    public static Encoding? ResolveEncoding(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        try
        {
            return Encoding.GetEncoding(name);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static bool TryReadNumber(ReadOnlySpan<byte> data, ref int pos, out int value)
    {
        value = 0;
        var start = pos;
        while (pos < data.Length && data[pos] >= (byte)'0' && data[pos] <= (byte)'9')
        {
            value = (value * 10) + (data[pos] - (byte)'0');
            if (value > 9999)
            {
                return false;
            }

            pos++;
        }

        return pos > start;
    }
}
