namespace telnet_cs.IO;

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

/// <summary>
/// Raw-deflate stream-end locator: the resumable block walker (with its Huffman decoder) that proves stream end without guessing. Split from <see cref="MccpDecompressor"/>; wire behavior is unchanged.
/// </summary>
internal partial class MccpDecompressor
{
    /// <summary>
    /// Resumable raw-deflate (RFC 1951) end locator. Walks block boundaries
    /// from bit zero and commits progress symbol by symbol, so each probe
    /// replays at most one symbol and then advances over the newly arrived
    /// bytes only (amortized O(1) per feed — no per-byte reparse of the
    /// whole stream). A truncated read leaves the committed state
    /// untouched, so split deliveries simply wait for more input. End is
    /// reported only when a final block closes at an exact byte position
    /// and the decoded length matches the live inflater's output total,
    /// which proves the slice; the end position is returned so bytes the
    /// live inflater read ahead stay recoverable as trailing plaintext.
    /// Invalid data (reserved block type, oversubscribed codes, a match
    /// reaching past the decoded output) freezes the locator: the live
    /// inflate will throw on the same bytes and take the corrupt path, and
    /// a frozen locator can never manufacture an end.
    /// </summary>
    private sealed class RawEndLocator
    {
        private const int Dead = -1;
        private const int BlockHeader = 0;
        private const int StoredLength = 1;
        private const int StoredSkip = 2;
        private const int Decode = 3;
        private const int DynamicCounts = 4;
        private const int DynamicCodeLengths = 5;
        private const int DynamicLengths = 6;

        private static readonly int[] CodeLengthOrder =
            [16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15];
        private static readonly int[] LengthBases =
            [3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258];
        private static readonly int[] LengthExtras =
            [0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0];
        private static readonly int[] DistanceBases =
            [1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577];
        private static readonly int[] DistanceExtras =
            [0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13];

        private static readonly HuffmanDecoder FixedLiterals = BuildFixedLiterals();
        private static readonly HuffmanDecoder FixedDistances = BuildFixedDistances();

        private int phase;
        private long commitBitPos;
        private long outputLength;
        private int finalBlock;
        private long storedLeft;
        private HuffmanDecoder? litDecoder;
        private HuffmanDecoder? distDecoder;
        private int hlit;
        private int hdist;
        private int hclen;
        private readonly int[] codeLengthLens = new int[19];
        private int codeLengthsRead;
        private HuffmanDecoder? codeLengthDecoder;
        private List<int>? combined;
        private int[]? litLens;
        private int[]? distLens;

