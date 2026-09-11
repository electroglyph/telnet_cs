// Codec tests for the retro-computer charmaps (ATASCII, PETSCII, Atari ST)
// and the Big5-BBS hybrid: every assertion pins exact byte values from the
// reference tables.
namespace telnet_cs.Tests
{
    using System;
    using System.Text;
    using FluentAssertions;
    using Xunit;
    using telnet_cs.Encodings;

    public class RetroEncodingTests
    {
        [Fact]
        public void Atascii_Decode_GraphicsAndEol()
        {
            var encoding = new AtasciiEncoding();
            encoding.GetString([0x00]).Should().Be("♥");
            encoding.GetString([0x0D]).Should().Be("\U0001FB82");
            encoding.GetString([0x9B]).Should().Be("\n");
            encoding.GetString([0x41]).Should().Be("A");
            encoding.GetString([0x61]).Should().Be("a");
            encoding.GetString([0x60]).Should().Be("♦");
            encoding.GetString([0x7B]).Should().Be("♠");
            encoding.GetString([0xA0]).Should().Be("█");
            encoding.GetString([0xE1]).Should().Be("a");
        }

        [Fact]
        public void Atascii_Encode_PrefersNormalRange_AndLfToEol()
        {
            var encoding = new AtasciiEncoding();
            encoding.GetBytes("\n").Should().Equal(0x9B);
            encoding.GetBytes("a").Should().Equal(0x61);
            encoding.GetBytes("A").Should().Equal(0x41);
            encoding.GetBytes("█").Should().Equal(0xA0);
            encoding.GetBytes("♥").Should().Equal(0x00);
        }

        [Fact]
        public void Atascii_Encode_FoldsCrAndCrlfToLf()
        {
            var encoding = new AtasciiEncoding();
            encoding.GetBytes("a\r\nb\rc").Should().Equal(0x61, 0x9B, 0x62, 0x9B, 0x63);
        }

        [Fact]
        public void Atascii_Encode_Unmappable_Throws()
        {
            var encoding = new AtasciiEncoding();
            Action encode = () => encoding.GetBytes("€");
            encode.Should().Throw<EncoderFallbackException>();
        }

        [Fact]
        public void Atascii_IncrementalEncoder_HoldsSplitCrAcrossChunks()
        {
            var encoding = new AtasciiEncoding();
            var encoder = encoding.GetEncoder();
            var first = new byte[16];
            var second = new byte[16];
            var usedFirst = encoder.GetBytes("a\r".ToCharArray(), 0, 2, first, 0, false);
            usedFirst.Should().Be(1);
            first[0].Should().Be(0x61);
            var usedSecond = encoder.GetBytes("b".ToCharArray(), 0, 1, second, 0, true);
            usedSecond.Should().Be(2);
            second[0].Should().Be(0x9B);
            second[1].Should().Be(0x62);
        }

        [Fact]
        public void Petscii_Decode_ShiftedLettersAndControls()
        {
            var encoding = new PetsciiEncoding();
            encoding.GetString([0x41]).Should().Be("a");
            encoding.GetString([0xC1]).Should().Be("A");
            encoding.GetString([0xFF]).Should().Be("π");
            encoding.GetString([0x0D]).Should().Be("\r");
            encoding.GetString([0x8D]).Should().Be("\r");
            encoding.GetString([0xA0])[0].Should().Be((char)0xA0);
            encoding.GetString([0x60]).Should().Be("─");
            encoding.GetString([0x20]).Should().Be(" ");
        }

        [Fact]
        public void Petscii_Encode_LastTableEntryWins()
        {
            var encoding = new PetsciiEncoding();
            encoding.GetBytes("a").Should().Equal(0x41);
            encoding.GetBytes("A").Should().Equal(0xC1);
            encoding.GetBytes("\r").Should().Equal(0x8D);
            encoding.GetBytes("π").Should().Equal(0xFF);
        }

