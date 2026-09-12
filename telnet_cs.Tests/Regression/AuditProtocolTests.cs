namespace telnet_cs.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Threading.Tasks;
    using FluentAssertions;
    using Xunit;
    using telnet_cs.Protocol;

    public class AuditProtocolTests
    {
        private const byte Var = 1;
        private const byte Val = 2;
        private const byte TableOpen = 3;
        private const byte TableClose = 4;

        [Fact]
        public async Task MsdpDecode_TableGarbage_CompletesInsteadOfHanging()
        {
            var payload = new byte[] { Var, (byte)'K', Val, TableOpen, 0x42, TableClose };
            var task = Task.Run(() => MudProtocol.MsdpDecode(payload));
            var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(3)));
            completed.Should().Be(task);
        }

        [Fact]
        public void MsdpDecode_TableKeyWithoutVal_IsDroppedLikeTopLevel()
        {
            var payload = new byte[] { Var, (byte)'A', Val, TableOpen, Var, (byte)'K', TableClose };
            var result = MudProtocol.MsdpDecode(payload);
            var table = result["A"].Should().BeOfType<Dictionary<string, object?>>().Subject;
            table.Should().BeEmpty();
        }

        [Fact]
        public void MsdpDecode_FramingBytes_DoNotLeakIntoKeys()
        {
            var payload = new byte[] { Var, (byte)'K', TableClose, Val, (byte)'v' };
            var result = MudProtocol.MsdpDecode(payload);
            result.Should().ContainKey("K");
        }

        [Fact]
        public void AardwolfDecode_TwoBytePayload_CarriesSingleByteOnce()
        {
            var message = MudProtocol.AardwolfDecode([100, 3]);
            message.DataByte.Should().Be(3);
            message.DataBytes.Should().BeEmpty();
        }

        [Fact]
        public void MsspDecode_EmptyName_IsSkipped()
        {
            var payload = new byte[] { Var, Val, (byte)'x' };
            MudProtocol.MsspDecode(payload).Should().BeEmpty();
        }

        [Fact]
        public void CharsetRequest_SpaceInName_RoundTrips()
        {
            var built = CharsetProtocol.BuildRequest(["US ASCII", "UTF-8"]);
            CharsetProtocol.ParseRequest(built).Should().Equal("US ASCII", "UTF-8");
        }

        [Fact]
        public void CharsetRequest_EmptyOffers_RoundTripsToEmpty()
        {
            var built = CharsetProtocol.BuildRequest([]);
            CharsetProtocol.ParseRequest(built).Should().BeEmpty();
        }

        [Fact]
        public void CharsetParseAccepted_EmptyPayload_ThrowsArgumentException()
        {
            Action parse = () => CharsetProtocol.ParseAccepted([]);
            parse.Should().Throw<ArgumentException>();
        }

        [Fact]
        public void EncodingFromLang_TrailingDot_ReturnsNull()
        {
            TelnetAccessories.EncodingFromLang("en_US.").Should().BeNull();
        }

        [Fact]
        public void EncodingFromLang_EmptyModifier_ReturnsNull()
        {
            TelnetAccessories.EncodingFromLang("en_US.@misc").Should().BeNull();
        }

        [Fact]
        public void LinemodeSlc_ReplyModifier_MatchesStoredFlags()
        {
            var state = new LinemodeState();
            var reply = state.ApplySlc(3, (byte)(2 | 32), 9);
            reply.Should().NotBeNull();
            var entry = state.GetEntry(3);
            var repliedFlags = (byte)(reply.GetValueOrDefault().Modifier & (64 | 32));
            entry.Flags.Should().Be(repliedFlags);
        }

        [Fact]
        public void TerminalSpeedRound_ExtremeRate_DoesNotOverflow()
        {
            TerminalSpeedProtocol.RoundForPadding(int.MinValue).Should().Be(50);
        }

        [Fact]
        public void EnvironBuildResponse_BogusVerb_ThrowsArgumentOutOfRange()
        {
            Action build = () => EnvironmentProtocol.BuildResponse(1, [], null, null, null);
            build.Should().Throw<ArgumentOutOfRangeException>();
        }
    }
}
