namespace telnet_cs.Fuzz;

using System.Text.RegularExpressions;
using telnet_cs.Client;
using telnet_cs.Server;

/// <summary>
/// Terminator target: drives the terminated-read and exact-read APIs with
/// fuzz-derived terminators, patterns, and counts — the <see cref="ClientHarness"/>
/// only uses a fixed <c>"\n"</c> and <c>login:</c> pattern. Each probe
/// refills the stream so every variant sees the same inbound bytes. Oracle:
/// no unexpected throw. <see cref="TimeoutException"/> (no terminator in
/// the window), <see cref="EndOfStreamException"/> (exact read past EOF),
/// and regex construction/match timeouts are documented control flow and
/// are consumed here.
/// </summary>
internal static class TermHarness
{
    private static readonly TimeSpan Slice = TimeSpan.FromMilliseconds(10);

    public static async Task<(long Outbound, long Hash)> RunAsync(FuzzInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        using var clientStream = new FuzzStream();
        using var client = await Client.CreateAsync(clientStream, TimeSpan.FromSeconds(5), CancellationToken.None, [], skipProactiveNegotiation: true);
        client.MillisecondReadDelay = 1;
        await DriveClientAsync(client, clientStream, input, cancellationToken).ConfigureAwait(false);

        var options = new TelnetServerOptions
        {
            IdleTimeout = Timeout.InfiniteTimeSpan,
            StatusInterval = null,
            IsWriteConsole = false,
        };
        using var serverStream = new FuzzStream();
        using var session = new ServerSession(serverStream, options, CancellationToken.None)
        {
            MillisecondReadDelay = 1,
        };
        await DriveServerAsync(session, serverStream, input, cancellationToken).ConfigureAwait(false);

        var (first, firstHash) = clientStream.OutboundSignature();
        var (second, secondHash) = serverStream.OutboundSignature();
        return (first + second, unchecked((firstHash * 31) + secondHash));
    }

    private static void Refill(FuzzStream stream, FuzzInput input)
    {
        foreach (var (start, length) in Feed.Chunks(input))
        {
            stream.Enqueue(input.Bytes.AsSpan(start, length));
        }
    }

    private static string Terminator(FuzzInput input)
    {
        // Non-empty by construction: empty-terminator handling is a unit-test
        // concern with an ambiguous oracle, so the fuzzer stays away from it.
        var term = FuzzText.Latin1(input.Bytes, 0, Math.Min(input.Bytes.Length, 16), 16);
        return term.Length == 0 ? "\n" : term;
    }

    private static Regex? TryBuild(FuzzInput input)
    {
        try
        {
            return new Regex(FuzzText.Latin1(input.Bytes, 32), RegexOptions.None, TimeSpan.FromMilliseconds(20));
        }
        catch (ArgumentException)
        {
            // Hostile patterns are caller error; the fuzzer only probes
            // patterns the regex engine accepts.
            return null;
        }
    }

    private static async Task DriveClientAsync(Client client, FuzzStream stream, FuzzInput input, CancellationToken cancellationToken)
    {
        var term = Terminator(input);
        Refill(stream, input);
        try
        {
            _ = await client.TerminatedReadAsync(term, Slice, 1, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }

        Refill(stream, input);
        try
        {
            _ = await client.TerminatedReadAsync([term, "\n", "\r\n"], Slice, 1, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }

        var pattern = TryBuild(input);
        if (pattern is not null)
        {
            Refill(stream, input);
            try
            {
                _ = await client.TerminatedReadAsync(pattern, Slice, 1, cancellationToken).ConfigureAwait(false);
            }
            catch (RegexMatchTimeoutException)
            {
            }
            catch (TimeoutException)
            {
            }

            Refill(stream, input);
            try
            {
                _ = await client.TerminatedReadAsync([pattern], Slice, 1, cancellationToken).ConfigureAwait(false);
            }
            catch (RegexMatchTimeoutException)
            {
            }
            catch (TimeoutException)
            {
            }
        }

        Refill(stream, input);
        var count = input.Bytes.Length == 0 ? 1 : 1 + (input.Bytes[0] % 64);
        try
        {
            _ = await client.ReadExactlyAsync(count, Slice, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }
        catch (EndOfStreamException)
        {
        }

        try
        {
            // Zero counts are caller error (documented validation).
            _ = await client.ReadExactlyAsync(0, Slice, cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentOutOfRangeException)
        {
        }
    }

    private static async Task DriveServerAsync(ServerSession session, FuzzStream stream, FuzzInput input, CancellationToken cancellationToken)
    {
        var term = Terminator(input);
        Refill(stream, input);
        try
        {
            _ = await session.TerminatedReadAsync(term, Slice, 1, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }

        Refill(stream, input);
        try
        {
            _ = await session.TerminatedReadAsync([term, "\n"], Slice, 1, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }

        var pattern = TryBuild(input);
        if (pattern is not null)
        {
            Refill(stream, input);
            try
            {
                _ = await session.TerminatedReadAsync(pattern, Slice, 1, cancellationToken).ConfigureAwait(false);
            }
            catch (RegexMatchTimeoutException)
            {
            }
            catch (TimeoutException)
            {
            }

            Refill(stream, input);
            try
            {
                _ = await session.TerminatedReadAsync([pattern], Slice, 1, cancellationToken).ConfigureAwait(false);
            }
            catch (RegexMatchTimeoutException)
            {
            }
            catch (TimeoutException)
            {
            }
        }
    }
}
