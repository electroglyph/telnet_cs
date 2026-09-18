namespace telnet_cs.Fuzz;

using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using telnet_cs.Client;

/// <summary>
/// Client-role target: drives a full <see cref="Client"/> (proactive
/// negotiation suppressed for speed) through plain and terminated reads.
/// Inputs are capped below the terminated-read overlong limit, and the
/// no-terminator <see cref="TimeoutException"/> is consumed here — it is a
/// documented control-flow outcome, not a finding. Returns the outbound
/// signature for the novelty tracker and the amplification oracle.
/// </summary>
internal static class ClientHarness
{
    private const int MaxInputBytes = 4096;

    private static readonly Regex LoginPrompt = new("login:", RegexOptions.None, TimeSpan.FromSeconds(1));

    public static async Task<(long Outbound, long Hash)> RunAsync(FuzzInput input, CancellationToken cancellationToken)
    {
        var bytes = Truncate(input);
        using var stream = new FuzzStream();
        using var client = await Client.CreateAsync(stream, TimeSpan.FromSeconds(5), CancellationToken.None, [], skipProactiveNegotiation: true);
        client.MillisecondReadDelay = 1;
        foreach (var (start, length) in Feed.Chunks(bytes))
        {
            cancellationToken.ThrowIfCancellationRequested();
            stream.Enqueue(bytes.Bytes.AsSpan(start, length));
            _ = await client.ReadAsync(TimeSpan.FromMilliseconds(10), cancellationToken).ConfigureAwait(false);
        }

        try
        {
            _ = await client.TerminatedReadAsync("\n", TimeSpan.FromMilliseconds(10)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // By design: no terminator within the window (mirrors the
            // reference TimeoutError); the plain read above already drained.
        }

        try
        {
            _ = await client.TerminatedReadAsync(LoginPrompt, TimeSpan.FromMilliseconds(10)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Same: prompt never arrived.
        }

        return stream.OutboundSignature();
    }

    public static async Task<(long Outbound, long Hash)> RunSequenceAsync(IReadOnlyList<FuzzInput> inputs, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        using var stream = new FuzzStream();
        using var client = await Client.CreateAsync(stream, TimeSpan.FromSeconds(5), CancellationToken.None, [], skipProactiveNegotiation: true);
        client.MillisecondReadDelay = 1;
        foreach (var input in inputs)
        {
            var bytes = Truncate(input);
            foreach (var (start, length) in Feed.Chunks(bytes))
            {
                cancellationToken.ThrowIfCancellationRequested();
                stream.Enqueue(bytes.Bytes.AsSpan(start, length));
                _ = await client.ReadAsync(TimeSpan.FromMilliseconds(10), cancellationToken).ConfigureAwait(false);
            }
        }

        return stream.OutboundSignature();
    }

    private static FuzzInput Truncate(FuzzInput input)
    {
        if (input.Bytes.Length <= MaxInputBytes)
        {
            return input;
        }

        // Splits past the cut would slice out of range; keep only those
        // strictly inside the truncated body.
        return new FuzzInput(
            input.Bytes[..MaxInputBytes],
            [.. input.Splits.Where(static s => s < MaxInputBytes)]);
    }
}
