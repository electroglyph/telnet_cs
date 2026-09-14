namespace telnet_cs.IO;

using System;
using System.IO;
using System.IO.Compression;

/// <summary>
/// Incremental zlib compressor for one outbound MCCP stream (MCCP2 on
/// servers, MCCP3 on clients). Each
/// <see cref="CompressChunk"/> call feeds plaintext through a persistent
/// <see cref="ZLibStream"/> (dictionary continuity across chunks, like the
/// reference compressor) and drains whatever the sync flush produced, so
/// every returned slice is immediately inflatable by the peer. The stream is
/// never finished mid-session (the reference never stops its compressor);
/// <see cref="Dispose"/> drops it, and the peer sees truncation.
/// Threading: calls must be externally serialized (the write filter holds
/// its send gate across compress-plus-write); a private lock guards the
/// spill against future callers.
/// </summary>
internal sealed class MccpCompressor : IDisposable
{
    private readonly MemoryStream spill = new();
    private readonly ZLibStream zlib;
    private bool disposed;

    /// <summary>
    /// Initialises a new compressor with an empty zlib stream.
    /// </summary>
    public MccpCompressor()
    {
        zlib = new ZLibStream(spill, CompressionLevel.Optimal, leaveOpen: true);
    }

    /// <summary>
    /// Compresses one plaintext slice and returns the wire bytes for it
    /// (possibly empty when nothing was fed or nothing flushed out yet).
    /// </summary>
    /// <param name="buffer">The plaintext source.</param>
    /// <param name="offset">The first byte to compress.</param>
    /// <param name="count">The number of bytes to compress.</param>
    public byte[] CompressChunk(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (offset + count > buffer.Length)
        {
            throw new ArgumentException("Offset plus count exceeds the buffer length.", nameof(count));
        }

        ObjectDisposedException.ThrowIf(disposed, this);

        if (count == 0)
        {
            return [];
        }

        lock (spill)
        {
            zlib.Write(buffer, offset, count);
            zlib.Flush();
            byte[] wire = spill.ToArray();
            spill.SetLength(0);
            return wire;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        zlib.Dispose();
        spill.Dispose();
    }
}
