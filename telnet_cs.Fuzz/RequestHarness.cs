namespace telnet_cs.Fuzz;

using System.Threading;
using System.Threading.Tasks;
using telnet_cs.Server;

/// <summary>
/// Request target: establishes negotiation state from the input, then fires
/// one server-role <c>Request*Async</c> collector with a short timeout. The
/// request is picked by the first input byte so every collector is reachable;
/// the answering bytes come from the rest of the input. Returns the outbound
/// signature mixed with a hash of the collected values, so novel answers
/// count as novel behavior.
/// </summary>
internal static class RequestHarness
{
    private static readonly TimeSpan PollTimeout = TimeSpan.FromMilliseconds(30);

    public static async Task<(long Outbound, long Hash)> RunAsync(FuzzInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
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
        // Establish WILL state first: the requesters refuse to SEND without
        // a peer WILL, so the answers only flow after these priming reads.
        var body = input.Bytes.Length > 1 ? input.Bytes[1..] : [];
        foreach (var (start, length) in Feed.Chunks(new FuzzInput(body, [])))
        {
            cancellationToken.ThrowIfCancellationRequested();
            stream.Enqueue(body.AsSpan(start, length));
            _ = await session.ReadAsync(TimeSpan.FromMilliseconds(15), cancellationToken).ConfigureAwait(false);
        }

        var which = input.Bytes.Length == 0 ? 0 : input.Bytes[0] % 7;
        var collected = which switch
        {
            0 => string.Join("\n", await session.RequestTerminalTypesAsync(PollTimeout, cancellationToken).ConfigureAwait(false)),
            1 => await session.RequestTerminalSpeedAsync(PollTimeout, cancellationToken).ConfigureAwait(false) ?? string.Empty,
            2 => await session.RequestXDisplayAsync(PollTimeout, cancellationToken).ConfigureAwait(false) ?? string.Empty,
            3 => Flatten(await session.RequestEnvironmentAsync(PollTimeout, null, cancellationToken).ConfigureAwait(false)),
            4 => Flatten(await session.RequestNewEnvironmentAsync(PollTimeout, null, cancellationToken).ConfigureAwait(false)),
            5 => await session.RequestCharsetAsync(PollTimeout, cancellationToken).ConfigureAwait(false) ?? string.Empty,
            _ => await session.RequestSendLocationAsync(PollTimeout, cancellationToken).ConfigureAwait(false) ?? string.Empty,
        };
        var (count, hash) = stream.OutboundSignature();
        return (count, FuzzSignature.Mix(hash, FuzzSignature.ForText(collected)));
    }

    private static string Flatten(IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return string.Join("\n", values.OrderBy(static kv => kv.Key, StringComparer.Ordinal).Select(static kv => $"{kv.Key}={kv.Value}"));
    }
}
