namespace telnet_cs.Fuzz;

using telnet_cs.Encodings;

/// <summary>
/// Unit-level target: feeds fuzz bytes through every retro-codec incremental
/// decoder at the input's own split points (plus a final flush), stressing
/// split multibyte sequences and lead-byte-at-end states. Fresh codec
/// instances per iteration, so no state leaks across runs. Oracle: decoding
/// with default fallbacks must never throw.
/// </summary>
internal static class EncodingHarness
{
    private static readonly int[] Codepages = [80001, 80002, 80003, 80004];

    public static Task<(long Outbound, long Hash)> RunAsync(FuzzInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        foreach (var codepage in Codepages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DecodeSplit(codepage, input);
        }

        return Task.FromResult((0L, 0L));
    }

    private static void DecodeSplit(int codepage, FuzzInput input)
    {
        var encoding = TelnetEncodingProvider.Instance.GetEncoding(codepage);
        ArgumentNullException.ThrowIfNull(encoding);
        var decoder = encoding.GetDecoder();
        foreach (var (start, length) in Feed.Chunks(input))
        {
            if (length == 0)
            {
                continue;
            }

            var chars = new char[Math.Max(16, encoding.GetMaxCharCount(length))];
            _ = decoder.GetChars(input.Bytes, start, length, chars, 0, flush: false);
        }

        var tail = new char[16];
        _ = decoder.GetChars([], 0, 0, tail, 0, flush: true);
    }
}
