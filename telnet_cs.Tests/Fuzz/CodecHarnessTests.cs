namespace telnet_cs.Tests
{
    using System.Threading;
    using System.Threading.Tasks;
    using FluentAssertions;
    using Xunit;

    /// <summary>
    /// Pins the GMCP round-trip oracle's input contract: fuzz bytes whose
    /// Latin-1 text contains a space would form a package the wire format
    /// cannot encode (<c>package SP JSON</c> split at the first space), so the
    /// harness sanitizes before asserting instead of reporting a codec bug.
    /// </summary>
    public class CodecHarnessTests
    {
        [Theory]
        [InlineData(new byte[] { 0xE3, 0x20, 0x5A })]
        [InlineData(new byte[] { 0x41, 0x20, 0x42 })]
        [InlineData(new byte[] { 0x20 })]
        public async Task Run_PackageWithInteriorSpace_Completes(byte[] bytes)
        {
            var act = () => telnet_cs.Fuzz.CodecHarness.RunAsync(
                new telnet_cs.Fuzz.FuzzInput(bytes, []), CancellationToken.None);

            await act.Should().NotThrowAsync("a space in the derived package is caller-shaped input, not a codec bug");
        }
    }
}
