namespace telnet_cs.Encodings
{
    using System;
    using System.Collections.Generic;
    using System.Text;

    /// <summary>
    /// Big5-BBS hybrid codec for Taiwanese BBS systems (PttBBS, DreamBBS):
    /// Big5 lead bytes (0xA1-0xFE) followed by a valid second byte
    /// (0x40-0x7E, 0xA1-0xFE) decode as Big5 when the pair is defined;
    /// lone lead bytes (undefined pairs, or followers like ESC) decode as
    /// CP437 half-width art; bytes below 0xA1 decode as Latin-1. Encoding
    /// prefers Big5 per character with CP437 fallback. Aliases:
    /// <c>big5_bbs</c>, <c>big5_pcman</c>, <c>big5_pcmanx</c>, <c>big5_ptt</c>.
    /// </summary>
    public sealed class Big5BbsEncoding : Encoding
    {
        private static readonly object Sync = new();
        private static bool providerRegistered;
        private static Encoding? big5;
        private static Encoding? cp437;
        private static char[]? cp437Chars;
        private static Dictionary<string, byte>? cp437EncodeTable;

        // Initialized once by EnsureCodePages under lock. The compiler cannot
        // see that edge, so reads go through these guards instead of `!`.
        private static Encoding Big5Codec
        {
            get
            {
                EnsureCodePages();
                return big5 ?? throw new InvalidOperationException("The Big5 code page failed to initialize.");
            }
        }

        private static char[] Cp437CharsOrThrow()
        {
            EnsureCodePages();
            return cp437Chars ?? throw new InvalidOperationException("The CP437 table failed to initialize.");
        }

