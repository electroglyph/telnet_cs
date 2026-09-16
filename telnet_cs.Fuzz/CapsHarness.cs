namespace telnet_cs.Fuzz;

using System.Threading;
using System.Threading.Tasks;
using telnet_cs.Server;

/// <summary>
/// Capability-cap target: derives the <c>TelnetServerOptions</c> bounding
/// knobs from the leading input bytes (tiny caps, unlimited zeros, and
/// negotiation-shape flags), then feeds the remaining bytes through reads.
/// Exercises the fail-closed cap paths (buffer caps, environ caps, TTYPE
/// caps, MUD list caps) that default options never reach. The log recorder
/// and the collector counts join the outbound signature.
/// </summary>
internal static class CapsHarness
{
    private const int KnobBytes = 8;

    public static async Task<(long Outbound, long Hash)> RunAsync(FuzzInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var knobs = input.Bytes;
        var logs = new List<string>();
        var options = new TelnetServerOptions
        {
            IdleTimeout = Timeout.InfiniteTimeSpan,
            StatusInterval = null,
            IsWriteConsole = false,
            MaxReplLineLength = Pick(At(knobs, 0), [0, 1, 2, 8, 64, 4096]),
            MaxEnvironVars = Pick(At(knobs, 1), [0, 1, 2, 4, 128]),
            MaxTtypeChars = Pick(At(knobs, 2), [0, 1, 4, 256]),
            MaxMudListItems = Pick(At(knobs, 3), [0, 1, 3, 128]),
            MaxBufferedTextChars = Pick(At(knobs, 4), [0, 1, 16, 256, 65536]),
            MaxEnvironValueChars = Pick(At(knobs, 5), [0, 1, 8, 4096]),
            MaxEnvironKeyChars = Pick(At(knobs, 6), [0, 1, 8, 256]),
            MaxMudKeys = Pick(At(knobs, 7), [0, 1, 4, 128]),
            DisableAllNegotiation = (At(knobs, 5) & 0x10) != 0,
            OfferEcho = (At(knobs, 5) & 0x20) == 0,
            RequestXDisplay = (At(knobs, 6) & 0x10) != 0,
            RequestLinemode = (At(knobs, 6) & 0x20) != 0,
            EnableMccp = (At(knobs, 7) & 0x10) == 0,
            Log = message =>
            {
                lock (logs)
                {
                    if (logs.Count < 64)
                    {
                        logs.Add(message);
                    }
                }
            },
        };
        options.MaxMudListBytes = options.MaxMudListItems == 0 ? 0 : 512;
        using var stream = new FuzzStream();
        using var session = new ServerSession(stream, options, CancellationToken.None)
        {
            MillisecondReadDelay = 1,
        };
        var body = knobs.Length > KnobBytes ? knobs[KnobBytes..] : [];
        foreach (var (start, length) in Feed.Chunks(new FuzzInput(body, [])))
        {
            cancellationToken.ThrowIfCancellationRequested();
            stream.Enqueue(body.AsSpan(start, length));
            _ = await session.ReadAsync(TimeSpan.FromMilliseconds(15), cancellationToken).ConfigureAwait(false);
        }

        var hash = FuzzSignature.ForLongs(
            logs.Count,
            session.ClientTerminalTypes.Count,
            session.ClientEnvironment.Count,
            session.ClientNewEnvironment.Count,
            session.MspData.Count,
            session.MxpData.Count,
            session.ZmpData.Count,
            session.AardwolfData.Count,
            session.AtcpData.Count,
            session.MsspData?.Count ?? -1,
            session.ClientWindowSize.HasValue ? 1 : 0,
            session.PeerStatusReport?.Count ?? -1,
            session.IsConnected ? 1 : 0);
        var (count, wire) = stream.OutboundSignature();
        return (count, FuzzSignature.Mix(wire, hash));
    }

    private static int At(byte[] knobs, int index) => index < knobs.Length ? knobs[index] : 0;

    private static int Pick(int selector, int[] choices) => choices[selector % (uint)choices.Length];
}