        /// <summary>
        /// Advances the walk over the first <paramref name="length"/> bytes
        /// of <paramref name="buffer"/>.
        /// </summary>
        /// <param name="buffer">The accumulated stream bytes.</param>
        /// <param name="length">How many bytes are available.</param>
        /// <param name="totalOut">The live inflater's output total (the proof).</param>
        /// <param name="endBytes">The exact stream end, when proven.</param>
        /// <returns>True when a final block closed and the length proved out.</returns>
        public bool TryAdvance(byte[] buffer, long length, long totalOut, out long endBytes)
        {
            endBytes = 0;
            if (phase == Dead)
            {
                return false;
            }

            var totalBits = length * 8;
            var cursor = commitBitPos;
            while (true)
            {
                switch (phase)
                {
                    case BlockHeader:
                        if (!Have(cursor, totalBits, 3))
                        {
                            return false;
                        }

                        TryTakeBits(buffer, ref cursor, 1, out finalBlock);
                        TryTakeBits(buffer, ref cursor, 2, out var blockType);
                        commitBitPos = cursor;
                        if (blockType == 0)
                        {
                            phase = StoredLength;
                        }
                        else if (blockType == 1)
                        {
                            litDecoder = FixedLiterals;
                            distDecoder = FixedDistances;
                            phase = Decode;
                        }
                        else if (blockType == 2)
                        {
                            phase = DynamicCounts;
                        }
                        else
                        {
                            phase = Dead;
                            return false;
                        }

                        break;
                    case StoredLength:
                        cursor = ((cursor + 7) / 8) * 8;
                        if (!Have(cursor, totalBits, 32))
                        {
                            return false;
                        }

                        TryTakeBits(buffer, ref cursor, 16, out var storedLen);
                        TryTakeBits(buffer, ref cursor, 16, out var storedNlen);
                        if ((storedLen ^ storedNlen) != 0xFFFF)
                        {
                            phase = Dead;
                            return false;
                        }

                        storedLeft = storedLen;
                        commitBitPos = cursor;
                        phase = StoredSkip;
                        break;
                    case StoredSkip:
                        {
                            var available = length - (commitBitPos / 8);
                            var take = Math.Min(storedLeft, available);
                            storedLeft -= take;
                            outputLength += take;
                            commitBitPos += take * 8;
                            cursor = commitBitPos;
                            if (storedLeft > 0)
                            {
                                return false;
                            }

                            if (BlockDone(totalOut, out endBytes))
                            {
                                return true;
                            }

                            break;
                        }

                    case Decode:
                        if (DecodeBlock(buffer, totalBits, ref cursor, totalOut, out endBytes))
                        {
                            return true;
                        }

                        // A non-final block may have closed mid-buffer: keep
                        // walking its successor header in this same probe
                        // instead of stalling a feed behind.
                        if (phase == BlockHeader)
                        {
                            break;
                        }

                        return false;
                    case DynamicCounts:
                        if (!Have(cursor, totalBits, 14))
                        {
                            return false;
                        }

                        TryTakeBits(buffer, ref cursor, 5, out hlit);
                        TryTakeBits(buffer, ref cursor, 5, out hdist);
                        TryTakeBits(buffer, ref cursor, 4, out hclen);
                        hlit += 257;
                        hdist += 1;
                        hclen += 4;
                        if (hlit > 288 || hdist > 32)
                        {
                            phase = Dead;
                            return false;
                        }

                        Array.Clear(codeLengthLens);
                        codeLengthsRead = 0;
                        codeLengthDecoder = null;
                        combined = new List<int>(hlit + hdist);
                        litLens = null;
                        distLens = null;
                        commitBitPos = cursor;
                        phase = DynamicCodeLengths;
                        break;
                    case DynamicCodeLengths:
                        while (codeLengthsRead < hclen)
                        {
                            if (!Have(cursor, totalBits, 3))
                            {
                                return false;
                            }

                            TryTakeBits(buffer, ref cursor, 3, out var codeLen);
                            codeLengthLens[CodeLengthOrder[codeLengthsRead]] = codeLen;
                            codeLengthsRead++;
                            commitBitPos = cursor;
                        }

                        if (!HuffmanDecoder.TryBuild(codeLengthLens, out codeLengthDecoder) ||
                            codeLengthDecoder is null)
                        {
                            phase = Dead;
                            return false;
                        }

                        phase = DynamicLengths;
                        break;
                    case DynamicLengths:
                        if (ReadDynamicLengths(buffer, totalBits, ref cursor))
                        {
                            phase = Decode;
                            break;
                        }

                        return false;
                    default:
                        phase = Dead;
                        return false;
                }
            }
        }

        /// <summary>
        /// Resets the walk to bit zero (a fresh stream or a raw retry).
        /// </summary>
        public void Reset()
        {
            phase = BlockHeader;
            commitBitPos = 0;
            outputLength = 0;
            finalBlock = 0;
            storedLeft = 0;
            litDecoder = null;
            distDecoder = null;
            hlit = 0;
            hdist = 0;
            hclen = 0;
            Array.Clear(codeLengthLens);
            codeLengthsRead = 0;
            codeLengthDecoder = null;
            combined = null;
            litLens = null;
            distLens = null;
        }

