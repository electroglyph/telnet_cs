namespace telnet_cs.Tests
{
    using System;
    using System.Collections.Generic;
    using FluentAssertions;
    using Xunit;
    using telnet_cs.Protocol;

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
