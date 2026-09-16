namespace telnet_cs.Fuzz;

using System.Threading;
using System.Threading.Tasks;
using telnet_cs.Server;

/// <summary>
/// TLS-sniff target: drives a plaintext session (no server certificate) and
/// pins the first-byte sniff — a <c>0x16</c> lead must warn and close instead
/// of being parsed. Anything else must leave the session usable. Returns the
/// connection state folded into the outbound signature.
/// </summary>
internal static class TlsSniffHarness
{
    private const byte TlsLead = 0x16;

    public static async Task<(long Outbound, long Hash)> RunAsync(FuzzInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var options = new TelnetServerOptions
        {
            IdleTimeout = Timeout.InfiniteTimeSpan,
            StatusInterval = null,
            IsWriteConsole = false,
            ServerCertificate = null,
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
            if (!session.IsConnected)
            {
                break;
            }
        }

        if (input.Bytes.Length != 0 && input.Bytes[0] == TlsLead && session.IsConnected)
        {
            throw new InvalidDataException("TLS ClientHello lead byte did not close the plaintext session.");
        }

        var (count, hash) = stream.OutboundSignature();
        return (count, FuzzSignature.Mix(hash, session.IsConnected ? 1 : 0));
    }
}
