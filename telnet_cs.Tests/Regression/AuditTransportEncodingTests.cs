namespace telnet_cs.Tests
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text;
    using FluentAssertions;
    using Xunit;
    using telnet_cs.Client;
    using telnet_cs.Encodings;
    using telnet_cs.IO;
    using telnet_cs.Transport;

    public class AuditTransportEncodingTests
    {
        [Fact]
        public void RegisteredProvider_ResolvesAdvertisedCodepage()
        {
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
            var encoding = new AtasciiEncoding();
            var fromString = encoding.GetBytes("a\r\nb");
            var chars = new[] { 'a', '\r', '\n', 'b' };
            encoding.GetBytes(chars, 0, chars.Length).Should().Equal(fromString);
        }

        [Fact]
        public void CharmapGetBytes_ShortDestination_ThrowsArgumentException()
        {
            var encoding = new AtasciiEncoding();
            var chars = new[] { 'a', 'b' };
            Action write = () => encoding.GetBytes(chars, 0, chars.Length, new byte[1], 0);
            write.Should().Throw<ArgumentException>();
        }

        [Fact]
        public void CharmapMaxByteCount_AccountsForReplacementText()
        {
            var encoding = new AtasciiEncoding
            {
                EncoderFallback = new EncoderReplacementFallback("??"),
            };
            encoding.GetMaxByteCount(1).Should().BeGreaterThanOrEqualTo(2);
        }

        [Fact]
        public void Big5MaxByteCount_AccountsForReplacementText()
        {
            var encoding = new Big5BbsEncoding
            {
                EncoderFallback = new EncoderReplacementFallback("??"),
            };
            encoding.GetMaxByteCount(1).Should().BeGreaterThanOrEqualTo(4);
        }

        [Fact]
        public void ByteConverter_ExoticByte255_IsEscaped()
        {
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
            Action create = () => new NetworkStream(null!);
            create.Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public void InputFilter_Tables_AreNotMutableDictionaries()
        {
            ((object)InputFilter.AtasciiSingleBytes).Should().NotBeOfType<Dictionary<byte, byte>>();
            ((object)InputFilter.AtasciiSequences).Should().NotBeOfType<List<KeyValuePair<byte[], byte[]>>>();
        }

        [Fact]
        public void LinemodeBuffer_Defaults_AreNotMutableDictionary()
        {
            ((object)LinemodeBuffer.DefaultSlc).Should().NotBeOfType<Dictionary<int, int>>();
        }
    }
}
