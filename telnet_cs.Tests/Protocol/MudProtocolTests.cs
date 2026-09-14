// Pure codec tests for the MUD framing helpers (MSDP 69, MSSP 70, GMCP 201,
// ZMP 93, ATCP 200, Aardwolf 102): every assertion pins wire bytes, mirroring
// telnetlib3's test_mud.py ground truth.
namespace telnet_cs.Tests
{
    using System.Collections.Generic;
    using System.Text.Json;
    using FluentAssertions;
    using Xunit;
    using telnet_cs.Protocol;

    public class MudProtocolTests
    {
        [Fact]
        public void GmcpEncode_WithoutData_IsPackageOnly()
        {
            MudProtocol.GmcpEncode("Core.Hello").Should().Equal(
              (byte)'C', (byte)'o', (byte)'r', (byte)'e', (byte)'.', (byte)'H', (byte)'e', (byte)'l', (byte)'l', (byte)'o');
        }

        [Fact]
        public void GmcpEncodeData_SerializesCompactJson()
        {
            var encoded = MudProtocol.GmcpEncodeData("Char.Vitals", new Dictionary<string, object?> { ["hp"] = 100, ["maxhp"] = 120 });
            var (package, data) = MudProtocol.GmcpDecode(encoded);
            package.Should().Be("Char.Vitals");
            data.Should().NotBeNull();
            data!["hp"]!.GetValue<int>().Should().Be(100);
            data!["maxhp"]!.GetValue<int>().Should().Be(120);
        }

        [Fact]
        public void GmcpEncodeData_NestedStructures_RoundTrip()
        {
            var items = new List<object?> { new Dictionary<string, object?> { ["name"] = "sword" } };
            var encoded = MudProtocol.GmcpEncodeData("Room.Info", new Dictionary<string, object?> { ["exits"] = new List<object?> { "north" }, ["items"] = items });
            var (package, data) = MudProtocol.GmcpDecode(encoded);
            package.Should().Be("Room.Info");
            data!["exits"]![0]!.GetValue<string>().Should().Be("north");
            data!["items"]![0]!["name"]!.GetValue<string>().Should().Be("sword");
        }

        [Fact]
        public void GmcpDecode_WithoutSpace_DataIsNull()
        {
            var (package, data) = MudProtocol.GmcpDecode("Room.Info"u8);
            package.Should().Be("Room.Info");
            data.Should().BeNull();
        }

        [Fact]
        public void GmcpDecode_TrailingSpace_DataIsNull()
        {
            var (package, data) = MudProtocol.GmcpDecode("Char.Vitals "u8);
            package.Should().Be("Char.Vitals");
            data.Should().BeNull();
        }

        [Fact]
        public void GmcpDecode_InvalidJson_Throws()
        {
            // Malformed GMCP JSON surfaces as ArgumentException wrapping the
            // JSON error (reference gmcp_decode raises ValueError wrapping
            // JSONDecodeError), not a bare JsonException.
            var decode = () => MudProtocol.GmcpDecode("Package {bad json}"u8);
            decode.Should().Throw<ArgumentException>();
        }

        [Fact]
        public void GmcpDecode_Latin1Fallback_DecodesHighBytes()
        {
            var (package, data) = MudProtocol.GmcpDecode([(byte)'C', (byte)'a', (byte)'f', 0xE9]);
            package.Should().Be("Caf\u00E9");
            data.Should().BeNull();
        }

        [Fact]
        public void MsdpEncode_Decode_RoundTripsFlatPairs()
        {
            var values = new Dictionary<string, object?>(StringComparer.Ordinal)
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
        public void MsdpEncode_NestedTable_RoundTrips()
        {
            var values = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["ROOM"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["NAME"] = "Inn",
                    ["EXITS"] = "north,south",
                },
            };
            var encoded = MudProtocol.MsdpEncode(values);
            encoded.Should().Contain(MudProtocol.MsdpTableOpen);
            encoded.Should().Contain(MudProtocol.MsdpTableClose);
            MudProtocol.MsdpDecode(encoded).Should().BeEquivalentTo(values);
        }

        [Fact]
        public void MsdpEncode_Array_RoundTrips()
        {
            var values = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["LIST"] = new List<object?> { "a", "b", "c" },
            };
            var encoded = MudProtocol.MsdpEncode(values);
            encoded.Should().Contain(MudProtocol.MsdpArrayOpen);
            encoded.Should().Contain(MudProtocol.MsdpArrayClose);
            MudProtocol.MsdpDecode(encoded).Should().BeEquivalentTo(values);
        }

