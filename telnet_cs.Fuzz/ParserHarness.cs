namespace telnet_cs.Fuzz;

using System.Threading;
using System.Threading.Tasks;
using telnet_cs.IO;

/// <summary>
/// Fast deterministic target: feeds one input through a single
/// <see cref="ByteStreamHandler"/> in chunk order and drains it.
/// No background pump, no sockets. Returns the outbound signature for the
/// novelty tracker and the amplification oracle.
/// </summary>
internal static class ParserHarness
{
    public static async Task<(long Outbound, long Hash)> RunAsync(FuzzInput input, CancellationToken cancellationToken)
    {
        using var stream = new FuzzStream();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var handler = new ByteStreamHandler(stream, cts, millisecondReadDelay: 1);
        foreach (var (start, length) in Feed.Chunks(input))
        {
            cancellationToken.ThrowIfCancellationRequested();
            stream.Enqueue(input.Bytes.AsSpan(start, length));
            _ = await handler.ReadAsync(TimeSpan.FromMilliseconds(15)).ConfigureAwait(false);
        }

        return stream.OutboundSignature();
    }
}
