namespace telnet_cs.Transport;

using System;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// An implementation of a network stream to read from and write to.
/// </summary>
/// <remarks>
/// The name deliberately mirrors <see cref="System.Net.Sockets.NetworkStream"/>:
/// always reference it as <c>telnet_cs.Transport.NetworkStream</c> (or alias
/// it) when <c>System.Net.Sockets</c> is also in scope. Renaming would break
/// the public surface, so the collision is documented instead.
/// </remarks>
public class NetworkStream : INetworkStream
{
    private readonly System.IO.Stream stream;

    /// <summary>
    /// Initialises a new instance of the <see cref="NetworkStream" /> class.
    /// </summary>
    /// <param name="stream">The stream.</param>
    public NetworkStream(System.IO.Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        this.stream = stream;
    }

    /// <summary>
    /// Reads the next byte.
    /// </summary>
    /// <returns>
    /// The next byte read.
    /// </returns>
    public int ReadByte()
    {
        return stream.ReadByte();
    }

    /// <inheritdoc/>
    public Task WriteByteAsync(byte value, CancellationToken cancellationToken)
    {
        return stream.WriteAsync([value], 0, 1, cancellationToken);
    }

    /// <inheritdoc/>
    public Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return stream.WriteAsync(buffer, offset, count, cancellationToken);
    }

    /// <summary>
    /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
    /// </summary>
    /// <param name="disposing"><c>true</c> to release both managed and unmanaged resources; <c>false</c> to release only unmanaged resources.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            stream.Dispose();
        }
    }
}
