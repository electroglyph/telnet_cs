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

    public FuzzStream()
    {
        // Picks up ambient --faults arming for this iteration's async flow
        // (one stream per harness run consumes at most one fault).
        ThrowOnRead = FuzzFaults.TakeRead();
        ThrowOnWrite = FuzzFaults.TakeWrite();
    }

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
                // EofOnDrain turns an empty queue into a disconnect: the
                // REPL harness feeds everything up front, and the prompt loop
                // only exits on disconnect or cancellation. Without this the
                // loop would idle until the hang guard fires every iteration.
                // Writes keep sinking after EOF (close notifications still go out).
                return connected && !disposed && !(EofOnDrain && reads.Count == 0);
            }
        }
    }

    /// <summary>
    /// When true, <see cref="Connected"/> reads false once the queued bytes
    /// are drained, modelling a peer that sent its lines and hung up.
    /// </summary>
    public bool EofOnDrain { get; set; }

    public int ReceiveTimeout { get; set; }

    /// <summary>
    /// One-shot read fault: the next <see cref="ReadByte"/> throws
    /// <see cref="IOException"/> (dead-peer shape) then resets to false.
    /// Set from input bits by harnesses exercising stashed-error paths.
    /// </summary>
    public bool ThrowOnRead { get; set; }

    /// <summary>
    /// One-shot write fault: the next write throws <see cref="IOException"/>
    /// then resets to false. Exercises write-failure paths deterministically.
    /// </summary>
    public bool ThrowOnWrite { get; set; }

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
            if (ThrowOnRead)
            {
                ThrowOnRead = false;
                throw new System.IO.IOException("Injected fuzz read fault.");
            }

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
        lock (reads)
        {
            if (ThrowOnWrite)
            {
                ThrowOnWrite = false;
                throw new System.IO.IOException("Injected fuzz write fault.");
            }
        }

        NoteWritten(new ReadOnlySpan<byte>(buffer, offset, count));
        return Task.CompletedTask;
    }

    public Task WriteAsync(string value, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(value);
        lock (reads)
        {
            if (ThrowOnWrite)
            {
                ThrowOnWrite = false;
                throw new System.IO.IOException("Injected fuzz write fault.");
            }
        }

        NoteWritten(System.Text.Encoding.ASCII.GetBytes(value));
        return Task.CompletedTask;
    }

    public Task WriteByteAsync(byte value, CancellationToken cancellationToken)
    {
        lock (reads)
        {
            if (ThrowOnWrite)
            {
                ThrowOnWrite = false;
                throw new System.IO.IOException("Injected fuzz write fault.");
            }
        }

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
