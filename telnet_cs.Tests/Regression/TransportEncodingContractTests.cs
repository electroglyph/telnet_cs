namespace telnet_cs.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Text;
    using FluentAssertions;
    using Xunit;
    using telnet_cs.Client;
    using telnet_cs.Encodings;
    using telnet_cs.IO;
    using telnet_cs.Transport;

    public class TransportEncodingContractTests
    {
        [Fact]
        public void RegisteredProvider_ResolvesAdvertisedCodepage()
        {
            // System.Text.EncodingProvider contract: every codepage named
            // by GetEncodings must resolve via GetEncoding (cf.
            // CodePagesEncodingProvider). Advertising 80001-80004 while
            // GetEncoding(int) returns null breaks Encoding.GetEncoding.
            try
            {
                Encoding.RegisterProvider(TelnetEncodingProvider.Instance);
            }
            catch (ArgumentException)
            {
            }

            Encoding.GetEncoding(80001).Should().NotBeNull();
            TelnetEncodingProvider.Instance.GetEncoding(80001).Should().NotBeNull();
        }

        [Fact]
        public void AtasciiGetBytes_CharArray_MatchesStringOverload()
        {
            // System.Text.Encoding overloads must agree on the same logical
            // input: the string path folds CR/CRLF to LF, so the one-shot
            // char[] path must too (a CRLF pair is fully visible in one
            // call; only cross-chunk Encoder streaming needs extra state,
            // which AtasciiEncoder already carries).
            var encoding = new AtasciiEncoding();
            var fromString = encoding.GetBytes("a\r\nb");
            var chars = new[] { 'a', '\r', '\n', 'b' };
            encoding.GetBytes(chars, 0, chars.Length).Should().Equal(fromString);
        }

        [Fact]
        public void CharmapGetBytes_ShortDestination_ThrowsArgumentException()
        {
            // Encoding.GetBytes(char[],int,int,byte[],int) documents
            // ArgumentException when the destination lacks capacity; the
            // direct-mapped store currently escapes as
            // IndexOutOfRangeException instead.
            var encoding = new AtasciiEncoding();
            var chars = new[] { 'a', 'b' };
            Action write = () => encoding.GetBytes(chars, 0, chars.Length, new byte[1], 0);
            write.Should().Throw<ArgumentException>();
        }

        [Fact]
        public void CharmapMaxByteCount_AccountsForReplacementText()
        {
            // GetMaxByteCount documents a worst case "including the worst
            // case for the currently selected EncoderFallback": with a "??"
            // replacement one input char encodes to 2 bytes, so returning
            // charCount (1) undersizes the caller buffer.
            var encoding = new AtasciiEncoding
            {
                EncoderFallback = new EncoderReplacementFallback("??"),
            };
            encoding.GetMaxByteCount(1).Should().BeGreaterThanOrEqualTo(2);
        }

        [Fact]
        public void Big5MaxByteCount_AccountsForReplacementText()
        {
            // Same GetMaxByteCount contract for the double-byte codec: with
            // a 5-char replacement one input char can encode to 5 bytes, so
            // the flat charCount * 2 formula undersizes the caller buffer.
            // (A 2-char replacement genuinely fits in 2 bytes, so the
            // discriminating case needs a longer replacement.)
            var encoding = new Big5BbsEncoding
            {
                EncoderFallback = new EncoderReplacementFallback("?????"),
            };
            encoding.GetMaxByteCount(1).Should().BeGreaterThanOrEqualTo(5);
        }

        [Fact]
        public void ByteConverter_ExoticByte255_IsEscaped()
        {
            // RFC 854: "only the IAC need be doubled to be sent as data" —
            // any 0xFF wire byte is IAC whatever the source character (here
            // U+00A0, which cp437 maps to 0xFF). telnetlib3 escapes after
            // encoding (buf.replace(IAC, IAC+IAC)); doubling pre-encoding at
            // char level misses it.
            try
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            }
            catch (ArgumentException)
            {
            }

            var cp437 = Encoding.GetEncoding(437);
            ByteStringConverter.ConvertStringToByteArray(" ", cp437).Should().Equal(255, 255);
        }

        [Fact]
        public void NetworkStream_NullBacking_ThrowsArgumentNull()
        {
            // Framework convention (cf. BufferedStream(Stream), and this
            // repo's own TelnetSessionBase ctor): public constructors reject null
            // with ArgumentNullException (CA1062) instead of NRE-ing on
            // first use.
            Action create = () => new NetworkStream(null!);
            create.Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public void InputFilter_Tables_AreNotMutableDictionaries()
        {
            // The statics are process-wide shared translation tables
            // exposed as IReadOnly*: a mutable Dictionary/List runtime type
            // lets any consumer cast and corrupt every session, so the
            // backing must be a read-only/frozen collection.
            ((object)InputFilter.AtasciiSingleBytes).Should().NotBeOfType<Dictionary<byte, byte>>();
            ((object)InputFilter.AtasciiSequences).Should().NotBeOfType<List<KeyValuePair<byte[], byte[]>>>();
        }

        [Fact]
        public void LinemodeBuffer_Defaults_AreNotMutableDictionary()
        {
            // Same shared-mutable-static hazard for the default SLC table:
            // the read-only declared surface must not hide a mutable
            // Dictionary instance.
            ((object)LinemodeBuffer.DefaultSlc).Should().NotBeOfType<Dictionary<int, int>>();
        }
    }
}
