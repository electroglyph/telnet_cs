namespace telnet_cs.Fuzz;

using System.Threading;
using System.Threading.Tasks;
using telnet_cs.Server;

/// <summary>
/// Login-path target: drives <see cref="ServerSession.AuthenticateAsync"/>
/// with fuzz bytes as the credential stream, alternating accept/reject
/// validators by input parity. The short auth timeout keeps starved inputs
/// (fewer than two credential lines) cheap. Oracle: no unexpected throw;
/// the boolean outcome is irrelevant.
/// </summary>
internal static class AuthHarness
{
    public static async Task<(long Outbound, long Hash)> RunAsync(FuzzInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var options = new TelnetServerOptions
        {
            IdleTimeout = Timeout.InfiniteTimeSpan,
            StatusInterval = null,
            IsWriteConsole = false,
            // No inter-attempt throttle: rejecting inputs burn all three
            // attempts, and the 1 s default delay would blow the
            // per-iteration hang guard on every one of them.
            LoginAttemptDelay = TimeSpan.Zero,
        };
        using var stream = new FuzzStream();
        stream.Enqueue(input.Bytes);
        using var session = new ServerSession(stream, options, CancellationToken.None)
        {
            MillisecondReadDelay = 1,
        };
        var accept = input.Bytes.Length % 2 == 0;
        _ = await session.AuthenticateAsync(
            (_, _) => Task.FromResult(accept),
            TimeSpan.FromMilliseconds(25),
            cancellationToken).ConfigureAwait(false);
        return stream.OutboundSignature();
    }
}
