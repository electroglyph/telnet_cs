namespace telnet_cs.Tests
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using FluentAssertions;
    using Xunit;
    using telnet_cs.IO;
    using telnet_cs.Protocol;
    using telnet_cs.Server;

    /// <summary>
    /// Round-6 audit fixes and intentional-divergence pins (see
    /// <c>audit6.md</c> §10): the A6-2/A6-3/A6-7/A6-14/A6-19 tests fail
    /// without their fix; the deferred-behavior pins guard the cases where
    /// matching the reference would regress safety.
    /// </summary>
    public class Audit6FixTests
    {
        private static int[] Ascii(string text) => text.Select(c => (int)c).ToArray();

        private static byte[] OutboundBytes(ScriptedStream stream) =>
          stream.ByteWrites.SelectMany(static w => w).ToArray();

        private static bool ContainsSubsequence(byte[] haystack, byte[] needle)
        {
            for (int i = 0; i + needle.Length <= haystack.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < needle.Length; j++)
                {
                    if (haystack[i + j] != needle[j])
                    {
                        match = false;
                        break;
                    }
                }

                if (match)
                {
                    return true;
                }
            }

            return false;
        }

        private static int CountSubsequence(byte[] haystack, byte[] needle)
        {
            int count = 0;
            for (int i = 0; i + needle.Length <= haystack.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < needle.Length; j++)
                {
                    if (haystack[i + j] != needle[j])
                    {
                        match = false;
                        break;
                    }
                }

                if (match)
                {
                    count++;
                }
            }

            return count;
        }

        private static int[] TtypeIsFrame(string value) =>
          [255, 250, 24, 0, .. Encoding.Latin1.GetBytes(value), 255, 240];

        [Fact]
        public void MsdpEncode_FloatValue_UsesInvariantDecimalSeparator()
        {
            // A6-2. The reference encodes floats with str(value), which is
            // locale-independent; Convert.ToString(value, null) follows the
            // process locale and emits "1,5" under fr-FR.
            var previous = CultureInfo.CurrentCulture;
            CultureInfo.CurrentCulture = new CultureInfo("fr-FR");
            try
            {
                var bytes = MudProtocol.MsdpEncode(new Dictionary<string, object?>
                {
                    ["F"] = 1.5,
                    ["T"] = new Hashtable { [1.5] = "v" },
                });
                string text = Encoding.ASCII.GetString(bytes);
                text.Should().Contain("1.5");
                text.Should().NotContain("1,5");
            }
            finally
            {
                CultureInfo.CurrentCulture = previous;
            }
        }

        [Fact]
        public void ParseAccepted_NonAsciiName_ReturnsEmpty()
        {
            // A6-7. Charset names are ASCII (RFC 2066 §2 accepts only a name
            // the recipient offered), so a non-ASCII ACCEPTED name matches
            // nothing: it parses empty and the caller takes the rejection
            // path instead of adopting a lossy "??" decode.
            CharsetProtocol.ParseAccepted([2, 0x80]).Should().BeEmpty();
            CharsetProtocol.ParseAccepted([2, 255, 254]).Should().BeEmpty();
            CharsetProtocol.ParseAccepted([2, (byte)'U', (byte)'T', (byte)'F', (byte)'-', (byte)'8'])
                .Should().Be("UTF-8");
        }

        [Fact]
        public async Task CharsetAnswer_NonAsciiAccepted_TakesRejectionPath()
        {
            // A6-7 handler half: a non-ASCII ACCEPTED records no charset,
            // latches no binary decoding, and fires the rejection callback.
            using var stream = new ScriptedStream([255, 250, 42, 2, 0x80, 255, 240]);
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1);
            bool rejected = false;
            sut.CharsetRejected += () => rejected = true;
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(200))).Should().BeEmpty();
            sut.NegotiatedCharset.Should().BeNull();
            sut.ForceBinaryDecoding.Should().BeFalse();
            rejected.Should().BeTrue();
        }

        [Fact]
        public async Task ServerSlc_WithoutLinemodeAgreement_RepliesSlcButSkipsForwardMask()
        {
            // A6-3. The reference suppresses DO FORWARDMASK without receipt
            // of WILL LINEMODE but still answers the SLC block itself.
            using var stream = new ScriptedStream();
            using var session = new ServerSession(stream, new TelnetServerOptions(), CancellationToken.None);
            stream.Enqueue(255, 250, 34, 3, 3, 2, 5, 255, 240);
            (await session.ReadAsync(TimeSpan.FromMilliseconds(500))).Should().BeEmpty();
            byte[] outbound = OutboundBytes(stream);
            ContainsSubsequence(outbound, [255, 250, 34, 3, 3, 130, 5]).Should().BeTrue();
            ContainsSubsequence(outbound, [255, 250, 34, 253, 2]).Should().BeFalse();
        }

        [Fact]
        public async Task LinemodeServer_AdvancedPreset_SendsNoWillSga()
        {
            // A6-14 (SGA half). The reference stays in NVT line mode when
            // line_mode is set: no WILL SGA, but DO LINEMODE still goes out.
            var options = new TelnetServerOptions { RequestLinemode = true };
            using var stream = new ScriptedStream();
            using var session = new ServerSession(stream, options, CancellationToken.None);
            await session.SendOpeningPresetAsync();
            stream.Enqueue(255, 251, 24);
            await session.ReadAsync(TimeSpan.FromMilliseconds(500));
            byte[] outbound = OutboundBytes(stream);
            ContainsSubsequence(outbound, [255, 253, 34]).Should().BeTrue();
            ContainsSubsequence(outbound, [255, 251, 3]).Should().BeFalse();
        }

        [Fact]
        public async Task LinemodeServer_TtypeAnswers_SendNoWillEcho()
        {
            // A6-14 (ECHO half). The reference _negotiate_echo returns early
            // when line_mode is set; deferred DO NEW_ENVIRON is unaffected.
            var options = new TelnetServerOptions { RequestLinemode = true };
            using var stream = new ScriptedStream();
            stream.Enqueue([.. TtypeIsFrame("ANSI"), .. TtypeIsFrame("VT100"), .. TtypeIsFrame("VT100")]);
            using var session = new ServerSession(stream, options, CancellationToken.None);
            (await session.RequestTerminalTypesAsync(TimeSpan.FromSeconds(5))).Should().Equal("ANSI", "VT100");
            byte[] outbound = OutboundBytes(stream);
            ContainsSubsequence(outbound, [255, 251, 1]).Should().BeFalse();
            ContainsSubsequence(outbound, [255, 253, 39]).Should().BeTrue();
        }

        [Fact]
        public async Task Repl_Quit_ClosesSession()
        {
            // A6-19. The reference shell always closes its writer when the
            // loop ends; quit must not leave the session idling open.
            using var stream = new ScriptedStream(Ascii("quit\n"));
            using var session = new ServerSession(stream, new TelnetServerOptions(), CancellationToken.None);
            await ServerShells.RunReplAsync(session, CancellationToken.None);
            session.IsConnected.Should().BeFalse();
        }

        [Fact]
        public void MsdpDecode_NestedTableStrayBytes_Terminates()
        {
            // A6-1 pin (deferred): the reference loops forever on stray
            // bytes inside a nested TABLE; this stack skips them so a
            // network parser always terminates.
            var decoded = MudProtocol.MsdpDecode(new byte[] { 1, 75, 2, 3, 88, 89, 90, 4 });
            decoded.Should().ContainKey("K");
        }

        [Fact]
        public void SlcDefault_UnsupportedFunction_RepliesNosupportWithoutThrow()
        {
            // A6-4 pin (deferred): the reference raises AttributeError for
            // DEFAULT on a function missing from its table; this stack
            // answers NOSUPPORT with the disable value.
            var state = new LinemodeState();
            var reply = state.ApplySlcAsServer(19, 3, 0);
            reply.Should().NotBeNull();
            (reply.GetValueOrDefault().Modifier & 3).Should().Be(0);
            reply.GetValueOrDefault().Value.Should().Be(255);
        }

        [Fact]
        public async Task CharsetRequest_TruncatedVerbOnly_AnsweredRejectedWithoutThrow()
        {
            // A6-6 pin (deferred): the reference raises IndexError on a
            // verb-only REQUEST; this stack answers REJECTED.
            using var stream = new ScriptedStream([255, 250, 42, 1, 255, 240]);
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1);
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(200))).Should().BeEmpty();
            ContainsSubsequence(OutboundBytes(stream), [255, 250, 42, 3, 255, 240]).Should().BeTrue();
        }

        [Fact]
        public void LinemodeState_SlcChange_DoesNotPolluteOtherInstances()
        {
            // A6-9 pin (deferred): the reference shares mutable SLC value
            // objects process-wide, so one VARIABLE change corrupts every
            // session; per-instance struct tables stay isolated.
            var first = new LinemodeState();
            var second = new LinemodeState();
            var before = first.GetEntry(3);
            first.ApplySlcAsServer(3, 2, 7).Should().NotBeNull();
            second.GetEntry(3).Should().Be(before);
            new LinemodeState().GetEntry(3).Should().Be(before);
        }

        [Fact]
        public async Task RepeatWillRefusal_ResendsDont()
        {
            // A6-10 pin (deferred): the reference suppresses a repeated
            // DONT for an already-refused WILL; this stack resends the
            // idempotent refusal every time (documented in
            // NegotiationState).
            using var stream = new ScriptedStream([255, 251, 99, 255, 251, 99]);
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1);
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(200))).Should().BeEmpty();
            CountSubsequence(OutboundBytes(stream), [255, 254, 99]).Should().Be(2);
        }

        [Fact]
        public void MsspEncode_NonStringValue_ThrowsArgumentException()
        {
            // A6-16 pin (deferred): the reference raises AttributeError on
            // tuple/int values; this stack throws ArgumentException.
            Action act = () => MudProtocol.MsspEncode(new Dictionary<string, object> { ["K"] = 42 });
            act.Should().Throw<ArgumentException>();
        }

        [Fact]
        public void GmcpEncode_EmptyPackage_ThrowsArgumentException()
        {
            // A6-17 pin (deferred): the reference emits a malformed
            // leading-space frame for an empty package; this stack throws.
            Action noData = () => MudProtocol.GmcpEncode(string.Empty);
            Action withJson = () => MudProtocol.GmcpEncode(string.Empty, "{\"a\":1}");
            Action withData = () => MudProtocol.GmcpEncodeData("   ", null);
            noData.Should().Throw<ArgumentException>();
            withJson.Should().Throw<ArgumentException>();
            withData.Should().Throw<ArgumentException>();
        }

        [Fact]
        public void EncodingFromLang_Null_ReturnsNull()
        {
            // A6-18 pin (deferred): the reference raises TypeError on None;
            // this stack returns null.
            TelnetAccessories.EncodingFromLang(null).Should().BeNull();
        }
    }
}