        private bool BlockDone(long totalOut, out long endBytes)
        {
            endBytes = 0;
            if (finalBlock != 1)
            {
                phase = BlockHeader;
                return false;
            }

            endBytes = (commitBitPos + 7) / 8;
            if (outputLength == totalOut)
            {
                return true;
            }

            phase = Dead;
            return false;
        }

        private bool DecodeBlock(byte[] buffer, long totalBits, ref long cursor, long totalOut, out long endBytes)
        {
            endBytes = 0;
            var lit = litDecoder;
            var dist = distDecoder;
            if (lit is null || dist is null)
            {
                phase = Dead;
                return false;
            }

            while (true)
            {
                if (!lit.TryDecode(buffer, totalBits, ref cursor, out var symbol))
                {
                    return false;
                }

                if (symbol < 256)
                {
                    outputLength++;
                    commitBitPos = cursor;
                    continue;
                }

                if (symbol == 256)
                {
                    commitBitPos = cursor;
                    return BlockDone(totalOut, out endBytes);
                }

                if (symbol > 285)
                {
                    phase = Dead;
                    return false;
                }

                var extra = LengthExtras[symbol - 257];
                if (!Have(cursor, totalBits, extra))
                {
                    return false;
                }

                TryTakeBits(buffer, ref cursor, extra, out var lengthExtra);
                var length = LengthBases[symbol - 257] + lengthExtra;
                if (!dist.TryDecode(buffer, totalBits, ref cursor, out var distSymbol) || distSymbol > 29)
                {
                    if (distSymbol > 29)
                    {
                        phase = Dead;
                    }

                    return false;
                }

                var distExtra = DistanceExtras[distSymbol];
                if (!Have(cursor, totalBits, distExtra))
                {
                    return false;
                }

                TryTakeBits(buffer, ref cursor, distExtra, out var distValue);
                var distance = DistanceBases[distSymbol] + distValue;
                if (distance < 1 || distance > outputLength)
                {
                    phase = Dead;
                    return false;
                }

                outputLength += length;
                commitBitPos = cursor;
            }
        }

        private bool ReadDynamicLengths(byte[] buffer, long totalBits, ref long cursor)
        {
            var cl = codeLengthDecoder;
            var lens = combined;
            if (cl is null || lens is null)
            {
                phase = Dead;
                return false;
            }

            while (lens.Count < hlit + hdist)
            {
                if (!cl.TryDecode(buffer, totalBits, ref cursor, out var symbol))
                {
                    return false;
                }

                if (symbol < 16)
                {
                    lens.Add(symbol);
                }
                else if (symbol == 16)
                {
                    if (lens.Count == 0 || !Have(cursor, totalBits, 2))
                    {
                        if (lens.Count == 0)
                        {
                            phase = Dead;
                        }

                        return false;
                    }

                    TryTakeBits(buffer, ref cursor, 2, out var repeat);
                    var prev = lens[^1];
                    for (var i = 0; i < 3 + repeat; i++)
                    {
                        lens.Add(prev);
                    }
                }
                else if (symbol == 17)
                {
                    if (!Have(cursor, totalBits, 3))
                    {
                        return false;
                    }

                    TryTakeBits(buffer, ref cursor, 3, out var repeat);
                    for (var i = 0; i < 3 + repeat; i++)
                    {
                        lens.Add(0);
                    }
                }
                else if (symbol == 18)
                {
                    if (!Have(cursor, totalBits, 7))
                    {
                        return false;
                    }

                    TryTakeBits(buffer, ref cursor, 7, out var repeat);
                    for (var i = 0; i < 11 + repeat; i++)
                    {
                        lens.Add(0);
                    }
                }
                else
                {
                    phase = Dead;
                    return false;
                }

                if (lens.Count > hlit + hdist)
                {
                    phase = Dead;
                    return false;
                }

                commitBitPos = cursor;
            }

            litLens = [.. lens.Take(hlit)];
            distLens = [.. lens.Skip(hlit)];
            if (litLens[256] == 0 ||
                !HuffmanDecoder.TryBuild(litLens, out litDecoder) || litDecoder is null ||
                !HuffmanDecoder.TryBuild(distLens, out distDecoder) || distDecoder is null)
            {
                phase = Dead;
                return false;
            }

            return true;
        }

