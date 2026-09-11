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
    /// Unmappable characters throw via <see cref="EncoderFallback.ExceptionFallback"/>
    /// (the equivalent of Python <c>errors="strict"</c>).
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

        /// <summary>
        /// Adjusts the built encode table (e.g. ATASCII maps LF to 0x9B).
        /// </summary>
        /// <param name="table">The encode table to adjust.</param>
        protected virtual void ApplyEncodeOverrides(Dictionary<string, byte> table)
        {
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
                string scalar;
                if (char.IsHighSurrogate(chars[i]) && i + 1 < end && char.IsLowSurrogate(chars[i + 1]))
                {
                    scalar = new string([chars[i], chars[i + 1]]);
                    i++;
                }
                else
                {
                    scalar = chars[i].ToString();
                }

                if (!encodeTable.ContainsKey(scalar))
                {
                    throw new EncoderFallbackException($"Character '{scalar}' has no mapping in {WebNameValue}.");
                }

                bytes++;
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
            var end = charIndex + charCount;
            var written = 0;
            for (var i = charIndex; i < end; i++)
            {
                string scalar;
                if (char.IsHighSurrogate(chars[i]) && i + 1 < end && char.IsLowSurrogate(chars[i + 1]))
                {
                    scalar = new string([chars[i], chars[i + 1]]);
                    i++;
                }
                else
                {
                    scalar = chars[i].ToString();
                }

                if (!encodeTable.TryGetValue(scalar, out var mapped))
                {
                    throw new EncoderFallbackException($"Character '{scalar}' has no mapping in {WebNameValue}.");
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
            return charCount;
        }

        /// <inheritdoc/>
        public override int GetMaxCharCount(int byteCount)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(byteCount);
            return checked(byteCount * 2);
        }
    }
}
