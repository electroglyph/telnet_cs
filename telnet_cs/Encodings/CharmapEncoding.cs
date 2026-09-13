namespace telnet_cs.Encodings
{
    using System;
    using System.Collections.Generic;
    using System.Text;

    /// <summary>
    /// Base class for the single-byte retro-computer charmaps (ATASCII,
    /// PETSCII, Atari ST). Decoding is a 256-entry table lookup; encoding is
    /// the inverse lookup with Python <c>charmap_build</c> semantics (later
    /// table entries win), optionally biased toward the 0x00-0x7F range.
    /// Unmappable characters go through the configured
    /// <see cref="Encoding.EncoderFallback"/>: the default is
    /// <see cref="EncoderFallback.ExceptionFallback"/> (the equivalent of
    /// Python <c>errors="strict"</c>); a
    /// <see cref="EncoderReplacementFallback"/> with <c>"?"</c> gives
    /// <c>errors="replace"</c>, one with <c>""</c> gives
    /// <c>errors="ignore"</c>.
    /// </summary>
    public abstract class CharmapEncoding : Encoding
    {
        private readonly string[] decodeTable;
        private readonly Dictionary<string, byte> encodeTable;

        /// <summary>
        /// Initializes a new instance with the given 256-entry decode table.
        /// </summary>
        /// <param name="name">The primary codec name (e.g. "atascii").</param>
        /// <param name="decode">Exactly 256 decode entries, one per byte value.</param>
        /// <param name="preferLowRange">
        /// When true, entries from bytes 0x00-0x7F win over duplicate glyphs in
        /// 0x80-0xFF on encode (ATASCII inverse-video rule). When false, later
        /// entries win (plain <c>charmap_build</c> rule).
        /// </param>
        protected CharmapEncoding(string name, string[] decode, bool preferLowRange)
        {
            ArgumentException.ThrowIfNullOrEmpty(name);
            ArgumentNullException.ThrowIfNull(decode);
            if (decode.Length != 256)
            {
                throw new ArgumentException("Decode table must have exactly 256 entries.", nameof(decode));
            }

            WebNameValue = name;
            decodeTable = decode;
            encodeTable = new Dictionary<string, byte>(256, StringComparer.Ordinal);
            for (var i = 0; i < 256; i++)
            {
                encodeTable[decode[i]] = (byte)i;
            }

            if (preferLowRange)
            {
                for (var i = 0; i < 128; i++)
                {
                    encodeTable[decode[i]] = (byte)i;
                }
            }

            ApplyEncodeOverrides(encodeTable);
        }

        /// <summary>
        /// Gets the primary codec name.
        /// </summary>
        public override string WebName => WebNameValue;

        /// <summary>
        /// Gets the primary codec name.
        /// </summary>
        public override string BodyName => WebNameValue;

        /// <summary>
        /// Gets the primary codec name.
        /// </summary>
        public override string HeaderName => WebNameValue;

        private string WebNameValue { get; }

        private EncoderFallback? encoderFallbackOverride;
        private DecoderFallback? decoderFallbackOverride;

        /// <summary>
        /// Gets or sets the encoder fallback. Defaults to
        /// <see cref="EncoderFallback.ExceptionFallback"/> (strict). This
        /// shadows the base property (which is not virtual and whose setter
        /// throws while the instance is read-only — all fresh instances are):
        /// direct construction and subclasses read strict here, while
        /// <see cref="TelnetEncodingProvider"/> installs requested fallbacks
        /// through this same property. Clones carry the setting via memberwise
        /// copy.
        /// </summary>
        public new EncoderFallback EncoderFallback
        {
            get => encoderFallbackOverride ?? EncoderFallback.ExceptionFallback;
            set => encoderFallbackOverride = value;
        }

        /// <summary>
        /// Gets or sets the decoder fallback (defaults to strict; decoding is
        /// total — every byte maps — so this never fires).
        /// </summary>
        public new DecoderFallback DecoderFallback
        {
            get => decoderFallbackOverride ?? DecoderFallback.ExceptionFallback;
            set => decoderFallbackOverride = value;
        }

        /// <inheritdoc/>
        public override Encoder GetEncoder()
        {
            return new CharmapEncoder(this);
        }

        /// <summary>
        /// Adjusts the built encode table (e.g. ATASCII maps LF to 0x9B).
        /// </summary>
        /// <param name="table">The encode table to adjust.</param>
        protected virtual void ApplyEncodeOverrides(Dictionary<string, byte> table)
        {
        }

        /// <summary>
        /// Encodes one scalar through the configured
        /// <see cref="Encoding.EncoderFallback"/> (used when the scalar has
        /// no table mapping). With <see cref="EncoderFallback.ExceptionFallback"/>
        /// this throws <see cref="EncoderFallbackException"/>; a replacement
        /// fallback emits its substitute text (each character encoded through
        /// the same table, so an unmappable substitute still throws rather
        /// than recursing).
        /// </summary>
        /// <param name="chars">The source buffer (for fallback context).</param>
        /// <param name="index">Index of the scalar's first char in <paramref name="chars"/>.</param>
        /// <param name="charLength">1, or 2 for a surrogate pair.</param>
        /// <param name="bytes">The destination, or null to only count.</param>
        /// <param name="byteIndex">Where to write in <paramref name="bytes"/>.</param>
        /// <returns>The number of bytes written (or that would be written).</returns>
        internal int EncodeWithFallback(char[] chars, int index, int charLength, byte[]? bytes, int byteIndex)
        {
            var fallback = EncoderFallback;
            var buffer = fallback.CreateFallbackBuffer();
            if (charLength == 2 && fallback is EncoderReplacementFallback)
            {
                // A surrogate pair is one character: offer it once so a
                // replacement emits a single substitute (Python
                // errors="replace" semantics), not one per surrogate as the
                // two-char Fallback overload would drain.
                buffer.Fallback(chars[index], index);
            }
            else if (charLength == 2)
            {
                buffer.Fallback(chars[index], chars[index + 1], index);
            }
            else
            {
                buffer.Fallback(chars[index], index);
            }

            var written = 0;
            char next;
            while ((next = buffer.GetNextChar()) != '\0')
            {
                if (!encodeTable.TryGetValue(next.ToString(), out var mapped))
                {
                    throw new EncoderFallbackException(
                        $"Character '{next}' from the fallback has no mapping in {WebNameValue}.");
                }

                if (bytes is not null)
                {
                    if (byteIndex + written >= bytes.Length)
                    {
                        throw new ArgumentException("The output byte buffer is too small.", nameof(bytes));
                    }

                    bytes[byteIndex + written] = mapped;
                }

                written++;
            }

            return written;
        }
        /// <summary>
        /// Looks up the byte for a single scalar value (1-2 chars).
        /// </summary>
        /// <param name="value">The scalar value as a string.</param>
        /// <param name="result">The mapped byte, when found.</param>
        /// <returns>True when the scalar value has a mapping.</returns>
        protected bool TryEncodeScalar(string value, out byte result)
        {
            return encodeTable.TryGetValue(value, out result);
        }

        /// <inheritdoc/>
        public override int GetByteCount(char[] chars, int index, int count)
        {
            ArgumentNullException.ThrowIfNull(chars);
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(count, chars.Length - index);
            var bytes = 0;
            var end = index + count;
            for (var i = index; i < end; i++)
            {
                int length = 1;
                string scalar;
                if (char.IsHighSurrogate(chars[i]) && i + 1 < end && char.IsLowSurrogate(chars[i + 1]))
                {
                    scalar = new string([chars[i], chars[i + 1]]);
                    length = 2;
                    i++;
                }
                else
                {
                    scalar = chars[i].ToString();
                }

                bytes += encodeTable.ContainsKey(scalar)
                    ? 1
                    : EncodeWithFallback(chars, i - length + 1, length, null, 0);
            }

            return bytes;
        }

        /// <inheritdoc/>
        public override int GetBytes(char[] chars, int charIndex, int charCount, byte[] bytes, int byteIndex)
        {
            ArgumentNullException.ThrowIfNull(chars);
            ArgumentNullException.ThrowIfNull(bytes);
            ArgumentOutOfRangeException.ThrowIfNegative(charIndex);
            ArgumentOutOfRangeException.ThrowIfNegative(charCount);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(charCount, chars.Length - charIndex);
            ArgumentOutOfRangeException.ThrowIfNegative(byteIndex);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(byteIndex, bytes.Length);
            var end = charIndex + charCount;
            var written = 0;
            for (var i = charIndex; i < end; i++)
            {
                int length = 1;
                string scalar;
                if (char.IsHighSurrogate(chars[i]) && i + 1 < end && char.IsLowSurrogate(chars[i + 1]))
                {
                    scalar = new string([chars[i], chars[i + 1]]);
                    length = 2;
                    i++;
                }
                else
                {
                    scalar = chars[i].ToString();
                }

                if (!encodeTable.TryGetValue(scalar, out var mapped))
                {
                    written += EncodeWithFallback(chars, i - length + 1, length, bytes, byteIndex + written);
                    continue;
                }

                if (byteIndex + written >= bytes.Length)
                {
                    throw new ArgumentException("The output byte buffer is too small.", nameof(bytes));
                }

                bytes[byteIndex + written] = mapped;
                written++;
            }

            return written;
        }

        /// <inheritdoc/>
        public override int GetCharCount(byte[] bytes, int index, int count)
        {
            ArgumentNullException.ThrowIfNull(bytes);
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(count, bytes.Length - index);
            var chars = 0;
            for (var i = index; i < index + count; i++)
            {
                chars += decodeTable[bytes[i]].Length;
            }

            return chars;
        }

        /// <inheritdoc/>
        public override int GetChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex)
        {
            ArgumentNullException.ThrowIfNull(bytes);
            ArgumentNullException.ThrowIfNull(chars);
            ArgumentOutOfRangeException.ThrowIfNegative(byteIndex);
            ArgumentOutOfRangeException.ThrowIfNegative(byteCount);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(byteCount, bytes.Length - byteIndex);
            ArgumentOutOfRangeException.ThrowIfNegative(charIndex);
            var written = 0;
            for (var i = byteIndex; i < byteIndex + byteCount; i++)
            {
                var decoded = decodeTable[bytes[i]];
                decoded.CopyTo(0, chars, charIndex + written, decoded.Length);
                written += decoded.Length;
            }

            return written;
        }

        /// <inheritdoc/>
        public override int GetMaxByteCount(int charCount)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(charCount);
            // Each input char can expand through the replacement text, with
            // each substitute char encoding to one byte.
            return checked(charCount * Math.Max(1, EncoderFallback.MaxCharCount));
        }

        /// <inheritdoc/>
        public override int GetMaxCharCount(int byteCount)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(byteCount);
            return checked(byteCount * 2);
        }

        private sealed class CharmapEncoder(CharmapEncoding encoding) : Encoder
        {
            private char? pendingLead;

            public override int GetByteCount(char[] chars, int index, int count, bool flush)
            {
                ArgumentNullException.ThrowIfNull(chars);
                ArgumentOutOfRangeException.ThrowIfNegative(index);
                ArgumentOutOfRangeException.ThrowIfNegative(count);
                ArgumentOutOfRangeException.ThrowIfGreaterThan(count, chars.Length - index);
                var end = index + count;
                var i = index;
                var bytes = 0;
                char? lead = pendingLead;
                if (lead.HasValue)
                {
                    if (i < end && char.IsLowSurrogate(chars[i]))
                    {
                        var pair = new string([lead.Value, chars[i]]);
                        bytes += encoding.TryEncodeScalar(pair, out _)
                            ? 1
                            : encoding.EncodeWithFallback([lead.Value, chars[i]], 0, 2, null, 0);
                        i++;
                    }
                    else if (flush)
                    {
                        bytes += encoding.EncodeWithFallback([lead.Value], 0, 1, null, 0);
                    }

                    lead = null;
                    if (!flush && i >= end)
                    {
                        return bytes;
                    }
                }

                while (i < end)
                {
                    if (char.IsHighSurrogate(chars[i]))
                    {
                        if (i + 1 < end && char.IsLowSurrogate(chars[i + 1]))
                        {
                            var scalar = new string([chars[i], chars[i + 1]]);
                            bytes += encoding.TryEncodeScalar(scalar, out _)
                                ? 1
                                : encoding.EncodeWithFallback(chars, i, 2, null, 0);
                            i += 2;
                        }
                        else if (i + 1 == end && !flush)
                        {
                            break;
                        }
                        else
                        {
                            bytes += encoding.EncodeWithFallback(chars, i, 1, null, 0);
                            i++;
                        }
                    }
                    else
                    {
                        var scalar = chars[i].ToString();
                        bytes += encoding.TryEncodeScalar(scalar, out _)
                            ? 1
                            : encoding.EncodeWithFallback(chars, i, 1, null, 0);
                        i++;
                    }
                }

                return bytes;
            }

            public override int GetBytes(char[] chars, int charIndex, int charCount, byte[] bytes, int byteIndex, bool flush)
            {
                var end = charIndex + charCount;
                var i = charIndex;
                var written = 0;
                if (pendingLead.HasValue)
                {
                    if (i < end && char.IsLowSurrogate(chars[i]))
                    {
                        var lead = pendingLead.Value;
                        var scalar = new string([lead, chars[i]]);
                        pendingLead = null;
                        if (byteIndex + written >= bytes.Length)
                        {
                            return written;
                        }

                        if (!encoding.TryEncodeScalar(scalar, out var mapped))
                        {
                            written += encoding.EncodeWithFallback([lead, chars[i]], 0, 2, bytes, byteIndex + written);
                        }
                        else
                        {
                            bytes[byteIndex + written] = mapped;
                            written++;
                        }

                        i++;
                    }
                    else if (flush)
                    {
                        var lead = pendingLead.Value;
                        pendingLead = null;
                        written += encoding.EncodeWithFallback([lead], 0, 1, bytes, byteIndex + written);
                    }
                    else if (i >= end)
                    {
                        return written;
                    }
                    else
                    {
                        var lead = pendingLead.Value;
                        pendingLead = null;
                        written += encoding.EncodeWithFallback([lead], 0, 1, bytes, byteIndex + written);
                    }
                }

                while (i < end)
                {
                    var start = i;
                    string scalar;
                    int next;
                    if (char.IsHighSurrogate(chars[i]) && i + 1 < end && char.IsLowSurrogate(chars[i + 1]))
                    {
                        scalar = new string([chars[i], chars[i + 1]]);
                        next = i + 2;
                    }
                    else if (char.IsHighSurrogate(chars[i]) && i + 1 == end && !flush)
                    {
                        pendingLead = chars[i];
                        i++;
                        continue;
                    }
                    else
                    {
                        scalar = chars[i].ToString();
                        next = i + 1;
                    }

                    if (byteIndex + written >= bytes.Length)
                    {
                        return written;
                    }

                    i = next;
                    if (!TryWriteScalar(chars, start, next - start, scalar, bytes, byteIndex, ref written))
                    {
                        return written;
                    }
                }

                return written;
            }

            public override void Reset()
            {
                pendingLead = null;
            }

            private bool TryWriteScalar(char[] chars, int start, int length, string scalar, byte[] bytes, int byteIndex, ref int written)
            {
                if (byteIndex + written >= bytes.Length)
                {
                    return false;
                }

                if (!encoding.TryEncodeScalar(scalar, out var mapped))
                {
                    written += encoding.EncodeWithFallback(chars, start, length, bytes, byteIndex + written);
                    return true;
                }

                bytes[byteIndex + written] = mapped;
                written++;
                return true;
            }
        }
    }
}
