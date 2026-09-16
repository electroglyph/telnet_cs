namespace telnet_cs.Fuzz;

using System.Text;

using telnet_cs.Encodings;

/// <summary>
/// Unit-level target: feeds fuzz bytes through every retro-codec incremental
/// decoder at the input's own split points (plus a final flush), stressing
/// split multibyte sequences and lead-byte-at-end states, plus the encode
/// direction and the provider lookups. Fresh codec instances per iteration,
/// so no state leaks across runs. Oracle: decoding with default fallbacks
/// must never throw; encoding unrepresentable characters is the documented
/// loud <see cref="EncoderFallbackException"/> and is consumed here.
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
            EncodeAll(codepage, input, cancellationToken);
        }

        LookupAll(input, cancellationToken);
        return Task.FromResult((0L, 0L));
    }

    private static void EncodeAll(int codepage, FuzzInput input, CancellationToken cancellationToken)
    {
        var encoding = TelnetEncodingProvider.Instance.GetEncoding(codepage);
        ArgumentNullException.ThrowIfNull(encoding);
        var text = FuzzText.Latin1(input.Bytes, 256);
        byte[] bytes;
        try
        {
            // The retro codecs fail loudly on unrepresentable characters
            // instead of emitting "?": that strict contract (also relied on
            // by the byte/string converter) is not a finding.
            bytes = encoding.GetBytes(text);
            _ = encoding.GetByteCount(text);
            var encoder = encoding.GetEncoder();
            _ = encoder.GetByteCount(text.ToCharArray(), 0, text.Length, flush: true);
        }
        catch (EncoderFallbackException)
        {
            bytes = [];
        }

        cancellationToken.ThrowIfCancellationRequested();
        _ = encoding.GetCharCount(input.Bytes);
        _ = encoding.GetMaxByteCount(Math.Min(text.Length, 64));
        _ = encoding.GetMaxCharCount(Math.Min(input.Bytes.Length, 64));
        _ = encoding.GetString(input.Bytes);
        var decoder = encoding.GetDecoder();
        var chars = new char[Math.Max(16, encoding.GetMaxCharCount(Math.Max(1, bytes.Length)))];
        _ = decoder.GetChars(bytes, 0, bytes.Length, chars, 0, flush: true);
    }

    private static void LookupAll(FuzzInput input, CancellationToken cancellationToken)
    {
        var name = FuzzText.Latin1(input.Bytes, 24);
        _ = TelnetEncodingProvider.Instance.GetEncoding(name);
        _ = TelnetEncodingProvider.Instance.GetEncoding(
            name, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        if (input.Bytes.Length > 0)
        {
            _ = TelnetEncodingProvider.Instance.GetEncoding(input.Bytes[0]);
            _ = TelnetEncodingProvider.Instance.GetEncoding(
                80001 + (input.Bytes[0] % 6),
                EncoderFallback.ReplacementFallback,
                DecoderFallback.ReplacementFallback);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var count = 0;
        foreach (var info in TelnetEncodingProvider.Instance.GetEncodings())
        {
            _ = info.Name;
            if (++count > 8)
            {
                break;
            }
        }
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