        private static Dictionary<string, byte> Cp437EncodeTableOrThrow()
        {
            EnsureCodePages();
            return cp437EncodeTable ?? throw new InvalidOperationException("The CP437 encode table failed to initialize.");
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="Big5BbsEncoding"/> class.
        /// </summary>
        public Big5BbsEncoding()
        {
            EnsureCodePages();
        }

        private EncoderFallback? encoderFallbackOverride;
        private DecoderFallback? decoderFallbackOverride;

        /// <summary>
        /// Gets or sets the encoder fallback, defaulting to
        /// <see cref="EncoderFallback.ExceptionFallback"/> (strict). Shadows
        /// the non-virtual base property; see
        /// <c>CharmapEncoding.EncoderFallback</c>.
        /// </summary>
        public new EncoderFallback EncoderFallback
        {
            get => encoderFallbackOverride ?? EncoderFallback.ExceptionFallback;
            set => encoderFallbackOverride = value;
        }

        /// <summary>
        /// Gets or sets the decoder fallback, defaulting to strict (decoding
        /// is total — lone leads become CP437 art — so this never fires).
        /// </summary>
        public new DecoderFallback DecoderFallback
        {
            get => decoderFallbackOverride ?? DecoderFallback.ExceptionFallback;
            set => decoderFallbackOverride = value;
        }

        /// <inheritdoc/>
        public override string WebName => "big5bbs";

        /// <inheritdoc/>
        public override string BodyName => "big5bbs";

        /// <inheritdoc/>
        public override string HeaderName => "big5bbs";

        /// <inheritdoc/>
        public override string EncodingName => "Big5-BBS hybrid (Taiwanese BBS)";

        private static void EnsureCodePages()
        {
            if (providerRegistered)
            {
                return;
            }

            lock (Sync)
            {
                if (providerRegistered)
                {
                    return;
                }

                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                big5 = Encoding.GetEncoding(
                    "big5",
                    EncoderFallback.ExceptionFallback,
                    DecoderFallback.ExceptionFallback);
                cp437 = Encoding.GetEncoding(
                    437,
                    EncoderFallback.ExceptionFallback,
                    DecoderFallback.ExceptionFallback);
                var chars = new char[256];
                var single = new byte[1];
                for (var i = 0; i < 256; i++)
                {
                    single[0] = (byte)i;
                    chars[i] = cp437.GetChars(single)[0];
                }

                cp437Chars = chars;
                var table = new Dictionary<string, byte>(256, StringComparer.Ordinal);
                for (var i = 0; i < 256; i++)
                {
                    table[chars[i].ToString()] = (byte)i;
                }

                cp437EncodeTable = table;
                providerRegistered = true;
            }
        }

        private static bool IsLead(byte value)
        {
            return value is >= 0xA1 and <= 0xFE;
        }

        private static bool IsSecond(byte value)
        {
            return value is >= 0x40 and <= 0x7E or >= 0xA1 and <= 0xFE;
        }

        private static string DecodePair(byte lead, byte second)
        {
            Span<byte> pair = [(byte)lead, second];
            return Big5Codec.GetString(pair);
        }

        /// <summary>
        /// Decodes a slice; used by one-shot overloads (flush) and the
        /// stateful decoder (flush carried from the caller).
        /// </summary>
        private static int DecodeCore(byte[] data, int index, int count, bool flush, ref int pendingLead, char[]? chars, int charIndex)
        {
            var end = index + count;
            var i = index;
            char[] art = Cp437CharsOrThrow();
            if (pendingLead >= 0)
            {
                var lead = pendingLead;
                pendingLead = -1;
                if (i < end)
                {
                    var second = data[i];
                    if (IsSecond(second))
                    {
                        try
                        {
                            WriteDecoded(DecodePair((byte)lead, second), chars, charIndex, out var used);
                            charIndex += used;
                            i++;
                        }
                        catch (DecoderFallbackException)
                        {
                            charIndex += WriteSingle(art[lead], chars, charIndex);
                        }
                    }
                    else
                    {
                        charIndex += WriteSingle(art[lead], chars, charIndex);
                    }
                }
                else if (flush)
                {
                    charIndex += WriteSingle(art[lead], chars, charIndex);
                }
                else
                {
                    pendingLead = lead;
                    return charIndex;
                }
            }

            while (i < end)
            {
                var current = data[i];
                if (!IsLead(current))
                {
                    charIndex += WriteSingle((char)current, chars, charIndex);
                    i++;
                    continue;
                }

                if (i + 1 >= end)
                {
                    if (flush)
                    {
                        charIndex += WriteSingle(art[current], chars, charIndex);
                        i++;
                    }
                    else
                    {
                        pendingLead = current;
                    }

                    break;
                }

                var follower = data[i + 1];
                if (!IsSecond(follower))
                {
                    charIndex += WriteSingle(art[current], chars, charIndex);
                    i++;
                    continue;
                }

                try
                {
                    WriteDecoded(DecodePair(current, follower), chars, charIndex, out var used);
                    charIndex += used;
                    i += 2;
                }
                catch (DecoderFallbackException)
                {
                    // Structurally valid but undefined in Big5: the lone lead
                    // is CP437 art, the second byte is re-processed.
                    charIndex += WriteSingle(art[current], chars, charIndex);
                    i++;
                }
            }

            return charIndex;
        }

        private static int WriteSingle(char value, char[]? chars, int charIndex)
        {
            chars?[charIndex] = value;
            return 1;
        }

        private static void WriteDecoded(string value, char[]? chars, int charIndex, out int used)
        {
            used = value.Length;
            if (chars is not null)
            {
                value.CopyTo(0, chars, charIndex, used);
            }
        }

        /// <inheritdoc/>
        public override Decoder GetDecoder()
        {
            return new Big5BbsDecoder();
        }

        /// <inheritdoc/>
        public override Encoder GetEncoder()
        {
            return new Big5BbsEncoder(this);
        }

        /// <inheritdoc/>
        public override int GetByteCount(char[] chars, int index, int count)
        {
            ArgumentNullException.ThrowIfNull(chars);
            return GetByteCount(new string(chars, index, count));
        }

        /// <inheritdoc/>
        public override int GetByteCount(string s)
        {
            ArgumentNullException.ThrowIfNull(s);
            var bytes = 0;
            var i = 0;
            while (i < s.Length)
            {
                var rune = Rune.GetRuneAt(s, i);
                bytes += EncodeRune(s, i, rune, null, 0);
                i += rune.Utf16SequenceLength;
            }

            return bytes;
        }

        /// <inheritdoc/>
        public override int GetBytes(char[] chars, int charIndex, int charCount, byte[] bytes, int byteIndex)
        {
            ArgumentNullException.ThrowIfNull(chars);
            return GetBytes(new string(chars, charIndex, charCount), 0, charCount, bytes, byteIndex);
        }

        /// <inheritdoc/>
        public override int GetBytes(string s, int charIndex, int charCount, byte[] bytes, int byteIndex)
        {
            ArgumentNullException.ThrowIfNull(s);
            ArgumentNullException.ThrowIfNull(bytes);
            var end = charIndex + charCount;
            var written = 0;
            var i = charIndex;
            while (i < end)
            {
                var rune = Rune.GetRuneAt(s, i);
                written += EncodeRune(s, i, rune, bytes, byteIndex + written);
                i += rune.Utf16SequenceLength;
            }

            return written;
        }

        private int EncodeRune(string s, int charIndex, Rune rune, byte[]? bytes, int byteIndex)
        {
            var text = rune.ToString();
            if (TryEncodeCore(text, bytes, byteIndex, out var count))
            {
                return count;
            }

            // No Big5 or CP437 mapping: go through the configured fallback
            // (strict throws here; replacement emits substitute text that is
            // itself encoded Big5-first, CP437-second).
            return EncodeWithFallback(
                s[charIndex],
                rune.Utf16SequenceLength == 2 ? s[charIndex + 1] : '\0',
                rune.Utf16SequenceLength == 2,
                charIndex,
                bytes,
                byteIndex);
        }

        private int EncodeWithFallback(char high, char low, bool isPair, int fallbackIndex, byte[]? bytes, int byteIndex)
        {
            var fallback = EncoderFallback;
            var buffer = fallback.CreateFallbackBuffer();
            if (isPair && fallback is EncoderReplacementFallback)
            {
                // One character, one substitute (see CharmapEncoding).
                buffer.Fallback(high, fallbackIndex);
            }
            else if (isPair)
            {
                buffer.Fallback(high, low, fallbackIndex);
            }
            else
            {
                buffer.Fallback(high, fallbackIndex);
            }

            var substitute = new StringBuilder();
            char next;
            while ((next = buffer.GetNextChar()) != '\0')
            {
                substitute.Append(next);
            }

            var drained = substitute.ToString();
            var written = 0;
            var j = 0;
            while (j < drained.Length)
            {
                var scalar = Rune.GetRuneAt(drained, j).ToString();
                j += scalar.Length;
                if (!TryEncodeCore(scalar, bytes is null ? null : bytes, byteIndex + written, out var used))
                {
                    throw new EncoderFallbackException(
                        $"Character '{scalar}' from the fallback has no mapping in big5bbs.");
                }

                written += used;
            }

            return written;
        }

        private int EncodeScalar(char[] chars, int start, int length, string scalar, byte[]? bytes, int byteIndex)
        {
            if (TryEncodeCore(scalar, bytes, byteIndex, out var count))
            {
                return count;
            }

            return length == 2
                ? EncodeWithFallback(chars[start], chars[start + 1], true, start, bytes, byteIndex)
                : EncodeWithFallback(scalar[0], '\0', false, start, bytes, byteIndex);
        }

        private bool TryEncodeCore(string text, byte[]? bytes, int byteIndex, out int count)
        {
            try
            {
                var encoded = Big5Codec.GetBytes(text);
                if (bytes is not null)
                {
                    encoded.CopyTo(bytes, byteIndex);
                }

                count = encoded.Length;
                return true;
            }
            catch (EncoderFallbackException)
            {
                if (text.Length == 1 && Cp437EncodeTableOrThrow().TryGetValue(text, out var mapped))
                {
                    if (bytes is not null)
                    {
                        bytes[byteIndex] = mapped;
                    }

                    count = 1;
                    return true;
                }

                count = 0;
                return false;
            }
        }

        /// <inheritdoc/>
        public override int GetCharCount(byte[] bytes, int index, int count)
        {
            ArgumentNullException.ThrowIfNull(bytes);
            var pending = -1;
            return DecodeCore(bytes, index, count, flush: true, ref pending, null, 0);
        }

        /// <inheritdoc/>
        public override int GetChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex)
        {
            ArgumentNullException.ThrowIfNull(bytes);
            ArgumentNullException.ThrowIfNull(chars);
            var pending = -1;
            return DecodeCore(bytes, byteIndex, byteCount, flush: true, ref pending, chars, charIndex);
        }

