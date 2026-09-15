namespace telnet_cs.Fuzz;

using System.Threading;
using System.Threading.Tasks;
using telnet_cs.IO;

/// <summary>
/// Fast deterministic target: feeds one input through a single
/// <see cref="ByteStreamHandler"/> in chunk order and drains it.
/// No background pump, no sockets.
/// </summary>
internal static class ParserHarness
{
    public static async Task RunAsync(FuzzInput input, CancellationToken cancellationToken)
    {
        using var stream = new FuzzStream();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var handler = new ByteStreamHandler(stream, cts, millisecondReadDelay: 1);
        foreach (var (start, length) in Chunks(input))
        {
            cancellationToken.ThrowIfCancellationRequested();
            stream.Enqueue(input.Bytes.AsSpan(start, length));
            _ = await handler.ReadAsync(TimeSpan.FromMilliseconds(15)).ConfigureAwait(false);
        }
    }

    private static IEnumerable<(int Start, int Length)> Chunks(FuzzInput input)
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
