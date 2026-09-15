namespace telnet_cs.Fuzz;

/// <summary>
/// Shared chunk splitter: turns an input's split points into
/// <c>(start, length)</c> ranges for ordered feeding.
/// </summary>
internal static class Feed
{
    public static IEnumerable<(int Start, int Length)> Chunks(FuzzInput input)
    {
        var start = 0;
        foreach (var split in input.Splits)
        {
            yield return (start, split - start);
            start = split;
        }

        yield return (start, input.Bytes.Length - start);
    }
}