        private static bool Have(long cursor, long totalBits, int need)
        {
            return cursor + need <= totalBits;
        }

        private static void TryTakeBits(byte[] buffer, ref long cursor, int count, out int value)
        {
            var result = 0;
            for (var i = 0; i < count; i++)
            {
                result |= (((buffer[cursor / 8] >> (int)(cursor % 8)) & 1) << i);
                cursor++;
            }

            value = result;
        }

        private static HuffmanDecoder BuildFixedLiterals()
        {
            var lens = new int[288];
            for (var i = 0; i <= 143; i++)
            {
                lens[i] = 8;
            }

            for (var i = 144; i <= 255; i++)
            {
                lens[i] = 9;
            }

            for (var i = 256; i <= 279; i++)
            {
                lens[i] = 7;
            }

            for (var i = 280; i <= 287; i++)
            {
                lens[i] = 8;
            }

            if (!HuffmanDecoder.TryBuild(lens, out var decoder) || decoder is null)
            {
                throw new InvalidOperationException("Fixed literal/length code lengths are not a valid Huffman set.");
            }

            return decoder;
        }

        private static HuffmanDecoder BuildFixedDistances()
        {
            var lens = new int[30];
            Array.Fill(lens, 5);
            if (!HuffmanDecoder.TryBuild(lens, out var decoder) || decoder is null)
            {
                throw new InvalidOperationException("Fixed distance code lengths are not a valid Huffman set.");
            }

            return decoder;
        }

        private sealed class HuffmanDecoder
        {
            private readonly Dictionary<int, int> codes = new();
            private readonly int maxLength;

            private HuffmanDecoder(int maxLength)
            {
                this.maxLength = maxLength;
            }

            public static bool TryBuild(int[] lengths, out HuffmanDecoder? decoder)
            {
                decoder = null;
                var longest = 0;
                foreach (var length in lengths)
                {
                    if (length > longest)
                    {
                        longest = length;
                    }
                }

                if (longest is < 1 or > 15)
                {
                    return false;
                }

                var result = new HuffmanDecoder(longest);
                var counts = new int[16];
                foreach (var length in lengths)
                {
                    if (length is > 0 and < 16)
                    {
                        counts[length]++;
                    }
                }

                var nextCode = new int[16];
                var code = 0;
                for (var bits = 1; bits <= 15; bits++)
                {
                    code = ((code + counts[bits - 1]) << 1);
                    nextCode[bits] = code;
                }

                for (var n = 0; n < lengths.Length; n++)
                {
                    var length = lengths[n];
                    if (length == 0)
                    {
                        continue;
                    }

                    var assigned = nextCode[length]++;
                    if (assigned >= 1 << length)
                    {
                        return false;
                    }

                    if (!result.codes.TryAdd((length << 16) | assigned, n))
                    {
                        return false;
                    }
                }

                decoder = result;
                return true;
            }

            public bool TryDecode(byte[] buffer, long totalBits, ref long cursor, out int symbol)
            {
                symbol = 0;
                var code = 0;
                for (var length = 1; length <= maxLength; length++)
                {
                    if (!Have(cursor, totalBits, 1))
                    {
                        return false;
                    }

                    TryTakeBits(buffer, ref cursor, 1, out var bit);
                    code = ((code << 1) | bit);
                    if (codes.TryGetValue((length << 16) | code, out symbol))
                    {
                        return true;
                    }
                }

                return false;
            }
        }
    }
}
