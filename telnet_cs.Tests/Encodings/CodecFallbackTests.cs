// Codec error-mode tests (conflict 14): the retro charmaps and Big5-BBS honor
// the configured EncoderFallback — strict (default) throws, "?" replaces,
// "" ignores — and the stateful Encoder/Decoder carry split input across
// Convert calls until Reset, which is also what StreamReader/Writer use.
namespace telnet_cs.Tests
{
    using System;
    using System.IO;
    using System.Text;
    using FluentAssertions;
    using Xunit;
    using telnet_cs.Encodings;

    public class CodecFallbackTests
    {
        private static Encoding CreateCodec(string name) => name switch
        {
            "atascii" => new AtasciiEncoding(),
            "petscii" => new PetsciiEncoding(),
            "atarist" => new AtaristEncoding(),
            "big5bbs" => new Big5BbsEncoding(),
            _ => throw new ArgumentOutOfRangeException(nameof(name)),
        };

        private static Encoding WithFallbacks(string name, string replacement)
        {
            return Encoding.GetEncoding(
                CreateCodec(name).WebName,
                new EncoderReplacementFallback(replacement),
                new DecoderReplacementFallback(replacement));
        }

        [Theory]
        [InlineData("atascii")]
        [InlineData("petscii")]
        [InlineData("atarist")]
        [InlineData("big5bbs")]
        public void Encode_Unmappable_StrictDefault_Throws(string name)
        {
            Action encode = () => CreateCodec(name).GetBytes("💉");
            encode.Should().Throw<EncoderFallbackException>();
        }

        [Theory]
        [InlineData("atascii")]
        [InlineData("petscii")]
        [InlineData("atarist")]
        [InlineData("big5bbs")]
        public void Encode_Unmappable_ReplaceFallback_EmitsQuestionMark(string name)
        {
            WithFallbacks(name, "?").GetBytes("💉").Should().Equal(0x3F);
        }

        [Theory]
        [InlineData("atascii")]
        [InlineData("petscii")]
        [InlineData("atarist")]
        [InlineData("big5bbs")]
        public void Encode_Unmappable_IgnoreFallback_EmitsNothing(string name)
        {
            WithFallbacks(name, string.Empty).GetBytes("💉").Should().BeEmpty();
        }

        [Fact]
        public void Encode_MixedContent_ReplaceFallback_CountMatchesBytes()
        {
            var encoding = WithFallbacks("atascii", "?");
            var bytes = encoding.GetBytes("a💉b");
            bytes.Should().Equal(0x61, 0x3F, 0x62);
            encoding.GetByteCount("a💉b").Should().Be(3);
            WithFallbacks("atascii", string.Empty).GetByteCount("a💉b").Should().Be(2);
        }

        [Fact]
        public void GetEncoding_WithFallbacks_UsesProviderClonePath()
        {
            // Exercises EncodingProvider.GetEncoding(name, encoder, decoder)
            // (clone + fallback install) rather than a directly constructed instance.
            var encoding = Encoding.GetEncoding(
                "petscii",
                new EncoderReplacementFallback("?"),
                new DecoderReplacementFallback("?"));
            encoding.Should().BeOfType<PetsciiEncoding>();
            encoding.GetBytes("💉").Should().Equal(0x3F);
        }

        [Fact]
        public void AtasciiEncoder_SplitCrLf_EmitsSingleEol()
        {
            var encoder = new AtasciiEncoding().GetEncoder();
            var first = new byte[16];
            var second = new byte[16];
            encoder.GetBytes("a\r".ToCharArray(), 0, 2, first, 0, false).Should().Be(1);
            first[0].Should().Be(0x61);
            encoder.GetBytes("\nb".ToCharArray(), 0, 2, second, 0, true).Should().Be(2);
            second[0].Should().Be(0x9B);
            second[1].Should().Be(0x62);
        }

        [Fact]
        public void AtasciiEncoder_Reset_DropsPendingCr()
        {
            var encoder = new AtasciiEncoding().GetEncoder();
            var held = new byte[16];
            encoder.GetBytes("a\r".ToCharArray(), 0, 2, held, 0, false).Should().Be(1);
            encoder.Reset();
            var after = new byte[16];
            encoder.GetBytes("b".ToCharArray(), 0, 1, after, 0, true).Should().Be(1);
            after[0].Should().Be(0x62);
        }

        [Fact]
        public void AtasciiEncoder_ReplaceFallback_AppliesToIncrementalWrites()
        {
            var encoder = WithFallbacks("atascii", "?").GetEncoder();
            var bytes = new byte[16];
            encoder.GetBytes("a💉".ToCharArray(), 0, 3, bytes, 0, true).Should().Be(2);
            bytes[0].Should().Be(0x61);
            bytes[1].Should().Be(0x3F);
        }

        [Fact]
        public void Big5BbsDecoder_SplitLead_AcrossChunks()
        {
            var decoder = new Big5BbsEncoding().GetDecoder();
            var chars = new char[16];
            decoder.GetChars([0xA4], 0, 1, chars, 0, false).Should().Be(0);
            decoder.GetChars([0xA4], 0, 1, chars, 0, true).Should().Be(1);
            chars[0].Should().Be('中');
        }

        [Fact]
        public void Big5BbsDecoder_Reset_DropsPendingLead()
        {
            var decoder = new Big5BbsEncoding().GetDecoder();
            var chars = new char[16];
            decoder.GetChars([0xA4], 0, 1, chars, 0, false).Should().Be(0);
            decoder.Reset();
            decoder.GetChars([(byte)'A'], 0, 1, chars, 0, true).Should().Be(1);
            chars[0].Should().Be('A');
        }

        [Fact]
        public void Big5Bbs_StreamReader_DecodesPairAndArt()
        {
            using var stream = new MemoryStream([0xA4, 0xA4, 0xB0], writable: false);
            using var reader = new StreamReader(stream, new Big5BbsEncoding());
            reader.ReadToEnd().Should().Be("中░");
        }

        [Fact]
        public void Atascii_StreamWriter_FoldsLineEndings()
        {
            using var stream = new MemoryStream();
            using (var writer = new StreamWriter(stream, new AtasciiEncoding(), leaveOpen: true))
            {
                writer.Write("a\r\nb");
                writer.Flush();
            }

            stream.ToArray().Should().Equal(0x61, 0x9B, 0x62);
        }
    }
}
