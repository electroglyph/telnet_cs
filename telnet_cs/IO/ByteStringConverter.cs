namespace telnet_cs.IO
{
    using System;
    using System.Text;
    using telnet_cs.Encodings;
    using telnet_cs.Protocol;

    /// <summary>
    /// Converts between strings and bytes for the TELNET data path.
    /// </summary>
    /// <remarks>
    /// The default (null-encoding) mapping is Latin-1: bytes 0-255 round-trip
    /// one-to-one. This is intentionally 8-bit-clean whether or not BINARY
    /// (RFC 856, option 0) was negotiated — a documented deviation from strict
    /// 7-bit NVT, kept so agreed-BINARY transfers arrive unmangled.
    /// </remarks>
    internal static class ByteStringConverter
    {
        public static byte[] ConvertStringToByteArray(string value)
        {
            return ConvertStringToByteArray(value, null);
        }

        /// <summary>
        /// Converts a string to bytes, escaping IAC (0xFF) at the byte level
        /// after encoding so every wire 0xFF is doubled (RFC 854), whatever
        /// source character produced it. Encoding is strict: a character the
        /// encoding cannot represent throws instead of silently emitting "?",
        /// so a mangled payload can never reach the wire unnoticed.
        /// </summary>
        /// <param name="value">The string to convert.</param>
        /// <param name="encoding">The encoding to use. When null (default), the legacy Latin-1 mapping is used.</param>
        /// <exception cref="EncoderFallbackException">
        /// Thrown when <paramref name="value"/> holds a character
        /// <paramref name="encoding"/> cannot represent. Nothing is sent in
        /// that case — the failure is loud instead of silently emitting "?".
        /// </exception>
        public static byte[] ConvertStringToByteArray(string value, Encoding? encoding)
        {
            if (encoding == null)
            {
                // Latin-1 maps bytes 0-255 one-to-one to the first 256 Unicode code
                // points. (ASCIIEncoding would map everything above 127 to '?'.) A
                // manual loop preserves the legacy truncation semantics exactly.
                var buffer = new byte[value.Length];
                for (var i = 0; i < value.Length; i++)
                {
                    buffer[i] = (byte)value[i];
                }

                return EscapeIacBytes(buffer);
            }

            return EscapeIacBytes(StrictGetBytes(encoding, value));
        }

        /// <summary>
        /// Encodes <paramref name="value"/> with <paramref name="encoding"/>,
        /// upgrading any replacement fallback to a throwing one so
        /// unrepresentable characters fail loudly instead of emitting "?" onto
        /// the wire. The input encoding is never mutated (a clone takes the
        /// strict fallback). Callers that genuinely want replacement semantics
        /// encode beforehand and pass bytes via the byte-level write paths.
        /// Retro codecs shadow the non-virtual base fallback property with
        /// their own, which their encode path reads, so the clone's shadowed
        /// fallback is forced to strict as well.
        /// </summary>
        private static byte[] StrictGetBytes(Encoding encoding, string value)
        {
            var strict = (Encoding)encoding.Clone();
            strict.EncoderFallback = EncoderFallback.ExceptionFallback;
            switch (strict)
            {
                case CharmapEncoding charmap:
                    charmap.EncoderFallback = EncoderFallback.ExceptionFallback;
                    break;
                case Big5BbsEncoding big5bbs:
                    big5bbs.EncoderFallback = EncoderFallback.ExceptionFallback;
                    break;
            }

            return strict.GetBytes(value);
        }

        /// <summary>
        /// Escapes literal IAC bytes (255) in outbound user data by doubling
        /// them (RFC 854, telnetlib3 <c>write()</c> parity). Returns
        /// <paramref name="data"/> unchanged when it holds no IAC byte, so the
        /// common path allocates nothing. Protocol frames must not pass
        /// through here — they carry meaningful IAC bytes and write to the
        /// byte stream directly.
        /// </summary>
        /// <param name="data">The user bytes to send.</param>
        internal static byte[] EscapeIacBytes(byte[] data)
        {
            ArgumentNullException.ThrowIfNull(data);
            int extra = 0;
            foreach (byte b in data)
            {
                if (b == (byte)Commands.InterpretAsCommand)
                {
                    extra++;
                }
            }

            if (extra == 0)
            {
                return data;
            }

            var escaped = new byte[data.Length + extra];
            int dst = 0;
            foreach (byte b in data)
            {
                escaped[dst++] = b;
                if (b == (byte)Commands.InterpretAsCommand)
                {
                    escaped[dst++] = b;
                }
            }

            return escaped;
        }

        public static string ToString(byte[] bytes)
        {
            return ToString(bytes, 0, bytes.Length);
        }

        /// <summary>
        /// Decodes bytes to a string.
        /// </summary>
        /// <param name="bytes">The bytes to decode.</param>
        /// <param name="encoding">The encoding to use. When null (default), the legacy Latin-1 mapping is used.</param>
        public static string ToString(byte[] bytes, Encoding? encoding)
        {
            return ToString(bytes, 0, bytes.Length, encoding);
        }

        internal static string ToString(byte[] bytes, int offset, int count)
        {
            return ToString(bytes, offset, count, null);
        }

        internal static string ToString(byte[] bytes, int offset, int count, Encoding? encoding)
        {
            ArgumentNullException.ThrowIfNull(bytes);
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(count, bytes.Length - offset);

            if (encoding != null)
            {
                return encoding.GetString(bytes, offset, count);
            }

            // Latin-1 decode: every byte round-trips, including 0xFF. Do not trim:
            // leading/trailing 0xFF bytes may be legitimate data.
            var chars = new char[count];
            for (var i = 0; i < count; i++)
            {
                chars[i] = (char)bytes[offset + i];
            }

            return new string(chars);
        }
    }
}
