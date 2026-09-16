namespace telnet_cs.Tests
{
    using FluentAssertions;
    using Xunit;

    /// <summary>
    /// Pins <see cref="telnet_cs.Fuzz.FuzzSignature"/> stability: equal inputs
    /// hash equal, distinct shapes hash distinct, so the novelty tracker keys
    /// on behavior rather than noise.
    /// </summary>
    public class FuzzSignatureTests
    {
        [Fact]
        public void ForText_SameText_SameHash()
        {
            telnet_cs.Fuzz.FuzzSignature.ForText("xterm-256color")
                .Should().Be(telnet_cs.Fuzz.FuzzSignature.ForText("xterm-256color"));
        }

        [Fact]
        public void ForText_DistinctTerms_DistinctHashes()
        {
            telnet_cs.Fuzz.FuzzSignature.ForText("xterm")
                .Should().NotBe(telnet_cs.Fuzz.FuzzSignature.ForText("vt100"));
        }

        [Fact]
        public void ForBytes_ContentSensitive()
        {
            var a = telnet_cs.Fuzz.FuzzSignature.ForBytes([1, 2, 3]);
            telnet_cs.Fuzz.FuzzSignature.ForBytes([1, 2, 3]).Should().Be(a);
            telnet_cs.Fuzz.FuzzSignature.ForBytes([1, 2, 4]).Should().NotBe(a);
            telnet_cs.Fuzz.FuzzSignature.ForBytes([1, 2]).Should().NotBe(a);
        }

        [Fact]
        public void Mix_AccumulatesWithoutCollapsing()
        {
            var h = telnet_cs.Fuzz.FuzzSignature.Mix(0, 1);
            h = telnet_cs.Fuzz.FuzzSignature.Mix(h, 2);
            h.Should().NotBe(telnet_cs.Fuzz.FuzzSignature.Mix(0, 1));
            h.Should().NotBe(0);
        }

        [Fact]
        public void ForLongs_OrderSensitive()
        {
            telnet_cs.Fuzz.FuzzSignature.ForLongs(1, 2)
                .Should().NotBe(telnet_cs.Fuzz.FuzzSignature.ForLongs(2, 1));
        }
    }
}
