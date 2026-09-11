namespace telnet_cs.Encodings
{
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
            // Char-array counts stay raw: EOL folding needs string context for
            // the CRLF pair, so only the string overloads normalize.
            return base.GetByteCount(chars, index, count);
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
            "♥", "├", "⎹", "┘", "┤", "└", "╱", "╲", "◢", "▗", "◣", "▝", "▘", "🮂", "▂", "▖",
            // 0x10-0x1F: graphics + arrows
            "♣", "┌", "─", "┼", "●", "▄", "▎", "┬", "┴", "▌", "└", "⎛", "↑", "↓", "←", "→",
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
            "♥", "├", "▊", "┘", "┤", "└", "╱", "╲", "◤", "▛", "◥", "▙", "▟", "▆", "🮅", "▜",
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

            public override int GetByteCount(char[] chars, int index, int count, bool flush)
            {
                // Folding is length-neutral except for CRLF pairs; count the
                // worst case (no folding) so the buffer always fits.
                return count;
            }

            public override int GetBytes(char[] chars, int charIndex, int charCount, byte[] bytes, int byteIndex, bool flush)
            {
                var end = charIndex + charCount;
                var i = charIndex;
                var written = 0;
                if (pendingCr)
                {
                    // A CR straddled the previous chunk boundary: the next char
                    // decides CR vs CRLF, but both fold to LF either way.
                    if (byteIndex + written >= bytes.Length)
                    {
                        return written;
                    }

                    pendingCr = false;
                    if (!TryWriteScalar("\n", bytes, byteIndex, ref written))
                    {
                        return written;
                    }
                }

                while (i < end)
                {
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
                    if (!TryWriteScalar(scalar, bytes, byteIndex, ref written))
                    {
                        return written;
                    }
                }

                return written;
            }

            public override void Reset()
            {
                pendingCr = false;
            }

            private bool TryWriteScalar(string scalar, byte[] bytes, int byteIndex, ref int written)
            {
                if (byteIndex + written >= bytes.Length)
                {
                    return false;
                }

                if (!encoding.TryEncodeScalar(scalar, out var mapped))
                {
                    throw new EncoderFallbackException($"Character '{scalar}' has no mapping in atascii.");
                }

                bytes[byteIndex + written] = mapped;
                written++;
                return true;
            }
        }
    }
}