        /// <inheritdoc/>
        public override int GetMaxByteCount(int charCount)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(charCount);
            // Each input char can expand through the replacement text, with
            // the double-byte case needing two bytes per char.
            return checked(charCount * Math.Max(2, EncoderFallback.MaxCharCount));
        }

        /// <inheritdoc/>
        public override int GetMaxCharCount(int byteCount)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(byteCount);
            return checked(byteCount + 1);
        }

        private sealed class Big5BbsDecoder : Decoder
        {
            private int pendingLead = -1;

            public override int GetCharCount(byte[] bytes, int index, int count)
            {
                return GetCharCount(bytes, index, count, false);
            }

            public override int GetCharCount(byte[] bytes, int index, int count, bool flush)
            {
                var pending = pendingLead;
                return DecodeCore(bytes, index, count, flush, ref pending, null, 0);
            }

            public override int GetChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex)
            {
                return GetChars(bytes, byteIndex, byteCount, chars, charIndex, false);
            }

            public override int GetChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex, bool flush)
            {
                var pending = pendingLead;
                var end = DecodeCore(bytes, byteIndex, byteCount, flush, ref pending, chars, charIndex);
                pendingLead = pending;
                return end - charIndex;
            }

            public override void Reset()
            {
                pendingLead = -1;
            }
        }

        private sealed class Big5BbsEncoder(Big5BbsEncoding encoding) : Encoder
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
                        bytes += encoding.EncodeScalar([lead.Value, chars[i]], 0, 2, new string([lead.Value, chars[i]]), null, 0);
                        i++;
                    }
                    else if (flush || i < end)
                    {
                        // A lead left dangling by new non-low input resolves
                        // through fallback exactly as GetBytes does, so the
                        // count stays exact (and throws under a strict
                        // fallback, like GetBytes).
                        bytes += encoding.EncodeWithFallback(lead.Value, '\0', false, i, null, 0);
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
                            bytes += encoding.EncodeScalar(chars, i, 2, new string([chars[i], chars[i + 1]]), null, 0);
                            i += 2;
                        }
                        else if (i + 1 == end && !flush)
                        {
                            break;
                        }
                        else
                        {
                            bytes += encoding.EncodeWithFallback(chars[i], '\0', false, i, null, 0);
                            i++;
                        }
                    }
                    else
                    {
                        bytes += encoding.EncodeScalar(chars, i, 1, chars[i].ToString(), null, 0);
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
                        pendingLead = null;
                        if (bytes.Length - (byteIndex + written) < 2)
                        {
                            return written;
                        }

                        written += encoding.EncodeScalar([lead, chars[i]], 0, 2, new string([lead, chars[i]]), bytes, byteIndex + written);
                        i++;
                    }
                    else if (flush)
                    {
                        var lead = pendingLead.Value;
                        pendingLead = null;
                        written += encoding.EncodeWithFallback(lead, '\0', false, i, bytes, byteIndex + written);
                    }
                    else if (i >= end)
                    {
                        return written;
                    }
                    else
                    {
                        var lead = pendingLead.Value;
                        pendingLead = null;
                        written += encoding.EncodeWithFallback(lead, '\0', false, i, bytes, byteIndex + written);
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

                    // Scalars encode to at most two bytes; never split one.
                    if (bytes.Length - (byteIndex + written) < 2)
                    {
                        return written;
                    }

                    i = next;
                    written += encoding.EncodeScalar(chars, start, next - start, scalar, bytes, byteIndex + written);
                }

                return written;
            }

            public override void Reset()
            {
                pendingLead = null;
            }
        }
    }
}
