// Pure codec tests for the MUD framing helpers (MSDP 69, MSSP 70, GMCP 201,
// ZMP 93, ATCP 200, Aardwolf 102): every assertion pins wire bytes.
namespace telnet_cs.Tests
{
    using System;
    using System.Collections.Generic;
    using FluentAssertions;
    using Xunit;
    using telnet_cs.Protocol;

    public class MudProtocolTests
    {
        [Fact]
        public void GmcpEncode_WithoutJson_IsPackageOnly()
        {
            MudProtocol.GmcpEncode("Core.Hello").Should().Equal(
              (byte)'C', (byte)'o', (byte)'r', (byte)'e', (byte)'.', (byte)'H', (byte)'e', (byte)'l', (byte)'l', (byte)'o');
        }

        [Fact]
        public void GmcpEncode_WithJson_JoinsWithSingleSpace()
        {
            var (package, json) = MudProtocol.GmcpDecode(MudProtocol.GmcpEncode("Core.Hello", """{"a":1}"""));
            package.Should().Be("Core.Hello");
            json.Should().Be("""{"a":1}""");
        }

        [Fact]
        public void GmcpDecode_WithoutSpace_HasNullJson()
        {
            var (package, json) = MudProtocol.GmcpDecode("Room.Info"u8);
            package.Should().Be("Room.Info");
            json.Should().BeNull();
        }

        [Fact]
        public void MsdpEncode_Decode_RoundTripsFlatPairs()
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["HP"] = "100",
                ["MANA"] = "50",
            };
            var encoded = MudProtocol.MsdpEncode(values);
            encoded.Should().Equal(
              1, (byte)'H', (byte)'P', 2, (byte)'1', (byte)'0', (byte)'0',
              1, (byte)'M', (byte)'A', (byte)'N', (byte)'A', 2, (byte)'5', (byte)'0');
            MudProtocol.MsdpDecode(encoded).Should().BeEquivalentTo(values);
        }

        [Fact]
        public void MsspEncode_Decode_RoundTripsSingleAndMultiValues()
        {
            var values = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            {
                ["NAME"] = ["Mud"],
                ["PLAYERS"] = ["10", "20"],
            };
            var encoded = MudProtocol.MsspEncode(values);
            encoded.Should().Equal(
              1, (byte)'N', (byte)'A', (byte)'M', (byte)'E', 2, (byte)'M', (byte)'u', (byte)'d',
              1, (byte)'P', (byte)'L', (byte)'A', (byte)'Y', (byte)'E', (byte)'R', (byte)'S',
              2, (byte)'1', (byte)'0', 2, (byte)'2', (byte)'0');
            var decoded = MudProtocol.MsspDecode(encoded);
            decoded["NAME"].Should().Equal("Mud");
            decoded["PLAYERS"].Should().Equal("10", "20");
        }

        [Fact]
        public void MsspDecode_ValuelessVariable_DecodesEmptyList()
        {
            var decoded = MudProtocol.MsspDecode([1, (byte)'A']);
            decoded.Should().ContainKey("A");
            decoded["A"].Should().BeEmpty();
        }

        [Fact]
        public void ZmpEncode_Decode_RoundTripsCommandAndArgs()
        {
            var encoded = MudProtocol.ZmpEncode("cmd", "a", "b");
            encoded.Should().Equal(
              (byte)'c', (byte)'m', (byte)'d', 0, (byte)'a', 0, (byte)'b', 0);
            MudProtocol.ZmpDecode(encoded).Should().Equal("cmd", "a", "b");
        }

        [Fact]
        public void AtcpDecode_WithoutSpace_ValueIsEmpty()
        {
            var (package, value) = MudProtocol.AtcpDecode("Auth.Request"u8);
            package.Should().Be("Auth.Request");
            value.Should().BeEmpty();
        }

        [Fact]
        public void AtcpDecode_WithSpace_SplitsOnFirstSpace()
        {
            var (package, value) = MudProtocol.AtcpDecode("Room.Brief a b"u8);
            package.Should().Be("Room.Brief");
            value.Should().Be("a b");
        }

        [Fact]
        public void AardwolfDecode_SplitsChannelFromData()
        {
            var (channel, data) = MudProtocol.AardwolfDecode([100, 1, 2]);
            channel.Should().Be(100);
            data.Should().Equal(1, 2);
        }

        [Fact]
        public void AardwolfDecode_EmptyPayload_Throws()
        {
            Action decode = () => MudProtocol.AardwolfDecode([]);
            decode.Should().Throw<ArgumentException>();
        }

        [Fact]
        public void CharsetSelect_PrefersConfiguredOrder()
        {
            CharsetProtocol.SelectSupported(["UTF-8", "US-ASCII"]).Should().Be("UTF-8");
            CharsetProtocol.SelectSupported(["BOGUS-ENCODING"]).Should().BeNull();
        }

        [Fact]
        public void CharsetRequest_RoundTripsOffers()
        {
            var payload = CharsetProtocol.BuildRequest(["UTF-8", "US-ASCII"]);
            payload[0].Should().Be(CharsetProtocol.Request);
            CharsetProtocol.ParseRequest(payload).Should().Equal("UTF-8", "US-ASCII");
        }
    }
}
