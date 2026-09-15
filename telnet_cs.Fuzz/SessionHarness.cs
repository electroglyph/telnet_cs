namespace telnet_cs.Fuzz;

using System.Threading;
using System.Threading.Tasks;
using telnet_cs.Server;

/// <summary>
/// Realistic target: feeds one input through a <see cref="ServerSession"/>,
/// including its background inbound pump and deferred negotiation. Output is
/// irrelevant; only unexpected exceptions count. Sequential use only.
/// </summary>
internal static class SessionHarness
{
    public static async Task RunAsync(FuzzInput input, CancellationToken cancellationToken)
    {
        var options = new TelnetServerOptions
        {
            IdleTimeout = Timeout.InfiniteTimeSpan,
            StatusInterval = null,
            IsWriteConsole = false,
        };
        using var stream = new FuzzStream();
        using var session = new ServerSession(stream, options, CancellationToken.None)
        {
            MillisecondReadDelay = 1,
        };
        foreach (var (start, length) in Chunks(input))
        {
            cancellationToken.ThrowIfCancellationRequested();
            stream.Enqueue(input.Bytes.AsSpan(start, length));
            _ = await session.ReadAsync(TimeSpan.FromMilliseconds(15), cancellationToken).ConfigureAwait(false);
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
