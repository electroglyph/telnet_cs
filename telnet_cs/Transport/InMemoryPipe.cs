namespace telnet_cs.Transport;

using telnet_cs.IO;

/// <summary>
/// Hermetic in-memory transport for tests and adapters: a linked pair of
/// <see cref="IByteStream"/> ends with real blocking semantics and no
/// sockets or ports. Bytes written on one end arrive on the other;
/// <c>Close</c> propagates end-of-stream to the peer. Loopback stays
/// reserved for what memory cannot prove (TCP accept lifecycle, TLS
/// handshake, urgent data, per-IP accounting).
/// </summary>
/// <example>
/// <code>
/// var (clientStream, serverStream) = InMemoryPipe.Create();
/// using var session = new ServerSession(serverStream, new TelnetServerOptions(), ct);
/// using var client = new Client.Client(clientStream, ct);
/// </code>
/// </example>
public static class InMemoryPipe
{
    /// <summary>
    /// Creates two linked ends. Bytes written on <c>A</c> are readable
    /// on <c>B</c> and vice versa, honoring the full <see
    /// cref="IByteStream"/> contract (blocking reads, <c>Available</c>,
    /// <c>ReceiveTimeout</c> expiry as <see
    /// cref="System.IO.IOException"/>, close propagation).
    /// </summary>
    /// <returns>The linked <c>(A, B)</c> ends.</returns>
    public static (IByteStream A, IByteStream B) Create()
    {
        var (a, b) = DuplexPipe.Create();
        return ((IByteStream)a, (IByteStream)b);
    }
}
