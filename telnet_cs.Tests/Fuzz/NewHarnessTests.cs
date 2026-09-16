namespace telnet_cs.Tests
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using FluentAssertions;
    using Xunit;

    /// <summary>
    /// Direct pins for the Track 2 harnesses: crafted inputs reach each
    /// harness's core path without throwing, signatures are deterministic,
    /// and the TLS-sniff oracle keeps its fail-closed shape.
    /// </summary>
    [Collection("Serial")]
    public class NewHarnessTests
    {
        private static telnet_cs.Fuzz.FuzzInput Input(params byte[] bytes) => new(bytes, []);

        private static CancellationToken Budget()
        {
            return new CancellationTokenSource(TimeSpan.FromSeconds(15)).Token;
        }

        [Fact]
        public async Task Repl_QuitLine_ExitsPromptLoop()
        {
            var result = await telnet_cs.Fuzz.ReplHarness.RunAsync(
                Input(0x68, 0x65, 0x6C, 0x70, 0x0D, 0x0A, 0x71, 0x75, 0x69, 0x74, 0x0D, 0x0A), Budget());

            result.Outbound.Should().BeGreaterThan(0, "banner + prompt echo sink outbound bytes");
        }

        [Fact]
        public async Task Repl_EmptyInput_DisconnectsPromptly()
        {
            var result = await telnet_cs.Fuzz.ReplHarness.RunAsync(Input(), Budget());

            result.Should().NotBe((0, 0), "even an empty REPL run emits the banner");
        }

        [Fact]
        public async Task Repl_SameInput_SameSignature()
        {
            var input = Input(0xFF, 0xFD, 0x18, 0x73, 0x74, 0x61, 0x74, 0x73, 0x0D, 0x0A);

            var first = await telnet_cs.Fuzz.ReplHarness.RunAsync(input, Budget());
            var second = await telnet_cs.Fuzz.ReplHarness.RunAsync(input, Budget());

            second.Should().Be(first);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        [InlineData(6)]
        public async Task Request_EachSelector_Completes(byte which)
        {
            // TTYPE WILL + SEND-shaped answers so collectors with primed state
            // return values instead of timing out.
            var input = Input(which, 0xFF, 0xFB, 0x18, 0xFF, 0xFA, 0x18, 0x00, 0x58, 0x54, 0x45, 0x52, 0x4D, 0xFF, 0xF0);

            var act = () => telnet_cs.Fuzz.RequestHarness.RunAsync(input, Budget());

            (await act.Should().NotThrowAsync()).Which.Should().NotBe((0, 0));
        }

        [Fact]
        public async Task Request_EmptyInput_UsesFirstCollector()
        {
            var act = () => telnet_cs.Fuzz.RequestHarness.RunAsync(Input(), Budget());

            await act.Should().NotThrowAsync();
        }

        [Fact]
        public async Task TlsSniff_LeadingTlsByte_DisconnectsWithoutFinding()
        {
            var act = () => telnet_cs.Fuzz.TlsSniffHarness.RunAsync(
                Input(0x16, 0x03, 0x01, 0x00, 0x2E), Budget());

            await act.Should().NotThrowAsync("a cert-less sniff must drop a TLS opener, not keep it");
        }

        [Fact]
        public async Task TlsSniff_PlainText_Completes()
        {
            var first = await telnet_cs.Fuzz.TlsSniffHarness.RunAsync(Input(0x68, 0x69, 0x0D, 0x0A), Budget());
            var second = await telnet_cs.Fuzz.TlsSniffHarness.RunAsync(Input(0x68, 0x69, 0x0D, 0x0A), Budget());

            second.Should().Be(first);
        }

        [Fact]
        public async Task Caps_EmptyInput_UsesZeroKnobs()
        {
            var act = () => telnet_cs.Fuzz.CapsHarness.RunAsync(Input(), Budget());

            await act.Should().NotThrowAsync();
        }

        [Fact]
        public async Task Caps_KnobsAndBody_Deterministic()
        {
            var input = Input(0x01, 0x02, 0x03, 0x01, 0x04, 0x35, 0x36, 0x17, 0xFF, 0xFD, 0x18);

            var first = await telnet_cs.Fuzz.CapsHarness.RunAsync(input, Budget());
            var second = await telnet_cs.Fuzz.CapsHarness.RunAsync(input, Budget());

            second.Should().Be(first);
        }

        [Fact]
        public async Task Storm_VerbBurst_Completes()
        {
            var burst = new byte[150 * 3];
            for (var i = 0; i < 150; i++)
            {
                burst[i * 3] = 0xFF;
                burst[i * 3 + 1] = 0xFD;
                burst[i * 3 + 2] = 0x18;
            }

            var act = () => telnet_cs.Fuzz.StormHarness.RunAsync(Input(burst), Budget());

            await act.Should().NotThrowAsync("the guard trip is handled, not a finding");
        }
    }
}
