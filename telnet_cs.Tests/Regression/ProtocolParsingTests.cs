namespace telnet_cs.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Threading.Tasks;
    using FluentAssertions;
    using Xunit;
    using telnet_cs.Protocol;

    public class ProtocolParsingTests
    {
        private const byte Var = 1;
        private const byte Val = 2;
        private const byte TableOpen = 3;
        private const byte TableClose = 4;

        [Fact]
        public async Task MsdpDecode_TableGarbage_CompletesInsteadOfHanging()
        {
            // Hardening beyond the reference: telnetlib3's _parse_table has
            // the same missing advance on non-VAR bytes and would loop
            // forever here too. The TMI MSDP spec is silent on malformed
            // input, so terminating (skipping garbage) is the defensible
            // choice for a network parser.
            var payload = new byte[] { Var, (byte)'K', Val, TableOpen, 0x42, TableClose };
            var task = Task.Run(() => MudProtocol.MsdpDecode(payload));
            var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(3)));
            completed.Should().Be(task);
        }

        [Fact]
        public void MsdpDecode_TableKeyWithoutVal_KeepsKeyWithEmptyValue()
        {
            // Keys run until VAR or VAL only, so TABLE_CLOSE stays inside the
            // key. The table parse stores unconditionally, keeping the key
            // with an empty value.
            var payload = new byte[] { Var, (byte)'A', Val, TableOpen, Var, (byte)'K', TableClose };
            var result = MudProtocol.MsdpDecode(payload);
            var table = result["A"].Should().BeOfType<Dictionary<string, object?>>().Subject;
            table.Should().ContainKey("K\x04").WhoseValue.Should().Be("");
        }

        [Fact]
        public void MsdpDecode_FramingBytes_DoNotLeakIntoKeys()
        {
            // Keys run until VAR or VAL only, so a framing byte such as
            // TABLE_CLOSE is preserved inside the key instead of terminating
            // it. The top level still requires VAL after the key.
            var payload = new byte[] { Var, (byte)'K', TableClose, Val, (byte)'v' };
            var result = MudProtocol.MsdpDecode(payload);
            result.Should().ContainSingle().Which.Should().Be(KeyValuePair.Create<string, object?>("K\x04", "v"));
        }

        [Fact]
        public void AardwolfDecode_TwoBytePayload_CarriesSingleByteInBothViews()
        {
            // Reassessment: telnetlib3's aardwolf_decode sets both
            // data_byte and data_bytes (buf[1:]) for a 2-byte payload, and
            // this implementation already matches. The remaining work is
            // doc clarity on AardwolfMessage, not a decode fix.
            var message = MudProtocol.AardwolfDecode([100, 3]);
            message.DataByte.Should().Be(3);
            message.DataBytes.Should().Equal(3);
        }

        [Fact]
        public void MsspDecode_EmptyName_KeepsEntryPerReference()
        {
            // Reassessment: telnetlib3's mssp_decode guards only on
            // "current_var is not None", so VAR VAL 'x' yields {"": "x"},
            // and this implementation matches. Empty names never occur on a
            // conforming wire; skipping them would be hardening that
            // diverges from the reference, so parity is pinned instead.
            var payload = new byte[] { Var, Val, (byte)'x' };
            MudProtocol.MsspDecode(payload).Should().ContainKey("").WhoseValue.Should().Be("x");
        }

        [Fact]
        public void CharsetRequest_SpaceInName_RoundTrips()
        {
            // RFC 2066 section 2: the separator "must not appear within any
            // character set" and its value "is chosen by the sender", so a
            // space-separated offer of "US ASCII" is self-corrupting and the
            // builder must pick a non-colliding separator (e.g. ';').
            var built = CharsetProtocol.BuildRequest(["US ASCII", "UTF-8"]);
            char separator = (char)built[1];
            "US ASCIIUTF-8".Should().NotContain(separator.ToString());
            CharsetProtocol.ParseRequest(built).Should().Equal("US ASCII", "UTF-8");
        }

        [Fact]
        public void CharsetRequest_EmptyOffers_RoundTripsToEmpty()
        {
            // RFC 2066 section 2 wants one or more charsets, so [] is
            // degenerate input; as leniency it must still round-trip to []
            // rather than [""] (the current " ".Split(' ') trap).
            var built = CharsetProtocol.BuildRequest([]);
            CharsetProtocol.ParseRequest(built).Should().BeEmpty();
        }

        [Fact]
        public void CharsetParseAccepted_EmptyPayload_ReturnsEmpty()
        {
            // An ACCEPTED without even the verb carries no charset names,
            // so it parses to the empty selection instead of throwing: a
            // short subnegotiation must not fail the read.
            CharsetProtocol.ParseAccepted([]).Should().BeEmpty();
        }

        [Fact]
        public void EncodingFromLang_TrailingDot_ReturnsNull()
        {
            // Matches the reference split-once behavior: "en_US." carries an
            // empty codeset string (not missing), so empty is correct.
            TelnetAccessories.EncodingFromLang("en_US.").Should().Be(string.Empty);
        }

        [Fact]
        public void EncodingFromLang_EmptyModifier_ReturnsNull()
        {
            // Same split-once rule: ".@misc" is an empty codeset before the
            // modifier, so empty is correct.
            TelnetAccessories.EncodingFromLang("en_US.@misc").Should().Be(string.Empty);
        }

        [Fact]
        public void LinemodeSlc_ReplyModifier_MatchesStoredFlags()
        {
            // RFC 1184 section 5.5 rule 3: on agreement "we switch to it and
            // reply with the same value, but also set the SLC_ACK bit",
            // and the section 5.10 example answers IP VALUE|FLUSHIN|FLUSHOUT
            // with VALUE|FLUSHIN|FLUSHOUT|ACK. The agreed FLUSHIN/FLUSHOUT
            // bits must therefore be echoed, not dropped. Note: this
            // conflicts with the pre-existing pin that expects a bare 130
            // reply on one path — that expectation needs revisiting against
            // this RFC text before any implementation change.
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
            // RFC 1079 section 5 rounds to the nearest allowed rate for
            // padding; the nearest to int.MinValue is the table minimum
            // (50, the lowest standard line rate). The int-arithmetic
            // Math.Abs(candidate - rate) wraps and currently picks 115200.
            TerminalSpeedProtocol.RoundForPadding(int.MinValue).Should().Be(50);
        }

        [Fact]
        public void EnvironBuildResponse_BogusVerb_ThrowsArgumentOutOfRange()
        {
            // RFC 1572 section 2: SEND (1) is a request; responses are IS
            // (0) and INFO (2). A response builder accepting verb 1 would
            // emit a bogus frame, so ArgumentOutOfRangeException is correct.
            Action build = () => EnvironmentProtocol.BuildResponse(1, [], null, null, null);
            build.Should().Throw<ArgumentOutOfRangeException>();
        }
    }
}
