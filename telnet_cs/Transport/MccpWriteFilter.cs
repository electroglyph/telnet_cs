namespace telnet_cs.Transport;

using System;
using System.Threading;
using System.Threading.Tasks;
using telnet_cs.IO;

/// <summary>
/// Compressing outbound view over a session byte stream (MCCP2, option 86,
/// on servers; MCCP3, option 87, on clients). Installed once compression is
/// agreed and the SB start marker went out raw: every later write
/// (application text and negotiation frames alike) is compressed before
/// hitting the inner stream. Reads, connection state and <see cref="Close"/>
/// pass through untouched; the session keeps owning (and closing) the inner
/// stream, so <see cref="Dispose"/> releases only the compressor and the
/// send gate. One send gate serializes each compress-plus-write as a unit,
/// so concurrent session and handler writes cannot interleave chunks.
/// Strict: the view runs to disconnect — the reference never stops its
/// outbound compressor, not even on WONT/DONT — so there is no finish path;
/// <see cref="Dispose"/> drops the compressor mid-stream (the peer sees
/// truncation, exactly as against the reference closing the socket).
/// </summary>
internal sealed class MccpWriteFilter : IByteStream
{
    private readonly IByteStream inner;
    private readonly MccpCompressor compressor;
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private bool disposed;

    /// <summary>
    /// Initialises a compressing view over <paramref name="inner"/>.
    /// </summary>
    /// <param name="inner">The raw session stream (kept owned by the session).</param>
    /// <param name="compressor">The session-owned MCCP compressor.</param>
    public MccpWriteFilter(IByteStream inner, MccpCompressor compressor)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(compressor);
        this.inner = inner;
        this.compressor = compressor;
    }

    /// <inheritdoc/>
    public int Available => inner.Available;

    /// <inheritdoc/>
    public bool Connected => inner.Connected;

    /// <inheritdoc/>
    public int ReceiveTimeout
    {
        get => inner.ReceiveTimeout;
        set => inner.ReceiveTimeout = value;
    }

    /// <inheritdoc/>
    public void Close() => inner.Close();

    /// <inheritdoc/>
    public int ReadByte() => inner.ReadByte();

    /// <inheritdoc/>
    public Task WriteByteAsync(byte value, CancellationToken cancellationToken) =>
        WriteAsync([value], 0, 1, cancellationToken);

    /// <inheritdoc/>
    public async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ObjectDisposedException.ThrowIf(disposed, this);
        await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            byte[] wire = compressor.CompressChunk(buffer, offset, count);
            if (wire.Length != 0)
            {
                await inner.WriteAsync(wire, 0, wire.Length, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            writeGate.Release();
        }
    }

    /// <inheritdoc/>
    public Task WriteAsync(string value, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(value);
        // The session stream never carries a text encoding of its own (the
        // session pre-encodes, or sends Latin-1 with IAC doubling): match
        // that default spelling exactly, then compress the bytes.
        byte[] encoded = ByteStringConverter.ConvertStringToByteArray(value, null);
        return WriteAsync(encoded, 0, encoded.Length, cancellationToken);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        compressor.Dispose();
        writeGate.Dispose();
    }
}
