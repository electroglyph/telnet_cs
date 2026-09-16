namespace telnet_cs.Fuzz;

/// <summary>
/// Derives harness strings from fuzz bytes. Latin-1 decoding maps every byte
/// to a non-surrogate char, so the result is always safe to hand to regex,
/// terminator, and converter APIs without tripping argument validation.
/// </summary>
internal static class FuzzText
{
    public static string Latin1(byte[] bytes, int start, int length, int maxChars = 256)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var take = Math.Min(Math.Min(length, bytes.Length - start), maxChars);
        if (take <= 0)
        {
            return string.Empty;
        }

        var chars = new char[take];
        for (var i = 0; i < take; i++)
        {
            chars[i] = (char)bytes[start + i];
        }

        return new string(chars);
    }

    public static string Latin1(byte[] bytes, int maxChars = 256)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return Latin1(bytes, 0, bytes.Length, maxChars);
    }
}
