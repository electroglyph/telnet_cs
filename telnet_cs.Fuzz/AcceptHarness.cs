namespace telnet_cs.Fuzz;

using System.Net;
using System.Net.Sockets;
using telnet_cs.Server;
using telnet_cs.Transport;

/// <summary>
/// Accept/transport target: drives the socket-free parts of the server
/// accept path (constructor validation, option-driven <c>Start</c>
/// validation, cancelled accepts, lifecycle, counters) plus the public
/// hermetic transport pair (<see cref="InMemoryPipe"/>): chunked writes in
/// both directions, timeout reads on an idle peer, close propagation, and
/// writes to a closed end. The live TCP accept exchange (real client
/// connects, TLS handshake, per-IP accounting) stays with the integration
/// tests: it needs real sockets and timing the fuzzer cannot afford.
/// Oracle: no unexpected throw, plus byte-exact pipe echo. Documented
/// validation (<see cref="ArgumentOutOfRangeException"/>), cancelled waits
/// (<see cref="OperationCanceledException"/>), idle-peer timeouts
/// (<see cref="IOException"/>), and writes to a closed end
/// (<see cref="ObjectDisposedException"/>) are consumed inline. A
/// <see cref="SocketException"/> while binding skips the socket-dependent
/// probes for that iteration (sandbox without loopback).
/// </summary>
internal static class AcceptHarness
{
    public static async Task<(long Outbound, long Hash)> RunAsync(FuzzInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        cancellationToken.ThrowIfCancellationRequested();
        var port = ProbeOptions(input);
        await ProbeLifecycleAsync(input, cancellationToken).ConfigureAwait(false);
        var (toB, toA) = await ProbePipeAsync(input, cancellationToken).ConfigureAwait(false);
        return (0L, FuzzSignature.ForLongs(port, toB, toA, input.Bytes.Length));
    }

    private static TelnetServerOptions BuildOptions(byte[] bytes) => new()
    {
        IdleTimeout = Timeout.InfiniteTimeSpan,
        StatusInterval = null,
        IsWriteConsole = false,
        RequestTerminalType = bytes.Length == 0 || bytes[0] % 2 == 0,
        DisableAllNegotiation = bytes.Length > 1 && bytes[1] % 2 == 0,
        OfferSuppressGoAhead = bytes.Length > 2 && bytes[2] % 2 == 0,
        OfferBinary = bytes.Length > 3 && bytes[3] % 2 == 0,
        RequestWindowSize = bytes.Length > 4 && bytes[4] % 2 == 0,
        HandshakeTimeout = TimeSpan.FromMilliseconds(bytes.Length > 5 ? bytes[5] % 100 : 0),
        MaxConcurrentSessions = bytes.Length > 6 ? bytes[6] % 16 : 0,
        MaxConnectionsPerIp = bytes.Length > 7 ? bytes[7] % 16 : 0,
    };

    private static int ProbeOptions(FuzzInput input)
    {
        var bytes = input.Bytes;
        var port = bytes.Length > 1 ? (bytes[0] << 8) | bytes[1] : 0;
        try
        {
            // Negative or >65535 ports are caller error.
            using var rejected = new TelnetServer(port - 70000, BuildOptions(bytes));
        }
        catch (ArgumentOutOfRangeException)
        {
        }

        using var server = new TelnetServer(0, BuildOptions(bytes));
        _ = server.Port;
        _ = server.Settings;
        _ = server.RejectedCapacityCount;
        _ = server.RejectedPerIpCount;
        _ = server.RejectedFilterCount;
        _ = server.QueueDroppedCount;
        return port;
    }

    private static async Task ProbeLifecycleAsync(FuzzInput input, CancellationToken cancellationToken)
    {
        using var server = new TelnetServer(0, BuildOptions(input.Bytes));
        try
        {
            server.Start();
        }
        catch (ArgumentOutOfRangeException)
        {
            // Negative caps are caller error (documented Start validation).
            return;
        }
        catch (SocketException)
        {
            return;
        }

        try
        {
            _ = server.Port;
            using var wait = new CancellationTokenSource(TimeSpan.FromMilliseconds(5));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, wait.Token);
            try
            {
                _ = await server.AcceptTcpAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            try
            {
                _ = await server.WaitForClientAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            try
            {
                _ = await server.AcceptSessionAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
        finally
        {
            server.Stop();
        }

        // Stopped servers re-arm (documented lifecycle); skip when the
        // sandbox refused the first bind above.
        try
        {
            server.Start();
        }
        catch (SocketException)
        {
            return;
        }
        finally
        {
            server.Stop();
        }
    }

    private static async Task<(int ToB, int ToA)> ProbePipeAsync(FuzzInput input, CancellationToken cancellationToken)
    {
        var (a, b) = InMemoryPipe.Create();
        using var endA = a;
        using var endB = b;
        endA.ReceiveTimeout = 5;
        endB.ReceiveTimeout = 5;
        var bytes = input.Bytes;
        var half = bytes.Length / 2;
        await endA.WriteAsync(bytes, 0, half, cancellationToken).ConfigureAwait(false);
        await endB.WriteAsync(bytes, half, bytes.Length - half, cancellationToken).ConfigureAwait(false);
        if (bytes.Length != 0)
        {
            await endA.WriteByteAsync(bytes[0], cancellationToken).ConfigureAwait(false);
        }

        var text = FuzzText.Latin1(bytes, 64);
        await endB.WriteAsync(text, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var toB = Drain(endB);
        if (bytes.Length != 0)
        {
            var expectedB = bytes[..half].Concat([bytes[0]]).ToArray();
            if (!toB.SequenceEqual(expectedB))
            {
                throw new InvalidDataException($"Pipe A->B mismatch: {half + 1} bytes written, {toB.Count} bytes read.");
            }
        }

        var toA = Drain(endA);
        // The string write crosses as Latin-1 with IAC doubling (the pipe's
        // documented mapping, mirroring the wire converter), so the
        // expectation truncates and doubles the same way.
        var expectedA = bytes[half..].Concat(EscapeLatin1(text)).ToArray();
        if (!toA.SequenceEqual(expectedA))
        {
            throw new InvalidDataException($"Pipe B->A mismatch: {expectedA.Length} bytes written, {toA.Count} bytes read.");
        }

        try
        {
            // Idle live peer: the documented receive-timeout expiry.
            _ = endA.ReadByte();
        }
        catch (IOException)
        {
        }

        endA.Close();
        while (endB.ReadByte() >= 0)
        {
        }

        try
        {
            // Writes to a closed end are caller error (documented).
            await endA.WriteByteAsync(1, cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
        }

        _ = endA.Connected;
        _ = endB.Available;
        endA.Dispose();
        endB.Dispose();
        return (toB.Count, toA.Count);
    }

    private static byte[] EscapeLatin1(string text)
    {
        var buf = new List<byte>(text.Length);
        foreach (var c in text)
        {
            buf.Add((byte)c);
            if ((byte)c == 255)
            {
                buf.Add(255);
            }
        }

        return [.. buf];
    }

    private static List<byte> Drain(IByteStream end)
    {
        var buf = new List<byte>();
        while (end.Available > 0)
        {
            var next = end.ReadByte();
            if (next < 0)
            {
                break;
            }

            buf.Add((byte)next);
        }

        return buf;
    }
}
