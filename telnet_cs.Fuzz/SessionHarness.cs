namespace telnet_cs.Fuzz;

using System.Threading;
using System.Threading.Tasks;
using telnet_cs.Server;

/// <summary>
/// Realistic target: feeds inputs through a <see cref="ServerSession"/>,
/// including its background inbound pump and deferred negotiation. Output is
/// irrelevant; only unexpected exceptions count. Sequential use only.
/// <see cref="RunSequenceAsync"/> drives several inputs through one session
/// to catch state-machine bugs single-shot inputs miss.
/// </summary>
internal static class SessionHarness
{
    public static async Task<(long Outbound, long Hash)> RunAsync(FuzzInput input, CancellationToken cancellationToken)
    {
        using var session = CreateSession(out var stream);
        foreach (var (start, length) in Feed.Chunks(input))
        {
            cancellationToken.ThrowIfCancellationRequested();
            stream.Enqueue(input.Bytes.AsSpan(start, length));
            _ = await session.ReadAsync(TimeSpan.FromMilliseconds(15), cancellationToken).ConfigureAwait(false);
        }

        return stream.OutboundSignature();
    }

    public static async Task<(long Outbound, long Hash)> RunSequenceAsync(IReadOnlyList<FuzzInput> inputs, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        using var session = CreateSession(out var stream);
        foreach (var input in inputs)
        {
            foreach (var (start, length) in Feed.Chunks(input))
            {
                cancellationToken.ThrowIfCancellationRequested();
                stream.Enqueue(input.Bytes.AsSpan(start, length));
                _ = await session.ReadAsync(TimeSpan.FromMilliseconds(15), cancellationToken).ConfigureAwait(false);
            }
        }

        return stream.OutboundSignature();
    }

    private static ServerSession CreateSession(out FuzzStream stream)
    {
        var options = new TelnetServerOptions
        {
            IdleTimeout = Timeout.InfiniteTimeSpan,
            StatusInterval = null,
            IsWriteConsole = false,
        };
        stream = new FuzzStream();
        return new ServerSession(stream, options, CancellationToken.None)
        {
            MillisecondReadDelay = 1,
        };
    }
}
