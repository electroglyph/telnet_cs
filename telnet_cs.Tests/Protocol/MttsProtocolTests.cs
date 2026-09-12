// Unit tests for MttsProtocol: the "MTTS <bitvector>" third TTYPE answer
// parses to its numeric vector (docs/mud-protocols/mtts.md capability
// table); the effective terminal type stays the second chain entry (pinned
// in ExtendedCollectorsTests/ServerSessionTests).
namespace telnet_cs.Tests
{
    using FluentAssertions;
    using telnet_cs.Protocol;
    using Xunit;

    public class MttsProtocolTests
    {
        [Theory]
        // 137 = Ansi(1) + Colors256(8) + Proxy(128).
        [InlineData("MTTS 137", 137)]
        // 13 = Ansi(1) + Utf8(4) + Colors256(8), the spec's own example.
        [InlineData("MTTS 13", 13)]
        [InlineData("MTTS 0", 0)]
        [InlineData("  MTTS 7  ", 7)]
        public void TryParseBitvector_ValidEntries_Parse(string entry, int expected)
        {
            MttsProtocol.TryParseBitvector(entry, out var bitvector).Should().BeTrue();
            bitvector.Should().Be(expected);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("XTERM")]
        [InlineData("MTTS ")]
        [InlineData("MTTS abc")]
        [InlineData("MTTS 1.5")]
        [InlineData("MTTS -1")]
        [InlineData("MTTS 0x10")]
        public void TryParseBitvector_InvalidEntries_Fail(string? entry)
        {
            MttsProtocol.TryParseBitvector(entry, out var bitvector).Should().BeFalse();
            bitvector.Should().Be(0);
        }

        [Fact]
        public void Flags_DecomposeDocumentedExample()
        {
            var flags = (MttsCapabilities)137;
            flags.Should().Be(MttsCapabilities.Ansi | MttsCapabilities.Colors256 | MttsCapabilities.Proxy);
            ((MttsCapabilities)13).Should().Be(
                MttsCapabilities.Ansi | MttsCapabilities.Utf8 | MttsCapabilities.Colors256);
        }
    }
}
