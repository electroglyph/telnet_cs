namespace telnet_cs.Fuzz;

using System.Threading;
using System.Threading.Tasks;
using telnet_cs.IO;
using telnet_cs.Server;

/// <summary>
/// Storm target: pins the negotiation-storm guard contract directly (idle
/// guard is quiet, a 150-frame burst trips it) and then feeds a verb-heavy
/// input through a session. A silent guard is a fail-closed violation worth
/// a finding; the outbound + guard state signature feeds novelty.
/// </summary>
internal static class StormHarness
{
    private const int BurstFrames = 150;

    public static async Task<(long Outbound, long Hash)> RunAsync(FuzzInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var guard = new NegotiationStormGuard();
        if (guard.IsOverThreshold)
        {
            throw new InvalidDataException("Fresh storm guard reports over-threshold with no frames noted.");
        }

        for (var i = 0; i < BurstFrames; i++)
        {
            guard.NoteFrame();
        }

        if (!guard.IsOverThreshold)
        {
            throw new InvalidDataException("Storm guard stayed quiet after a 150-frame burst.");
        }

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
        foreach (var (start, length) in Feed.Chunks(input))
        {
            cancellationToken.ThrowIfCancellationRequested();
            stream.Enqueue(input.Bytes.AsSpan(start, length));
            _ = await session.ReadAsync(TimeSpan.FromMilliseconds(15), cancellationToken).ConfigureAwait(false);
        }

        var (count, hash) = stream.OutboundSignature();
        return (count, FuzzSignature.Mix(hash, session.IsConnected ? 1 : 0));
    }
}
