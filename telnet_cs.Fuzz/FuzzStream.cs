namespace telnet_cs.Fuzz;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using telnet_cs.Transport;

/// <summary>
/// In-memory <see cref="IByteStream"/> fed one fuzz input at a time.
/// Reads drain the queued bytes and return -1 when empty; writes are sunk
/// while feeding the outbound accounting (byte count plus an FNV-1a hash)
/// that backs the novelty tracker and the reply-amplification oracle.
/// Chunked feeds across successive reads exercise split-frame resume paths.
/// </summary>
internal sealed class FuzzStream : IByteStream
{
    private readonly Queue<int> reads = new();
    private bool connected = true;
    private bool disposed;
    private long written;
    private long hash = unchecked((long)1469598103934665603UL);

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
    /// Total sunk outbound bytes plus their FNV-1a hash, for the novelty
    /// tracker and the amplification oracle.
    /// </summary>
    public (long Count, long Hash) OutboundSignature()
    {
        lock (reads)
        {
            return (written, hash);
        }
    }

    private void NoteWritten(ReadOnlySpan<byte> chunk)
    {
        lock (reads)
        {
            written += chunk.Length;
            foreach (var b in chunk)
            {
                hash ^= b;
                hash *= unchecked((long)1099511628211UL);
            }
        }
    }

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
        NoteWritten(new ReadOnlySpan<byte>(buffer, offset, count));
        return Task.CompletedTask;
    }

    public Task WriteAsync(string value, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(value);
        NoteWritten(System.Text.Encoding.ASCII.GetBytes(value));
        return Task.CompletedTask;
    }

    public Task WriteByteAsync(byte value, CancellationToken cancellationToken)
    {
        NoteWritten([value]);
        return Task.CompletedTask;
    }

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
