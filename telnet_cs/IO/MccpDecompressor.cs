namespace telnet_cs.IO;

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

/// <summary>
/// Incremental zlib/gzip/raw-deflate decompressor for one inbound MCCP
/// stream. Wire framing follows <c>docs/mud-protocols/mccp.md</c>: raw
/// DEFLATE bytes follow <c>IAC SB 86/87 IAC SE</c> until Z_FINISH, after
/// which the stream ends and the wire resumes plaintext (any bytes fed past
/// the footer surface as trailing plaintext).
/// Format sniffing mirrors the reference autodetect: a leading
/// <c>0x78</c> byte selects RFC 1950 zlib, <c>0x1F 0x8B</c> selects gzip,
/// anything else is raw deflate — and a zlib-sniffed stream that fails to
/// inflate is retried as raw before being called corrupt (the reference
/// tries zlib first, then raw), since a raw stream may start with
/// <c>0x78</c>. gzip and raw carry no
/// reference-equivalent end signal here: gzip ends at its CRC32/ISIZE
/// footer, while raw deflate has no footer and therefore never
/// end-detects (only a corrupt read or a fresh SB re-arms the stream).
/// Stream end is confirmed, never guessed: when the inflater stalls with
/// all input consumed, the footer bytes are prechecked against running
/// checksums (Adler32 for zlib, CRC32/ISIZE for gzip) and only then is the
/// whole slice re-inflated from byte zero to prove the end is exact.
/// A stall with a footer mismatch simply waits for more input, so split
/// deliveries never read as stream end. A live inflate failure marks
/// <see cref="Failed"/> (the caller answers DONT and resumes plaintext,
/// matching the reference corrupt path); fed bytes are never handed to the
/// reader.
/// </summary>
internal sealed class MccpDecompressor : IDisposable
{
    private readonly MemoryStream input = new();
    private readonly Queue<byte> ready = new();
    private readonly Queue<byte> trailing = new();
    private readonly byte[] scratch = new byte[4096];

    private Stream? inflater;
    private bool sniffed;
    private bool isZlib;
    private bool isGzip;
    private bool rawRetried;
    private long consumed;
    private long totalOut;
    private uint runningCheck = 1;
    private bool disposed;

    /// <summary>
    /// Gets whether decompressed output is queued for reading.
    /// </summary>
    public bool HasOutput => ready.Count > 0;

    /// <summary>
    /// Gets whether post-stream plaintext is queued for reading.
    /// </summary>
    public bool HasTrailing => trailing.Count > 0;

    /// <summary>
    /// Gets whether the stream ended cleanly at a proven footer.
    /// </summary>
    public bool StreamEnded { get; private set; }

    /// <summary>
    /// Gets whether the stream hit corrupt data.
    /// </summary>
    public bool Failed { get; private set; }

    /// <summary>
    /// Gets whether the stream is still decompressing (neither ended nor failed).
    /// </summary>
    public bool IsActive => !StreamEnded && !Failed;

    /// <summary>
    /// Feeds one wire byte: decompressed output queues in
    /// <see cref="HasOutput"/>, post-footer bytes queue in
    /// <see cref="HasTrailing"/>. No-ops once <see cref="Failed"/>.
    /// </summary>
    /// <param name="value">The wire byte.</param>
    public void Feed(byte value)
    {
        if (Failed || disposed)
        {
            return;
        }

        if (StreamEnded)
        {
            trailing.Enqueue(value);
            return;
        }

        input.Position = input.Length;
        input.WriteByte(value);
        if (!sniffed)
        {
            Sniff();
        }

        Pump();
    }

    /// <summary>
    /// Takes one queued decompressed byte.
    /// </summary>
    /// <param name="value">The byte, when present.</param>
    /// <returns>True when a byte was queued.</returns>
    public bool TryTakeReady(out byte value)
    {
        if (ready.Count > 0)
        {
            value = ready.Dequeue();
            return true;
        }

        value = 0;
        return false;
    }

