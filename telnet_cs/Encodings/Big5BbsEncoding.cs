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
            return big5!.GetString(pair);
        }

        /// <summary>
        /// Decodes a slice; used by one-shot overloads (flush) and the
        /// stateful decoder (flush carried from the caller).
        /// </summary>
        private static int DecodeCore(byte[] data, int index, int count, bool flush, ref int pendingLead, char[]? chars, int charIndex)
        {
            var end = index + count;
            var i = index;
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
                            charIndex += WriteSingle(cp437Chars![lead], chars, charIndex);
                        }
                    }
                    else
                    {
                        charIndex += WriteSingle(cp437Chars![lead], chars, charIndex);
                    }
                }
                else if (flush)
                {
                    charIndex += WriteSingle(cp437Chars![lead], chars, charIndex);
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
                        charIndex += WriteSingle(cp437Chars![current], chars, charIndex);
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
                    charIndex += WriteSingle(cp437Chars![current], chars, charIndex);
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
                    charIndex += WriteSingle(cp437Chars![current], chars, charIndex);
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
            var fallback = EncoderFallback;
            var buffer = fallback.CreateFallbackBuffer();
            if (rune.Utf16SequenceLength == 2 && fallback is EncoderReplacementFallback)
            {
                // One character, one substitute (see CharmapEncoding).
                buffer.Fallback(s[charIndex], charIndex);
            }
            else if (rune.Utf16SequenceLength == 2)
            {
                buffer.Fallback(s[charIndex], s[charIndex + 1], charIndex);
            }
            else
            {
                buffer.Fallback(s[charIndex], charIndex);
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

        private bool TryEncodeCore(string text, byte[]? bytes, int byteIndex, out int count)
        {
            try
            {
                var encoded = big5!.GetBytes(text);
                if (bytes is not null)
                {
                    encoded.CopyTo(bytes, byteIndex);
                }

                count = encoded.Length;
                return true;
            }
            catch (EncoderFallbackException)
            {
                if (text.Length == 1 && cp437EncodeTable!.TryGetValue(text, out var mapped))
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
            return checked(charCount * 2);
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
    }
}
