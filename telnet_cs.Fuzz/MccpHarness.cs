namespace telnet_cs.Fuzz;

using telnet_cs.IO;
using telnet_cs.Transport;

/// <summary>
/// MCCP-direct target: feeds fuzz bytes into a fresh
/// <see cref="MccpDecompressor"/> at the input's own split points, runs a
/// compressor-to-decompressor round-trip over the fuzz plaintext, and drives
/// the <see cref="MccpWriteFilter"/> write paths. The wire harnesses only
/// reach MCCP through a negotiated session; this hits the inflater,
/// checksum, end-detection, and cap paths directly. Oracle: no throw, plus
/// byte-exact round-trips (a mismatch or a failed inflate of compressor
/// output is reported as a finding).
/// </summary>
internal static class MccpHarness
{
    public static async Task<(long Outbound, long Hash)> RunAsync(FuzzInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        cancellationToken.ThrowIfCancellationRequested();
        FeedRaw(input, cancellationToken);
        RoundTrip(input, cancellationToken);
        var signature = await WriteFilterAsync(input, cancellationToken).ConfigureAwait(false);
        return signature;
    }

    private static void FeedRaw(FuzzInput input, CancellationToken cancellationToken)
    {
        using var decompressor = new MccpDecompressor();
        if (input.Bytes.Length > 1)
        {
            // Straddle the cap paths on some inputs; unlimited otherwise.
            decompressor.MaxDecompressedBytes = input.Bytes[0] % 2 == 0 ? 0 : 1 + (input.Bytes[1] % 4096);
            decompressor.MaxCompressedBytes = input.Bytes[0] % 3 == 0 ? 0 : 1 + (input.Bytes[1] % 2048);
        }

        foreach (var (start, length) in Feed.Chunks(input))
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var i = start; i < start + length; i++)
            {
                decompressor.Feed(input.Bytes[i]);
            }

            Drain(decompressor);
        }

        Drain(decompressor);
        _ = decompressor.HasOutput;
        _ = decompressor.HasTrailing;
        _ = decompressor.StreamEnded;
        _ = decompressor.Failed;
        _ = decompressor.IsActive;
    }

    private static void Drain(MccpDecompressor decompressor)
    {
        while (decompressor.TryTakeReady(out _))
        {
        }

        while (decompressor.TryTakeTrailing(out _))
        {
        }
    }

    private static void RoundTrip(FuzzInput input, CancellationToken cancellationToken)
    {
        using var compressor = new MccpCompressor();
        var wire = new List<byte>();
        foreach (var (start, length) in Feed.Chunks(input))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (length != 0)
            {
                wire.AddRange(compressor.CompressChunk(input.Bytes, start, length));
            }
        }

        using var decompressor = new MccpDecompressor();
        foreach (var b in wire)
        {
            cancellationToken.ThrowIfCancellationRequested();
            decompressor.Feed(b);
        }

        var plain = new List<byte>();
        while (decompressor.TryTakeReady(out var b))
        {
            plain.Add(b);
        }

        if (decompressor.Failed)
        {
            throw new InvalidDataException("MCCP round-trip: valid compressor output failed to inflate.");
        }

        if (plain.Count != input.Bytes.Length || !plain.SequenceEqual(input.Bytes))
        {
            throw new InvalidDataException(
                $"MCCP round-trip mismatch: {input.Bytes.Length} plaintext bytes became {plain.Count} inflated bytes.");
        }
    }

    private static async Task<(long Outbound, long Hash)> WriteFilterAsync(FuzzInput input, CancellationToken cancellationToken)
    {
        using var inner = new FuzzStream();
        using var compressor = new MccpCompressor();
        using var filter = new MccpWriteFilter(inner, compressor);
        var bytes = input.Bytes;
        if (bytes.Length != 0)
        {
            var half = bytes.Length / 2;
            await filter.WriteAsync(bytes, 0, half, cancellationToken).ConfigureAwait(false);
            await filter.WriteAsync(bytes, half, bytes.Length - half, cancellationToken).ConfigureAwait(false);
            await filter.WriteByteAsync(bytes[0], cancellationToken).ConfigureAwait(false);
        }

        await filter.WriteAsync(FuzzText.Latin1(bytes, 128), cancellationToken).ConfigureAwait(false);
        return inner.OutboundSignature();
    }
}