    /// <summary>
    /// Takes one queued post-stream plaintext byte.
    /// </summary>
    /// <param name="value">The byte, when present.</param>
    /// <returns>True when a byte was queued.</returns>
    public bool TryTakeTrailing(out byte value)
    {
        if (trailing.Count > 0)
        {
            value = trailing.Dequeue();
            return true;
        }

        value = 0;
        return false;
    }

    /// <summary>
    /// Releases the inflater and buffers.
    /// </summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        inflater?.Dispose();
        input.Dispose();
    }

    private void Sniff()
    {
        if (input.Length == 0)
        {
            return;
        }

        var first = input.GetBuffer()[0];
        if (first == 0x78)
        {
            // RFC 1950 CMF: compression method 8 (deflate), any window size.
            isZlib = true;
            sniffed = true;
        }
        else if (first != 0x1F)
        {
            sniffed = true;
        }
        else if (input.Length >= 2)
        {
            isGzip = input.GetBuffer()[1] == 0x8B;
            sniffed = true;
        }

        if (sniffed)
        {
            inflater = isZlib
              ? new ZLibStream(input, CompressionMode.Decompress, leaveOpen: true)
              : isGzip
              ? new GZipStream(input, CompressionMode.Decompress, leaveOpen: true)
              : new DeflateStream(input, CompressionMode.Decompress, leaveOpen: true);
            runningCheck = isGzip ? 0xFFFF_FFFFu : 1u;
        }
    }

    private void Pump()
    {
        if (inflater is null || StreamEnded || Failed)
        {
            return;
        }

        try
        {
            while (true)
            {
                input.Position = consumed;
                var read = inflater.Read(scratch, 0, scratch.Length);
                consumed = input.Position;
                for (var i = 0; i < read; i++)
                {
                    ready.Enqueue(scratch[i]);
                }

                if (read > 0)
                {
                    totalOut += read;
                    runningCheck = isGzip
                      ? CrcUpdate(runningCheck, new ReadOnlySpan<byte>(scratch, 0, read))
                      : AdlerUpdate(runningCheck, new ReadOnlySpan<byte>(scratch, 0, read));
                    continue;
                }

                // A zero read with all input consumed and progress made is
                // either stream end or a stall for more input; the footer
                // probe tells them apart (O(1) precheck first, full
                // re-inflate only on a checksum hit).
                if (consumed == input.Length && consumed > 0 && TryConfirmEnd())
                {
                    StreamEnded = true;
                    DrainTrailing();
                }

                return;
            }
        }
        catch (InvalidDataException)
        {
            if (isZlib && !rawRetried)
            {
                // The reference inflates zlib-first and retries raw deflate
                // on failure: a raw stream that happens to start with 0x78
                // mis-sniffs as zlib, so rewind and retry raw before calling
                // the stream corrupt. Partial zlib output is discarded — the
                // raw pass re-inflates the whole slice from byte zero.
                rawRetried = true;
                isZlib = false;
                inflater?.Dispose();
                inflater = new DeflateStream(input, CompressionMode.Decompress, leaveOpen: true);
                consumed = 0;
                totalOut = 0;
                runningCheck = 1u;
                ready.Clear();
                Pump();
                return;
            }

            // Corrupt compressed data: drop everything queued (the reference
            // feeds the reader nothing) and let the caller DONT + resume raw.
            Failed = true;
            ready.Clear();
        }
    }

    private void DrainTrailing()
    {
        var buffer = input.GetBuffer();
        for (var i = consumed; i < input.Length; i++)
        {
            trailing.Enqueue(buffer[i]);
        }
    }

    private bool TryConfirmEnd()
    {
        if (isZlib)
        {
            // Smallest valid zlib stream (empty payload) is 8 bytes.
            if (consumed < 8)
            {
                return false;
            }

            var buffer = input.GetBuffer();
            var footer = (uint)(buffer[consumed - 4] << 24 | buffer[consumed - 3] << 16 | buffer[consumed - 2] << 8 | buffer[consumed - 1]);
            if (footer != runningCheck)
            {
                return false;
            }

            return ReInflateMatches(ms => new ZLibStream(ms, CompressionMode.Decompress, leaveOpen: true));
        }

        if (isGzip)
        {
            // 10-byte header + minimal body + 8-byte footer.
            if (consumed < 20)
            {
                return false;
            }

            var buffer = input.GetBuffer();
            var crc = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(buffer, (int)consumed - 8, 4));
            var size = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(buffer, (int)consumed - 4, 4));
            if (crc != (runningCheck ^ 0xFFFF_FFFFu) || size != (uint)(totalOut & 0xFFFF_FFFF))
            {
                return false;
            }

            return ReInflateMatches(ms => new GZipStream(ms, CompressionMode.Decompress, leaveOpen: true));
        }

        // Raw deflate has no footer: confirm end by parsing stored blocks for a
        // final-block bit at a block boundary. Huffman raw streams stay
        // never-ending here.
        var rawBuffer = input.GetBuffer();
        return TryConfirmRawStoredEnd(rawBuffer, consumed);
    }

    private static bool TryConfirmRawStoredEnd(byte[] buffer, long length)
    {
        var bitPos = 0L;
        var totalBits = length * 8;
        while (true)
        {
            if (bitPos + 3 > totalBits)
            {
                return false;
            }

            var bfinal = (buffer[bitPos / 8] >> (int)(bitPos % 8)) & 1;
            var btype = (buffer[bitPos / 8] >> (int)(bitPos % 8) >> 1) & 3;
            // Handle split across byte boundary for 3-bit header.
            if (bitPos % 8 > 5)
            {
                var next = buffer[bitPos / 8 + 1];
                var combined = buffer[bitPos / 8] | (next << 8);
                var shift = (int)(bitPos % 8);
                bfinal = (combined >> shift) & 1;
                btype = (combined >> (shift + 1)) & 3;
            }

            bitPos += 3;
            if (btype != 0)
            {
                return false;
            }

            bitPos = ((bitPos + 7) / 8) * 8;
            if (bitPos + 32 > totalBits)
            {
                return false;
            }

            var pos = bitPos / 8;
            var len = buffer[pos] | (buffer[pos + 1] << 8);
            var nlen = buffer[pos + 2] | (buffer[pos + 3] << 8);
            if ((len ^ nlen) != 0xFFFF)
            {
                return false;
            }

            bitPos += 32 + (long)len * 8;
            if (bitPos > totalBits)
            {
                return false;
            }

            if (bfinal == 1)
            {
                return bitPos == totalBits;
            }
        }
    }

    private bool ReInflateMatches(Func<MemoryStream, Stream> factory)
    {
        // Prove the slice [0, consumed) is exactly one complete stream:
        // byte-identical output length plus a matching checksum. (The
        // slice end is not position-checked: the inflater may read ahead
        // past the footer, so only output identity proves the end.)
        try
        {
            using var slice = new MemoryStream(input.GetBuffer(), 0, (int)consumed, writable: false);
            using var check = factory(slice);
            var expected = isGzip ? 0xFFFF_FFFFu : 1u;
            long total = 0;
            int read;
            while ((read = check.Read(scratch, 0, scratch.Length)) > 0)
            {
                total += read;
                expected = isGzip
                  ? CrcUpdate(expected, new ReadOnlySpan<byte>(scratch, 0, read))
                  : AdlerUpdate(expected, new ReadOnlySpan<byte>(scratch, 0, read));
            }

            return total == totalOut && expected == runningCheck;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static uint AdlerUpdate(uint adler, ReadOnlySpan<byte> data)
    {
        var s1 = adler & 0xFFFFu;
        var s2 = adler >> 16;
        foreach (var b in data)
        {
            s1 = (s1 + b) % 65521;
            s2 = (s2 + s1) % 65521;
        }

        return s2 << 16 | s1;
    }

    private static uint CrcUpdate(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var entry = i;
            for (var bit = 0; bit < 8; bit++)
            {
                entry = (entry & 1) != 0 ? 0xEDB8_8320u ^ (entry >> 1) : entry >> 1;
            }

            table[i] = entry;
        }

        return table;
    }
}
