namespace telnet_cs.Fuzz;

using System.Threading;
using System.Threading.Tasks;
using telnet_cs.Server;

/// <summary>
/// REPL target: streams the input in chunk order with small pauses, then
/// signals EOF and drives <see cref="ServerShells.RunReplAsync"/> to exit.
/// Staggered delivery spreads lines across the prompt loop's read slices,
/// exercising the pending-line accumulation that a single-shot feed never
/// reaches. The stream reports a disconnect once drained, so the loop exits
/// on its own instead of idling into the hang guard. Returns the outbound
/// signature (banner, prompts, Go-Aheads, command output).
/// </summary>
internal static class ReplHarness
{
    public static async Task<(long Outbound, long Hash)> RunAsync(FuzzInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var options = new TelnetServerOptions
        {
            IdleTimeout = Timeout.InfiniteTimeSpan,
            StatusInterval = null,
            IsWriteConsole = false,
            LoginAttemptDelay = TimeSpan.Zero,
        };
        using var stream = new FuzzStream();
        using var session = new ServerSession(stream, options, CancellationToken.None)
        {
            MillisecondReadDelay = 1,
        };
        // Belt and braces: a REPL path that stops consuming (a command that
        // blocks on a live peer) must still terminate under the hang guard.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            var repl = ServerShells.RunReplAsync(session, linked.Token);
            foreach (var (start, length) in Feed.Chunks(input))
            {
                linked.Token.ThrowIfCancellationRequested();
                stream.Enqueue(input.Bytes.AsSpan(start, length));
                await Task.Delay(2, linked.Token).ConfigureAwait(false);
            }

            stream.EofOnDrain = true;
            await repl.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("REPL did not reach EOF within its slice budget.");
        }

        return stream.OutboundSignature();
    }
}