        [Fact]
        public void Atarist_Decode_SpotChecks()
        {
            var encoding = new AtaristEncoding();
            encoding.GetString([0x41]).Should().Be("A");
            encoding.GetString([0x80]).Should().Be("Ç");
            encoding.GetString([0x9B]).Should().Be("¢");
            encoding.GetString([0xC2]).Should().Be("א");
            encoding.GetString([0xE0]).Should().Be("α");
            encoding.GetString([0xF0]).Should().Be("≡");
            encoding.GetString([0xFF]).Should().Be("¯");
        }

        [Fact]
        public void Atarist_RoundTrips_AllBytes()
        {
            var encoding = new AtaristEncoding();
            var all = new byte[256];
            for (var i = 0; i < 256; i++)
            {
                all[i] = (byte)i;
            }

            encoding.GetBytes(encoding.GetString(all)).Should().Equal(all);
        }

        [Fact]
        public void Big5Bbs_Decode_AsciiAndBig5Pair()
        {
            var encoding = new Big5BbsEncoding();
            encoding.GetString([0x41]).Should().Be("A");
            encoding.GetString([0xA4, 0xA4]).Should().Be("中");
        }

        [Fact]
        public void Big5Bbs_Decode_LoneLeadBeforeEscape_IsCp437()
        {
            var encoding = new Big5BbsEncoding();
            encoding.GetString([0xA1, 0x1B]).Should().Be("í");
        }

        [Fact]
        public void Big5Bbs_Decode_LeadBeforeEscape_ReprocessesFollower()
        {
            var encoding = new Big5BbsEncoding();
            encoding.GetString([0xA4, 0x1B, 0xA4, 0xA4]).Should().Be("ñ中");
        }

        [Fact]
        public void Big5Bbs_Decode_TrailingLeadAtFlush_IsCp437()
        {
            var encoding = new Big5BbsEncoding();
            encoding.GetString([0xA4]).Should().Be("ñ");
        }

        [Fact]
        public void Big5Bbs_Encode_PrefersBig5_WithCp437Fallback()
        {
            var encoding = new Big5BbsEncoding();
            encoding.GetBytes("中").Should().Equal(0xA4, 0xA4);
            encoding.GetBytes("∙").Should().Equal(0xF9);
            encoding.GetBytes("A").Should().Equal(0x41);
        }

        [Fact]
        public void Big5Bbs_IncrementalDecoder_HoldsSplitLead()
        {
            var encoding = new Big5BbsEncoding();
            var decoder = encoding.GetDecoder();
            var first = new char[16];
            var second = new char[16];
            decoder.GetChars([0xA4], 0, 1, first, 0, false).Should().Be(0);
            decoder.GetChars([0xA4], 0, 1, second, 0, true).Should().Be(1);
            second[0].Should().Be('中');
        }

        [Fact]
        public void Provider_ResolvesNamesAndAliases()
        {
            TelnetEncodings.Register();
            Encoding.GetEncoding("atascii").Should().BeOfType<AtasciiEncoding>();
            Encoding.GetEncoding("ATARI8BIT").Should().BeOfType<AtasciiEncoding>();
            Encoding.GetEncoding("petscii").Should().BeOfType<PetsciiEncoding>();
            Encoding.GetEncoding("c64").Should().BeOfType<PetsciiEncoding>();
            Encoding.GetEncoding("atarist").Should().BeOfType<AtaristEncoding>();
            Encoding.GetEncoding("atari").Should().BeOfType<AtaristEncoding>();
            Encoding.GetEncoding("big5bbs").Should().BeOfType<Big5BbsEncoding>();
            Encoding.GetEncoding("big5_ptt").Should().BeOfType<Big5BbsEncoding>();
        }

        [Fact]
        public void ForceBinary_PolicyMatchesReferenceSet()
        {
            TelnetEncodings.RequiresBinaryMode("atascii").Should().BeTrue();
            TelnetEncodings.RequiresBinaryMode("c64").Should().BeTrue();
            TelnetEncodings.RequiresBinaryMode("atari").Should().BeTrue();
            TelnetEncodings.RequiresBinaryMode("big5bbs").Should().BeFalse();
            TelnetEncodings.RequiresBinaryMode(null).Should().BeFalse();
            TelnetEncodings.ForceBinaryEncodings.Should().HaveCount(10);
        }
    }
}
