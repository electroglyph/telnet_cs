namespace telnet_cs.Encodings
{
    using System;
    using System.Collections.Generic;
    using System.Text;

    /// <summary>
    /// ATASCII, the Atari 8-bit (400/800/XL/XE) character encoding. Bytes
    /// 0x20-0x5F are ASCII, 0x61-0x7A are lowercase, 0x00-0x1F are graphics,
    /// 0x60/0x7B/0x7D-0x7F are suit/control glyphs, and 0x80-0xFF are
    /// inverse-video variants. Byte 0x9B is end-of-line (LF). Aliases:
    /// <c>atari8bit</c>, <c>atari_8bit</c>.
    /// </summary>
    public sealed class AtasciiEncoding : CharmapEncoding
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="AtasciiEncoding"/> class.
        /// </summary>
        public AtasciiEncoding()
            : base("atascii", DecodeTable, preferLowRange: true)
        {
        }

        /// <inheritdoc/>
        protected override void ApplyEncodeOverrides(Dictionary<string, byte> table)
        {
            // LF must encode to the ATASCII end-of-line byte 0x9B, never 0x0A
            // (0x0A decodes to a graphics triangle).
            table["\n"] = 0x9B;
        }

        /// <inheritdoc/>
        public override string EncodingName => "ATASCII (Atari 8-bit)";

        /// <summary>
        /// Normalizes CR and CRLF to LF before encoding: ATASCII has no CR
        /// byte (0x0D is a graphics character), so both fold to 0x9B.
        /// </summary>
        /// <param name="s">The string to encode.</param>
        /// <returns>The encoded bytes.</returns>
        public override byte[] GetBytes(string s)
        {
            return base.GetBytes(NormalizeEol(s));
        }

        /// <inheritdoc/>
        public override int GetByteCount(string s)
        {
            return base.GetByteCount(NormalizeEol(s));
        }

        /// <inheritdoc/>
        public override int GetByteCount(char[] chars, int index, int count)
        {
            // One-shot char[] input folds like the string overload: a CRLF
            // pair is fully visible here, so normalize before counting.
            ArgumentNullException.ThrowIfNull(chars);
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(count, chars.Length - index);
            var normalized = NormalizeEol(new string(chars, index, count)).ToCharArray();
            return base.GetByteCount(normalized, 0, normalized.Length);
        }

        /// <inheritdoc/>
        public override int GetBytes(char[] chars, int charIndex, int charCount, byte[] bytes, int byteIndex)
        {
            // Same one-shot folding as GetByteCount: normalize the slice,
            // then encode through the base table.
            ArgumentNullException.ThrowIfNull(chars);
            ArgumentNullException.ThrowIfNull(bytes);
            ArgumentOutOfRangeException.ThrowIfNegative(charIndex);
            ArgumentOutOfRangeException.ThrowIfNegative(charCount);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(charCount, chars.Length - charIndex);
            ArgumentOutOfRangeException.ThrowIfNegative(byteIndex);
            var normalized = NormalizeEol(new string(chars, charIndex, charCount)).ToCharArray();
            return base.GetBytes(normalized, 0, normalized.Length, bytes, byteIndex);
        }

        /// <inheritdoc/>
        public override byte[] GetBytes(char[] chars, int index, int count)
        {
            // One-shot array encoding folds like the string overload.
            ArgumentNullException.ThrowIfNull(chars);
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(count, chars.Length - index);
            var normalized = NormalizeEol(new string(chars, index, count)).ToCharArray();
            return base.GetBytes(normalized, 0, normalized.Length);
        }

        /// <inheritdoc/>
        public override Encoder GetEncoder()
        {
            return new AtasciiEncoder(this);
        }

        private static string NormalizeEol(string value)
        {
            return value.Replace("\r\n", "\n", System.StringComparison.Ordinal).Replace('\r', '\n');
        }

        private static readonly string[] DecodeTable =
        [
            // 0x00-0x0F: graphics
            "♥", "├", "⎹", "┘", "┤", "┐", "╱", "╲", "◢", "▗", "◣", "▝", "▘", "🮂", "▂", "▖",
            // 0x10-0x1F: graphics + arrows
            "♣", "┌", "─", "┼", "●", "▄", "▎", "┬", "┴", "▌", "└", "␛", "↑", "↓", "←", "→",
            // 0x20-0x2F: ASCII
            " ", "!", "\"", "#", "$", "%", "&", "'", "(", ")", "*", "+", ",", "-", ".", "/",
            // 0x30-0x3F
            "0", "1", "2", "3", "4", "5", "6", "7", "8", "9", ":", ";", "<", "=", ">", "?",
            // 0x40-0x4F
            "@", "A", "B", "C", "D", "E", "F", "G", "H", "I", "J", "K", "L", "M", "N", "O",
            // 0x50-0x5F
            "P", "Q", "R", "S", "T", "U", "V", "W", "X", "Y", "Z", "[", "\\", "]", "^", "_",
            // 0x60-0x6F: diamond + lowercase
            "♦", "a", "b", "c", "d", "e", "f", "g", "h", "i", "j", "k", "l", "m", "n", "o",
            // 0x70-0x7F: lowercase + spade/pipe/clear/backspace/tab
            "p", "q", "r", "s", "t", "u", "v", "w", "x", "y", "z", "♠", "|", "↰", "◀", "▶",
            // 0x80-0x8F: inverse (distinct block glyphs where they exist)
            "♥", "├", "▊", "┘", "┤", "┐", "╱", "╲", "◤", "▛", "◥", "▙", "▟", "▆", "🮅", "▜",
            // 0x90-0x9F: inverse + EOL at 0x9B
            "♣", "┌", "─", "┼", "◘", "▀", "🮊", "┬", "┴", "▐", "└", "\n", "↑", "↓", "←", "→",
            // 0xA0-0xAF: full block + inverse ASCII
            "█", "!", "\"", "#", "$", "%", "&", "'", "(", ")", "*", "+", ",", "-", ".", "/",
            // 0xB0-0xBF
            "0", "1", "2", "3", "4", "5", "6", "7", "8", "9", ":", ";", "<", "=", ">", "?",
            // 0xC0-0xCF
            "@", "A", "B", "C", "D", "E", "F", "G", "H", "I", "J", "K", "L", "M", "N", "O",
            // 0xD0-0xDF
            "P", "Q", "R", "S", "T", "U", "V", "W", "X", "Y", "Z", "[", "\\", "]", "^", "_",
            // 0xE0-0xEF: inverse diamond + lowercase
            "♦", "a", "b", "c", "d", "e", "f", "g", "h", "i", "j", "k", "l", "m", "n", "o",
            // 0xF0-0xFF
            "p", "q", "r", "s", "t", "u", "v", "w", "x", "y", "z", "♠", "|", "↰", "◀", "▶",
        ];

        private sealed class AtasciiEncoder(AtasciiEncoding encoding) : Encoder
        {
            private bool pendingCr;
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
                var pending = pendingCr;
                char? lead = pendingLead;
                if (pending)
                {
                    bytes++;
                    if (i < end && chars[i] == '\n')
                    {
                        i++;
                    }

                    pending = false;
                }

                if (lead.HasValue)
                {
                    if (i < end && char.IsLowSurrogate(chars[i]))
                    {
                        bytes++;
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
                    if (chars[i] == '\r')
                    {
                        if (i + 1 < end && chars[i + 1] == '\n')
                        {
                            bytes++;
                            i += 2;
                        }
                        else if (i + 1 == end && !flush)
                        {
                            pending = true;
                            i++;
                        }
                        else
                        {
                            bytes++;
                            i++;
                        }
                    }
                    else if (char.IsHighSurrogate(chars[i]))
                    {
                        if (i + 1 < end && char.IsLowSurrogate(chars[i + 1]))
                        {
                            var scalar = new string([chars[i], chars[i + 1]]);
                            bytes += encoding.TryEncodeScalar(scalar, out _) ? 1 : encoding.EncodeWithFallback(chars, i, 2, null, 0);
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
                        bytes += encoding.TryEncodeScalar(scalar, out _) ? 1 : encoding.EncodeWithFallback(chars, i, 1, null, 0);
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

                if (pendingCr)
                {
                    if (i >= end && !flush)
                    {
                        return written;
                    }

                    if (byteIndex + written >= bytes.Length)
                    {
                        return written;
                    }

                    pendingCr = false;
                    if (!encoding.TryEncodeScalar("\n", out var folded))
                    {
                        throw new EncoderFallbackException("Character '\n' has no mapping in atascii.");
                    }

                    bytes[byteIndex + written] = folded;
                    written++;
                    if (i < end && chars[i] == '\n')
                    {
                        i++;
                    }
                }

                while (i < end)
                {
                    var start = i;
                    string scalar;
                    int next;
                    if (chars[i] == '\r')
                    {
                        if (i + 1 < end && chars[i + 1] == '\n')
                        {
                            next = i + 2;
                        }
                        else if (i + 1 == end && !flush)
                        {
                            pendingCr = true;
                            i++;
                            continue;
                        }
                        else
                        {
                            next = i + 1;
                        }

                        scalar = "\n";
                    }
                    else if (char.IsHighSurrogate(chars[i]) && i + 1 < end && char.IsLowSurrogate(chars[i + 1]))
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
                pendingCr = false;
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
