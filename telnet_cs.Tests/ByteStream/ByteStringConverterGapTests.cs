namespace telnet_cs.Tests
{
    using System;
    using FluentAssertions;
    using Xunit;
    using telnet_cs.IO;

    public class ByteStringConverterGapTests
    {
        [Fact]
        public void ConvertAsciiStringToBytes()
        {
            ByteStringConverter.ConvertStringToByteArray("ABC")
              .Should().Equal(new byte[] { 65, 66, 67 });
        }

        [Fact]
        public void ConvertEmptyStringToEmptyArray()
        {
            ByteStringConverter.ConvertStringToByteArray(string.Empty)
              .Should().BeEmpty();
        }

        [Fact]
        public void ToStringRoundTripsAscii()
        {
            var bytes = new byte[] { 65, 66, 67 };
            ByteStringConverter.ToString(bytes).Should().Be("ABC");
            ByteStringConverter.ToString(bytes, 1, 2).Should().Be("BC");
        }

        [Fact]
        public void RealIacIsEscapedByDoubling()
        {
            // PROPER (test.md §3.3): a real IAC byte (char)255 in the input must be
            // escaped by doubling it. Currently fails: the code replaces the literal
            // 4-char sequence "\0xFF" instead, and ASCII maps 255 -> '?' (63).
            var iac = ((char)255).ToString();
            var result = ByteStringConverter.ConvertStringToByteArray("a" + iac + "b");
            result.Should().Equal(new byte[] { (byte)'a', 255, 255, (byte)'b' });
        }

        [Fact]
        public void NulPlusXffSequencePassesThroughUnchanged()
        {
            // PROPER: the literal chars NUL + "xFF" are ordinary data, not an IAC
            // escape target. Currently fails: InvariantCulture Replace matches the
            // "xFF" part (NUL is ignorable in culture comparison) and splices in
            // the 8-char replacement (1+1+8+1=11 bytes).
            var needle = "\0xFF"; // 4 chars: \0, x, F, F
            needle.Should().HaveLength(4);
            var result = ByteStringConverter.ConvertStringToByteArray("a" + needle + "b");
            result.Should().Equal(new byte[]
            {
        (byte)'a', 0, (byte)'x', (byte)'F', (byte)'F', (byte)'b',
            });
        }

        [Fact]
        public void BareXffPassesThroughUnchanged()
        {
            // PROPER: a bare "xFF" (no NUL, no IAC byte) is ordinary text.
            // Currently fails on net6+: InvariantCulture comparison ignores the NUL
            // in the search value, so even the bare sequence is replaced.
            ByteStringConverter.ConvertStringToByteArray("axFFb").Should().Equal(
              new byte[] { (byte)'a', (byte)'x', (byte)'F', (byte)'F', (byte)'b' });
        }

        [Fact]
        public void ToStringPreservesFfBytes()
        {
            // PROPER (test.md §3.3): decoding must round-trip 0xFF (e.g. Latin-1)
            // and must not strip anything. Currently fails: ASCII decodes 0xFF to
            // '?' (63), so the .Trim((char)255) is a no-op over already-lost data.
            var s = ByteStringConverter.ToString(new byte[] { 255, 65, 255 });
            s.Should().Be("ÿAÿ");
        }

        [Theory]
        [InlineData(-1, 1)]
        [InlineData(0, -1)]
        [InlineData(4, 1)]
        [InlineData(3, 2)]
        [InlineData(0, 4)]
        public void ToString_InvalidOffsetCount_ThrowsArgumentOutOfRange(int offset, int count)
        {
            var act = () => ByteStringConverter.ToString(new byte[] { 65, 66, 67 }, offset, count);
            act.Should().Throw<ArgumentOutOfRangeException>();
        }
    }
}
