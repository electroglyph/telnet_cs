namespace telnet_cs.Fuzz;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using telnet_cs.Transport;

/// <summary>
/// In-memory <see cref="IByteStream"/> fed one fuzz input at a time.
/// Reads drain the queued bytes and return -1 when empty; writes are sunk.
/// Chunked feeds across successive reads exercise split-frame resume paths.
/// </summary>
internal sealed class FuzzStream : IByteStream
{
    private readonly Queue<int> reads = new();
    private bool connected = true;
    private bool disposed;

    public int Available
    {
        get
        {
            lock (reads)
            {
                return reads.Count;
            }
        }
    }

    public bool Connected
    {
        get
        {
            lock (reads)
            {
                return connected && !disposed;
            }
        }
    }

    public int ReceiveTimeout { get; set; }

    /// <summary>
    /// Queues inbound bytes for the next reads.
    /// </summary>
    public void Enqueue(ReadOnlySpan<byte> chunk)
    {
        lock (reads)
        {
            foreach (var b in chunk)
            {
                reads.Enqueue(b);
            }
        }
    }

    public int ReadByte()
    {
        lock (reads)
        {
            if (disposed || reads.Count == 0)
            {
                return -1;
            }

            return reads.Dequeue();
        }
    }

    public Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return Task.CompletedTask;
    }

    public Task WriteAsync(string value, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Task.CompletedTask;
    }

    public Task WriteByteAsync(byte value, CancellationToken cancellationToken) => Task.CompletedTask;

    public void Close()
    {
        lock (reads)
        {
            reads.Clear();
            connected = false;
        }
    }

    public void Dispose()
    {
        lock (reads)
        {
            disposed = true;
            reads.Clear();
            connected = false;
        }
    }
}
