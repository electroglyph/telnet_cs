// Full decode-table verification against telnetlib3's ground-truth
// DECODING_TABLEs (telnetlib3/encodings/{atascii,petscii,atarist}.py).
// Tables are hex code points, 16 rows of 16 cells. Cells that differ from
// the reference are EXCLUDED from the loop and pinned separately with an
// owner-decision flag (conflicts 17/18 in test.md) — the loop asserts every
// other cell matches the reference exactly.
namespace telnet_cs.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using FluentAssertions;
    using Xunit;
    using telnet_cs.Encodings;

    public class CodecTableTests
    {
        private static string DecodeAll(Encoding encoding)
        {
            var bytes = new byte[256];
            for (var i = 0; i < 256; i++)
            {
                bytes[i] = (byte)i;
            }

            return encoding.GetString(bytes);
        }

        private static string FromHexRows(params string[] rows)
        {
            return string.Concat(
                string.Join(",", rows).Split(',').Select(h => char.ConvertFromUtf32(Convert.ToInt32(h, 16))));
        }

        private static void ShouldMatchExcept(
            Encoding encoding, ISet<int> excluded, params string[] hexRows)
        {
            var expected = FromHexRows(hexRows);
            var actual = DecodeAll(encoding);
            // Decode must be total and single-scalar per byte (astral scalars
            // surface as surrogate pairs, hence the UTF-32 walk).
            var actualScalars = actual.EnumerateRunes().Select(r => r.Value).ToList();
            var expectedScalars = expected.EnumerateRunes().Select(r => r.Value).ToList();
            actualScalars.Should().HaveCount(256);
            expectedScalars.Should().HaveCount(256);
            for (var i = 0; i < 256; i++)
            {
                if (!excluded.Contains(i))
                {
                    actualScalars[i].Should().Be(
                        expectedScalars[i], $"byte 0x{i:X2} must match the telnetlib3 table");
                }
            }
        }

        private static readonly string[] AtasciiHex =
        [
            "2665,251C,23B9,2518,2524,2510,2571,2572,25E2,2597,25E3,259D,2598,1FB82,2582,2596",
            "2663,250C,2500,253C,25CF,2584,258E,252C,2534,258C,2514,241B,2191,2193,2190,2192",
            "0020,0021,0022,0023,0024,0025,0026,0027,0028,0029,002A,002B,002C,002D,002E,002F",
            "0030,0031,0032,0033,0034,0035,0036,0037,0038,0039,003A,003B,003C,003D,003E,003F",
            "0040,0041,0042,0043,0044,0045,0046,0047,0048,0049,004A,004B,004C,004D,004E,004F",
            "0050,0051,0052,0053,0054,0055,0056,0057,0058,0059,005A,005B,005C,005D,005E,005F",
            "2666,0061,0062,0063,0064,0065,0066,0067,0068,0069,006A,006B,006C,006D,006E,006F",
            "0070,0071,0072,0073,0074,0075,0076,0077,0078,0079,007A,2660,007C,21B0,25C0,25B6",
            "2665,251C,258A,2518,2524,2510,2571,2572,25E4,259B,25E5,2599,259F,2586,1FB85,259C",
            "2663,250C,2500,253C,25D8,2580,1FB8A,252C,2534,2590,2514,000A,2191,2193,2190,2192",
            "2588,0021,0022,0023,0024,0025,0026,0027,0028,0029,002A,002B,002C,002D,002E,002F",
            "0030,0031,0032,0033,0034,0035,0036,0037,0038,0039,003A,003B,003C,003D,003E,003F",
            "0040,0041,0042,0043,0044,0045,0046,0047,0048,0049,004A,004B,004C,004D,004E,004F",
            "0050,0051,0052,0053,0054,0055,0056,0057,0058,0059,005A,005B,005C,005D,005E,005F",
            "2666,0061,0062,0063,0064,0065,0066,0067,0068,0069,006A,006B,006C,006D,006E,006F",
            "0070,0071,0072,0073,0074,0075,0076,0077,0078,0079,007A,2660,007C,21B0,25C0,25B6"
        ];

        // telnetlib3 DECODING_TABLE cells, all except 0x05/0x1B/0x85.
        [Fact]
        public void Atascii_FullTable_MatchesReferenceExceptFlaggedCells()
        {
            ShouldMatchExcept(new AtasciiEncoding(), new HashSet<int> { 0x05, 0x1B, 0x85 }, AtasciiHex);
        }

        // Flagged (conflict 17): 0x05 and 0x85 decode to U+2514 here, but
        // telnetlib3 maps both to U+2510; 0x1B decodes to U+239B here vs
        // U+241B there. Pinned as-is until the owner decides.
        [Theory]
        [InlineData(0x05, "\u2514")]
        [InlineData(0x85, "\u2514")]
        [InlineData(0x1B, "\u239B")]
        public void Atascii_FlaggedCells_DecodeAsImplemented(int byteValue, string expected)
        {
            new AtasciiEncoding().GetString([(byte)byteValue]).Should().Be(expected);
        }

        private static readonly string[] PetsciiHex =
        [
            "0000,0001,0002,0003,0004,0005,0006,0007,0008,0009,000A,000B,000C,000D,000E,000F",
            "0010,0011,0012,0013,0014,0015,0016,0017,0018,0019,001A,001B,001C,001D,001E,001F",
            "0020,0021,0022,0023,0024,0025,0026,0027,0028,0029,002A,002B,002C,002D,002E,002F",
            "0030,0031,0032,0033,0034,0035,0036,0037,0038,0039,003A,003B,003C,003D,003E,003F",
            "0040,0061,0062,0063,0064,0065,0066,0067,0068,0069,006A,006B,006C,006D,006E,006F",
            "0070,0071,0072,0073,0074,0075,0076,0077,0078,0079,007A,005B,00A3,005D,2191,2190",
            "2500,2660,2502,2500,2597,2596,2598,259D,2599,259F,259E,2595,258F,2584,2580,2588",
            "2584,259B,2583,2665,259C,256D,2573,25CB,2663,259A,2666,253C,2502,2571,03C0,25E5",
            "0080,0081,0082,0083,0084,0085,0086,0087,0088,0089,008A,008B,008C,000D,008E,008F",
            "0090,0091,0092,0093,0094,0095,0096,0097,0098,0099,009A,009B,009C,009D,009E,009F",
            "00A0,2584,2580,2500,2500,2500,2502,2502,2502,256E,2570,256F,2572,2571,2573,2022",
            "25E4,258C,2597,2514,2510,2582,250C,2534,252C,2524,251C,2586,2585,2590,2588,2572",
            "2500,0041,0042,0043,0044,0045,0046,0047,0048,0049,004A,004B,004C,004D,004E,004F",
            "0050,0051,0052,0053,0054,0055,0056,0057,0058,0059,005A,253C,2502,2571,03C0,25E5",
            "00A0,2584,2580,2500,2500,2500,2502,2502,2502,256E,2570,256F,2572,2571,2573,2022",
            "25E4,258C,2597,2514,2510,2582,250C,2534,252C,2524,251C,2586,2585,2590,2588,03C0"
        ];

        // telnetlib3 DECODING_TABLE cells, all except 0x7A/0xB4/0xE0/0xF4.
        [Fact]
        public void Petscii_FullTable_MatchesReferenceExceptFlaggedCells()
        {
            ShouldMatchExcept(
                new PetsciiEncoding(), new HashSet<int> { 0x7A, 0xB4, 0xE0, 0xF4 }, PetsciiHex);
        }

        // Flagged (conflict 18): 0x7A decodes to U+25C6 here vs U+2666 in
        // telnetlib3; 0xB4/0xF4 decode to U+2518 here vs U+2510 there; 0xE0
        // decodes to U+00E0 here vs U+00A0 there. Pinned as-is until the
        // owner decides.
        [Theory]
        [InlineData(0x7A, "\u25C6")]
        [InlineData(0xB4, "\u2518")]
        [InlineData(0xF4, "\u2518")]
        [InlineData(0xE0, "\u00E0")]
        public void Petscii_FlaggedCells_DecodeAsImplemented(int byteValue, string expected)
        {
            new PetsciiEncoding().GetString([(byte)byteValue]).Should().Be(expected);
        }

        // Encode vectors from test_petscii_codec.py.
        [Theory]
        [InlineData("hello", new byte[] { 0x48, 0x45, 0x4C, 0x4C, 0x4F })]
        [InlineData("HELLO", new byte[] { 0xC8, 0xC5, 0xCC, 0xCC, 0xCF })]
        [InlineData("0123456789", new byte[] { 0x30, 0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39 })]
        public void Petscii_Encode_KnownVectors(string text, byte[] expected)
        {
            new PetsciiEncoding().GetBytes(text).Should().Equal(expected);
        }

        private static readonly string[] AtaristHex =
        [
            "0000,0001,0002,0003,0004,0005,0006,0007,0008,0009,000A,000B,000C,000D,000E,000F",
            "0010,0011,0012,0013,0014,0015,0016,0017,0018,0019,001A,001B,001C,001D,001E,001F",
            "0020,0021,0022,0023,0024,0025,0026,0027,0028,0029,002A,002B,002C,002D,002E,002F",
            "0030,0031,0032,0033,0034,0035,0036,0037,0038,0039,003A,003B,003C,003D,003E,003F",
            "0040,0041,0042,0043,0044,0045,0046,0047,0048,0049,004A,004B,004C,004D,004E,004F",
            "0050,0051,0052,0053,0054,0055,0056,0057,0058,0059,005A,005B,005C,005D,005E,005F",
            "0060,0061,0062,0063,0064,0065,0066,0067,0068,0069,006A,006B,006C,006D,006E,006F",
            "0070,0071,0072,0073,0074,0075,0076,0077,0078,0079,007A,007B,007C,007D,007E,007F",
            "00C7,00FC,00E9,00E2,00E4,00E0,00E5,00E7,00EA,00EB,00E8,00EF,00EE,00EC,00C4,00C5",
            "00C9,00E6,00C6,00F4,00F6,00F2,00FB,00F9,00FF,00D6,00DC,00A2,00A3,00A5,00DF,0192",
            "00E1,00ED,00F3,00FA,00F1,00D1,00AA,00BA,00BF,2310,00AC,00BD,00BC,00A1,00AB,00BB",
            "00E3,00F5,00D8,00F8,0153,0152,00C0,00C3,00D5,00A8,00B4,2020,00B6,00A9,00AE,2122",
            "0133,0132,05D0,05D1,05D2,05D3,05D4,05D5,05D6,05D7,05D8,05D9,05DB,05DC,05DE,05E0",
            "05E1,05E2,05E4,05E6,05E7,05E8,05E9,05EA,05DF,05DA,05DD,05E3,05E5,00A7,2227,221E",
            "03B1,03B2,0393,03C0,03A3,03C3,00B5,03C4,03A6,0398,03A9,03B4,222E,03C6,2208,2229",
            "2261,00B1,2265,2264,2320,2321,00F7,2248,00B0,2219,00B7,221A,207F,00B2,00B3,00AF"
        ];

        // telnetlib3 DECODING_TABLE cells, all 256 (zero exclusions: the
        // tables were verified identical cell-for-cell).
        [Fact]
        public void Atarist_FullTable_MatchesReference()
        {
            ShouldMatchExcept(new AtaristEncoding(), new HashSet<int>(), AtaristHex);
        }

        // Lone-lead art table from test_big5bbs_codec.py: a lead byte that
        // cannot start a pair decodes as CP437 art.
        [Theory]
        [InlineData(0xA1, "í")]
        [InlineData(0xA2, "ó")]
        [InlineData(0xA8, "¿")]
        [InlineData(0xA9, "⌐")]
        [InlineData(0xAA, "¬")]
        [InlineData(0xAB, "½")]
        [InlineData(0xB0, "░")]
        [InlineData(0xB6, "╢")]
        [InlineData(0xBF, "┐")]
        [InlineData(0xC3, "├")]
        [InlineData(0xEE, "ε")]
        [InlineData(0xEF, "∩")]
        public void Big5Bbs_LoneLead_DecodesAsCp437Art(int lead, string expected)
        {
            // Each lead is followed by ESC so the pair path cannot apply
            // (mirrors the reference test setup).
            new Big5BbsEncoding().GetString([(byte)lead, 0x1B]).Should().Be(expected + "\u001B");
        }

        [Fact]
        public void Big5Bbs_CjkPhrase_RoundTrips()
        {
            var encoding = new Big5BbsEncoding();
            var bytes = encoding.GetBytes("夢想台灣");
            bytes.Should().HaveCount(8);
            encoding.GetString(bytes).Should().Be("夢想台灣");
        }

        [Fact]
        public void Big5Bbs_MixedArtAndEscape_RoundTrips()
        {
            // Mirrors the reference mixed stream: the ESC after ░ forces the
            // lone-lead path (B0 + 't' would otherwise decode as a Big5 pair).
            var encoding = new Big5BbsEncoding();
            var text = "夢░\u001B[32mtext";
            encoding.GetString(encoding.GetBytes(text)).Should().Be(text);
        }

        // Aliases the reference suite names that had no pin yet
        // (test_encoding.py / test_petscii_codec.py / test_big5bbs_codec.py).
        [Theory]
        [InlineData("cbm", typeof(PetsciiEncoding))]
        [InlineData("commodore", typeof(PetsciiEncoding))]
        [InlineData("c128", typeof(PetsciiEncoding))]
        [InlineData("big5-bbs", typeof(Big5BbsEncoding))]
        [InlineData("big5_pcman", typeof(Big5BbsEncoding))]
        [InlineData("big5_pcmanx", typeof(Big5BbsEncoding))]
        [InlineData("atari8bit", typeof(AtasciiEncoding))]
        [InlineData("atari_8bit", typeof(AtasciiEncoding))]
        public void Provider_ResolvesRemainingAliases(string name, Type expected)
        {
            TelnetEncodings.Register();
            Encoding.GetEncoding(name).Should().BeOfType(expected);
        }
    }
}