        [Fact]
        public void MsdpEncode_EmptyValue_RoundTrips()
        {
            var values = new Dictionary<string, object?>(StringComparer.Ordinal) { ["KEY"] = string.Empty };
            MudProtocol.MsdpDecode(MudProtocol.MsdpEncode(values)).Should().BeEquivalentTo(values);
        }

        [Fact]
        public void MsdpDecode_SkipsGarbageBytes()
        {
            var decoded = MudProtocol.MsdpDecode([0x42, 1, (byte)'K', (byte)'E', (byte)'Y', 2, (byte)'v', (byte)'a', (byte)'l']);
            decoded.Should().BeEquivalentTo(new Dictionary<string, object?> { ["KEY"] = "val" });
        }

        [Fact]
        public void MsdpDecode_Latin1Fallback_DecodesHighBytes()
        {
            var decoded = MudProtocol.MsdpDecode([1, (byte)'K', (byte)'E', (byte)'Y', 2, (byte)'C', (byte)'a', (byte)'f', 0xE9]);
            decoded.Should().BeEquivalentTo(new Dictionary<string, object?> { ["KEY"] = "Caf\u00E9" });
        }

        [Fact]
        public void MsspEncode_Decode_RoundTripsSingleAndMultiValues()
        {
            var values = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["NAME"] = "Mud",
                ["PLAYERS"] = new List<string> { "10", "20" },
            };
            var encoded = MudProtocol.MsspEncode(values);
            encoded.Should().Equal(
              1, (byte)'N', (byte)'A', (byte)'M', (byte)'E', 2, (byte)'M', (byte)'u', (byte)'d',
              1, (byte)'P', (byte)'L', (byte)'A', (byte)'Y', (byte)'E', (byte)'R', (byte)'S',
              2, (byte)'1', (byte)'0', 2, (byte)'2', (byte)'0');
            var decoded = MudProtocol.MsspDecode(encoded);
            decoded["NAME"].Should().Be("Mud");
            decoded["PLAYERS"].Should().BeAssignableTo<IReadOnlyList<string>>()
                .Subject.Should().Equal("10", "20");
        }

        [Fact]
        public void MsspDecode_SingleValue_DecodesString()
        {
            var decoded = MudProtocol.MsspDecode([1, (byte)'S', (byte)'I', (byte)'N', (byte)'G', (byte)'L', (byte)'E', 2, (byte)'o', (byte)'n', (byte)'e']);
            decoded["SINGLE"].Should().Be("one");
        }

        [Fact]
        public void MsspDecode_RepeatedVar_MergesToList()
        {
            var decoded = MudProtocol.MsspDecode(
              [1, (byte)'P', (byte)'O', (byte)'R', (byte)'T', 2, (byte)'6', (byte)'0', (byte)'2', (byte)'3',
               1, (byte)'P', (byte)'O', (byte)'R', (byte)'T', 2, (byte)'6', (byte)'0', (byte)'2', (byte)'4']);
            decoded["PORT"].Should().BeAssignableTo<IReadOnlyList<string>>()
                .Subject.Should().Equal("6023", "6024");
        }

        [Fact]
        public void MsspDecode_VarWithoutVal_IsDropped()
        {
            var decoded = MudProtocol.MsspDecode([1, (byte)'A']);
            decoded.Should().BeEmpty();
        }

        [Fact]
        public void MsspDecode_SkipsGarbageBytes()
        {
            var decoded = MudProtocol.MsspDecode([0x42, 1, (byte)'N', (byte)'A', (byte)'M', (byte)'E', 2, (byte)'M', (byte)'u', (byte)'d']);
            decoded.Should().BeEquivalentTo(new Dictionary<string, object> { ["NAME"] = "Mud" });
        }

        [Fact]
        public void MsspDecode_Latin1Fallback_DecodesHighBytes()
        {
            var decoded = MudProtocol.MsspDecode([1, (byte)'N', (byte)'A', (byte)'M', (byte)'E', 2, 0xC9, (byte)'t', (byte)'o', (byte)'i', (byte)'l', (byte)'e']);
            decoded["NAME"].Should().Be("\u00C9toile");
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
        public void ZmpDecode_EmptyPayload_DecodesEmptyList()
        {
            MudProtocol.ZmpDecode([]).Should().BeEmpty();
        }

        [Fact]
        public void ZmpDecode_WithoutTrailingNul_KeepsLastArg()
        {
            MudProtocol.ZmpDecode("zmp.check\x00zmp.ping"u8).Should().Equal("zmp.check", "zmp.ping");
        }

        [Fact]
        public void ZmpDecode_Latin1Fallback_DecodesHighBytes()
        {
            var decoded = MudProtocol.ZmpDecode([(byte)'e', (byte)'c', (byte)'h', (byte)'o', 0, (byte)'c', (byte)'a', (byte)'f', 0xE9, 0]);
            decoded.Should().Equal("echo", "caf\u00E9");
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
        public void AtcpDecode_EmptyPayload_DecodesEmptyPair()
        {
            var (package, value) = MudProtocol.AtcpDecode([]);
            package.Should().BeEmpty();
            value.Should().BeEmpty();
        }

        [Fact]
        public void AtcpDecode_Latin1Fallback_DecodesHighBytes()
        {
            var (package, value) = MudProtocol.AtcpDecode([(byte)'R', (byte)'o', (byte)'o', (byte)'m', 0x20, (byte)'C', (byte)'a', (byte)'f', 0xE9]);
            package.Should().Be("Room");
            value.Should().Be("Caf\u00E9");
        }

        [Fact]
        public void AardwolfDecode_KnownChannel_MapsNameAndDataByte()
        {
            var message = MudProtocol.AardwolfDecode([100, 3]);
            message.Channel.Should().Be("status");
            message.ChannelByte.Should().Be(100);
            message.DataByte.Should().Be(3);
        }

        [Fact]
        public void AardwolfDecode_UnknownChannel_FormatsHexName()
        {
            var message = MudProtocol.AardwolfDecode([200, 5]);
            message.Channel.Should().Be("0xc8");
            message.ChannelByte.Should().Be(200);
            message.DataByte.Should().Be(5);
        }

        [Fact]
        public void AardwolfDecode_EmptyPayload_DecodesUnknownChannel()
        {
            var message = MudProtocol.AardwolfDecode([]);
            message.Channel.Should().Be("unknown");
            message.ChannelByte.Should().Be(0);
            message.DataByte.Should().BeNull();
            message.DataBytes.Should().BeEmpty();
        }

        [Fact]
        public void AardwolfDecode_SingleByte_HasNoDataByte()
        {
            var message = MudProtocol.AardwolfDecode([102]);
            message.Channel.Should().Be("affect");
            message.DataByte.Should().BeNull();
            message.DataBytes.Should().BeEmpty();
        }

        [Fact]
        public void AardwolfDecode_LongPayload_CarriesDataBytesOnly()
        {
            var message = MudProtocol.AardwolfDecode([100, 3, 4, 5]);
            message.Channel.Should().Be("status");
            message.DataByte.Should().BeNull();
            message.DataBytes.Should().Equal(3, 4, 5);
        }

        [Fact]
        public void CharsetSelect_PrefersConfiguredOrder()
        {
            CharsetProtocol.SelectSupported(["UTF-8", "US-ASCII"]).Should().Be("UTF-8");
            CharsetProtocol.SelectSupported(["BOGUS-ENCODING"]).Should().BeNull();
        }

        [Fact]
        public void CharsetSelect_SkipsIllegalOffers()
        {
            CharsetProtocol.SelectSupported(["illegal", "UTF-8"]).Should().Be("UTF-8");
            CharsetProtocol.SelectSupported(["illegal", "this-is-no-good-either"]).Should().BeNull();
        }

        [Fact]
        public void CharsetSelect_ExplicitPreference_ExactMatchOrReject()
        {
            CharsetProtocol.SelectSupported(["UTF-8", "US-ASCII"], "utf-8").Should().Be("UTF-8");
            CharsetProtocol.SelectSupported(["UTF-8", "US-ASCII"], "utf-16").Should().BeNull();
        }

        [Fact]
        public void CharsetSelect_WeakDefaultAcceptsFirstViable()
        {
            CharsetProtocol.SelectSupported(["UTF-8"], "iso-8859-1").Should().Be("UTF-8");
            CharsetProtocol.SelectSupported(["UTF-8"], "latin1").Should().Be("UTF-8");
        }

        [Fact]
        public void CharsetSelect_NormalizesNames()
        {
            CharsetProtocol.SelectSupported(["US ASCII"], null).Should().Be("US ASCII");
            CharsetProtocol.SelectSupported(["ISO-8859-01"], null).Should().Be("ISO-8859-01");
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
