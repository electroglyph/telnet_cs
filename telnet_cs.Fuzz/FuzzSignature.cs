namespace telnet_cs.Fuzz;

/// <summary>
/// Shared FNV-1a helpers for harness outbound signatures. Every harness
/// reports <c>(count, hash)</c> so the novelty tracker and the reply
/// amplification oracle see behavior, not just wire bytes.
/// </summary>
internal static class FuzzSignature
{
    private const long Offset = unchecked((long)1469598103934665603UL);
    private const long Prime = unchecked((long)1099511628211UL);

    public static long Mix(long hash, long value) => unchecked((hash ^ value) * Prime);

    public static long ForLongs(params ReadOnlySpan<long> values)
    {
        var hash = Offset;
        foreach (var value in values)
        {
            hash = Mix(hash, value);
        }

        return hash;
    }

    public static long ForText(string? text)
    {
        var hash = Offset;
        if (string.IsNullOrEmpty(text))
        {
            return Mix(hash, 0);
        }

        foreach (var c in text)
        {
            hash = Mix(hash, c);
        }

        return Mix(hash, text.Length);
    }

    public static long ForBytes(ReadOnlySpan<byte> bytes)
    {
        var hash = Offset;
        foreach (var b in bytes)
        {
            hash = Mix(hash, b);
        }

        return Mix(hash, bytes.Length);
    }
}
