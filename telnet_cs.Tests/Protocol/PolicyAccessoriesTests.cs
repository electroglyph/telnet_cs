namespace telnet_cs.Tests
{
    using System;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using FluentAssertions;
    using Xunit;
    using telnet_cs.Encodings;
    using telnet_cs.IO;
    using telnet_cs.Protocol;

    public class PolicyAccessoriesTests
    {
        [Fact]
        public void RetroCodecs_ResolveWithoutExplicitRegister()
        {
            Encoding.GetEncoding("atascii").GetString([(byte)0x9B]).Should().Be("\n");
            Encoding.GetEncoding("PETSCII").GetString([(byte)0x41]).Should().Be("a");
        }

        [Fact]
        public void CodePages_ResolveThroughAutoRegister()
        {
            Encoding.GetEncoding("cp437").GetString([(byte)0x41]).Should().Be("A");
        }

        [Theory]
        [InlineData("en_US.UTF-8@misc", "UTF-8")]
        [InlineData("C.UTF-8", "UTF-8")]
        [InlineData("tr_TR.ISO-8859-9", "ISO-8859-9")]
        [InlineData("de_DE.iso-8859-1@euro", "iso-8859-1")]
        [InlineData("abc.def", "def")]
        [InlineData(".def@ghi", "def")]
        public void EncodingFromLang_WithSuffix_ReturnsEncoding(string lang, string expected)
        {
            TelnetAccessories.EncodingFromLang(lang).Should().Be(expected);
        }

        [Theory]
        [InlineData("en_IL")]
        [InlineData("C")]
        [InlineData("UTF-8")]
        [InlineData("POSIX")]
        [InlineData("")]
        [InlineData(null)]
        public void EncodingFromLang_WithoutSuffix_ReturnsNull(string? lang)
        {
            TelnetAccessories.EncodingFromLang(lang).Should().BeNull();
        }

        [Fact]
        public void Hexdump_ShortRow_PinsExactLayout()
        {
            var data = Encoding.ASCII.GetBytes("Hello World\r\n");
            var expected = "00000000  48 65 6c 6c 6f 20 57 6f  72 6c 64 0d 0a" + new string(' ', 11) + "|Hello World..|";
            TelnetAccessories.Hexdump(data).Should().Be(expected);
        }

        [Fact]
        public void Hexdump_Empty_YieldsEmptyString()
        {
            TelnetAccessories.Hexdump(ReadOnlySpan<byte>.Empty).Should().BeEmpty();
        }

        [Fact]
        public void Hexdump_SplitRow_PinsOffsetsAndPrefix()
        {
            var data = new byte[17];
            for (var i = 0; i < data.Length; i++)
            {
                data[i] = (byte)i;
            }

            var first = ">>  00000000  00 01 02 03 04 05 06 07  08 09 0a 0b 0c 0d 0e 0f  |................|";
            var second = ">>  00000010  10" + new string(' ', 21) + "  " + new string(' ', 23) + "  |.|";
            TelnetAccessories.Hexdump(data, ">>  ").Should().Be(first + "\n" + second);
        }

        [Theory]
        [InlineData(new byte[] { 0x1B, (byte)'[', (byte)'0', (byte)';', (byte)'3', (byte)'2', 0x20, (byte)'D' }, "petscii")]
        [InlineData(new byte[] { 0x1B, (byte)'[', (byte)'5', (byte)';', (byte)'3', (byte)'6', 0x20, (byte)'D' }, "atascii")]
        [InlineData(new byte[] { 0x1B, (byte)'[', (byte)'0', (byte)';', (byte)'0', 0x20, (byte)'D' }, "cp437")]
        [InlineData(new byte[] { 0x1B, (byte)'[', (byte)'0', (byte)';', (byte)'8', 0x20, (byte)'D' }, "iso-8859-8")]
        [InlineData(new byte[] { 0x1B, (byte)'[', (byte)'0', (byte)';', (byte)'1', (byte)'4', 0x20, (byte)'D' }, "iso-8859-5")]
        [InlineData(new byte[] { 0x1B, (byte)'[', (byte)'0', (byte)';', (byte)'2', (byte)'1', 0x20, (byte)'D' }, "iso-8859-7")]
        [InlineData(new byte[] { 0x1B, (byte)'[', (byte)'0', (byte)';', (byte)'3', (byte)'1', 0x20, (byte)'D' }, "cp1131")]
        [InlineData(new byte[] { 0x1B, (byte)'[', (byte)'0', (byte)';', (byte)'4', (byte)'2', 0x20, (byte)'D' }, "cp437")]
        public void SyncTermFont_DetectEncoding_MapsFontId(byte[] data, string expected)
        {
            SyncTermFont.DetectEncoding(data).Should().Be(expected);
        }

        [Theory]
        [InlineData(new byte[] { 0x1B, (byte)'[', (byte)'0', (byte)';', (byte)'7', 0x20, (byte)'D' })]
        [InlineData(new byte[] { 0x1B, (byte)'[', (byte)'0', (byte)';', (byte)'1', (byte)'5', 0x20, (byte)'D' })]
        [InlineData(new byte[] { 0x1B, (byte)'[', (byte)'0', (byte)';', (byte)'1', (byte)'9', 0x20, (byte)'D' })]
        [InlineData(new byte[] { 0x1B, (byte)'[', (byte)'0', (byte)';', (byte)'2', (byte)'8', 0x20, (byte)'D' })]
        [InlineData(new byte[] { 0x1B, (byte)'[', (byte)'0', (byte)';', (byte)'4', (byte)'3', 0x20, (byte)'D' })]
        [InlineData(new byte[] { (byte)'h', (byte)'e', (byte)'l', (byte)'l', (byte)'o' })]
        [InlineData(new byte[] { 0x1B, (byte)'[', (byte)'0', (byte)';' })]
        [InlineData(new byte[] { 0x1B, (byte)'[', (byte)'0', (byte)';', (byte)'1', (byte)'0', (byte)'0', (byte)'0', (byte)'0', 0x20, (byte)'D' })]
        public void SyncTermFont_DetectEncoding_NoMatch_ReturnsNull(byte[] data)
        {
            SyncTermFont.DetectEncoding(data).Should().BeNull();
        }

        [Fact]
        public void SyncTermFont_DetectEncoding_ScansPastMalformedSequence()
        {
            // A truncated "0;3" (no " D" terminator) is skipped; the later
            // well-formed "0;32 D" still resolves.
            var data = new byte[]
            {
                0x1B, (byte)'[', (byte)'0', (byte)';', (byte)'3',
                0x1B, (byte)'[', (byte)'0', (byte)';', (byte)'3', (byte)'2', 0x20, (byte)'D',
            };
            SyncTermFont.DetectEncoding(data).Should().Be("petscii");
        }

        [Fact]
        public void SyncTermFont_DetectEncoding_SkipsOversizedId()
        {
            // Font ids are capped at four digits: a five-digit id is
            // skipped, so a later well-formed sequence still resolves.
            var data = new byte[]
            {
                0x1B, (byte)'[', (byte)'0', (byte)';', (byte)'1', (byte)'0', (byte)'0', (byte)'0', (byte)'0', 0x20, (byte)'D',
                0x1B, (byte)'[', (byte)'0', (byte)';', (byte)'3', (byte)'2', 0x20, (byte)'D',
            };
            SyncTermFont.DetectEncoding(data).Should().Be("petscii");
        }

        [Fact]
        public void SyncTermFont_ResolveEncoding_Unknown_ReturnsNull()
        {
            SyncTermFont.ResolveEncoding("no-such-codec-xyz").Should().BeNull();
        }

        [Fact]
        public void SyncTermFont_ResolveEncoding_Known_ReturnsEncoding()
        {
            SyncTermFont.ResolveEncoding("petscii").Should().NotBeNull();
            SyncTermFont.ResolveEncoding("cp437").Should().NotBeNull();
        }

        [Fact]
        public async Task ReadPath_SyncTermSequence_SwitchesEncodingAndFiresHook()
        {
            string? detected = null;
            using var stream = new ScriptedStream(27, 91, 48, 59, 51, 54, 32, 68);
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1);
            sut.SyncTermFontDetected += name => detected = name;
            await sut.ReadAsync(TimeSpan.FromMilliseconds(50));
            detected.Should().Be("atascii");
            sut.TextEncoding.Should().BeOfType<AtasciiEncoding>();
        }

        [Fact]
        public async Task ReadPath_SyncTermSequence_RespectsExplicitEncoding()
        {
            var fired = 0;
            using var stream = new ScriptedStream(27, 91, 48, 59, 51, 54, 32, 68);
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1);
            sut.TextEncoding = Encoding.Latin1;
            sut.SyncTermFontDetected += _ => fired++;
            await sut.ReadAsync(TimeSpan.FromMilliseconds(50));
            fired.Should().Be(0);
            sut.TextEncoding.Should().BeSameAs(Encoding.Latin1);
        }
    }
}
