namespace telnet_cs.Tests
{
    using System;
    using System.Collections.Generic;
    using FluentAssertions;
    using Xunit;
    using telnet_cs.Protocol;

    /// <summary>
    /// MUD client detector notes: <c>MudClientDetector.IsMudClient</c>
    /// faithfully mirrors the reference <c>_is_maybe_mud</c>
    /// (fingerprinting.py:1132-1143: MUD_TERMINALS + GMCP/MSDP/MXP/MSP/
    /// ATCP/AARDWOLF only — no MTTS check, MSP not MSSP). The MTTS-prefix
    /// loop and MSSP-inclusive set live only in
    /// <c>fingerprinting_server_shell</c> (fingerprinting.py:1213-1221),
    /// a probing path this library does not implement. Asserting MTTS/MSSP
    /// here would bless divergence from <c>_is_maybe_mud</c>, not parity
    /// with it.
    /// </summary>
    public class MudClientDetectorTests
    {
        [Theory]
        [InlineData("Mudlet")]
        [InlineData("mushclient")]
        [InlineData("TinTin++")]
        [InlineData("ZMUD")]
        public void EffectiveTerm_KnownMudClient_Detected(string term)
        {
            MudClientDetector.IsMudClient(term, [], _ => false).Should().BeTrue();
        }

        [Fact]
        public void TtypeChain_MudNameInFirstThree_Detected()
        {
            MudClientDetector.IsMudClient("xterm", ["xterm", "Mudlet"], _ => false).Should().BeTrue();
        }

        [Fact]
        public void TtypeChain_MudNamePastThird_Ignored()
        {
            MudClientDetector.IsMudClient("xterm", ["a", "b", "c", "Mudlet"], _ => false).Should().BeFalse();
        }

        [Fact]
        public void RemoteMudOption_Detected()
        {
            MudClientDetector.IsMudClient("xterm", ["xterm"], o => o == (int)Options.Gmcp).Should().BeTrue();
        }

        [Theory]
        [InlineData("xterm")]
        [InlineData("ANSI")]
        [InlineData(null)]
        public void OrdinaryClient_NotDetected(string? term)
        {
            IReadOnlyList<string> chain = term is null ? [] : [term];
            MudClientDetector.IsMudClient(term, chain, _ => false).Should().BeFalse();
        }
    }
}
