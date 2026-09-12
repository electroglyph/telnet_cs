// Phase-A option tests: LOGOUT (18), SNDLOC (23), EOR (25/239), LFLOW (33),
// NewEnvironment (39), CHARSET (42), MCCP (85/86/87), MUD SB wire, and the
// still-refused options (37/38/45/46/92). Each pins exact wire bytes.
namespace telnet_cs.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using FluentAssertions;
    using Xunit;
    using telnet_cs.IO;

    public class ExtendedOptionsTests
    {
        private const int Iac = 255;
        private const int Sb = 250;
        private const int Se = 240;
        private const int Will = 251;
        private const int Wont = 252;
        private const int Do = 253;
        private const int Dont = 254;

        private static async Task<(string Output, List<byte[]> Writes, ByteStreamHandler Handler)> ReadOnceAsync(
          Action<ByteStreamHandler> configure, params int[] reads)
        {
            using var stream = new ScriptedStream(reads);
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1);
            configure(sut);
            var output = await sut.ReadAsync(TimeSpan.FromMilliseconds(50));
            return (output, [.. stream.ByteWrites], sut);
        }

        private static byte[] Concat(List<byte[]> writes)
        {
            return [.. writes.SelectMany(w => w)];
        }

        [Fact]
        public async Task DoLogout_RefusedWithWont_AndSignalsLogout()
        {
            var signaled = false;
            var (output, writes, _) = await ReadOnceAsync(h => h.LogoutRequested += () => signaled = true, Iac, Do, 18);
            output.Should().BeEmpty();
            Concat(writes).Should().Equal(Iac, Wont, 18);
            signaled.Should().BeTrue();
        }

        [Fact]
        public async Task WillLogout_RefusedWithDont_WithoutSignal()
        {
            var signaled = false;
            var (output, writes, _) = await ReadOnceAsync(h => h.LogoutRequested += () => signaled = true, Iac, Will, 18);
            output.Should().BeEmpty();
            Concat(writes).Should().Equal(Iac, Dont, 18);
            signaled.Should().BeFalse();
        }

        [Fact]
        public async Task DoSendLocation_AnsweredWill_AndVolunteersLocation()
        {
            var (output, writes, _) = await ReadOnceAsync(h => h.SendLocation = "DEN", Iac, Do, 23);
            output.Should().BeEmpty();
            writes.Should().HaveCount(2);
            writes[0].Should().Equal(Iac, Will, 23);
            writes[1].Should().Equal(Iac, Sb, 23, (byte)'D', (byte)'E', (byte)'N', Iac, Se);
        }

        [Fact]
        public async Task DoSendLocation_WithoutConfiguredLocation_SendsNoSb()
        {
            var (output, writes, _) = await ReadOnceAsync(_ => { }, Iac, Do, 23);
            output.Should().BeEmpty();
            Concat(writes).Should().Equal(Iac, Will, 23);
        }

        [Fact]
        public async Task SbSendLocation_SurfacesRawLocation()
        {
            string? received = null;
            var (output, writes, sut) = await ReadOnceAsync(
              h => h.LocationReceived += location => received = location,
              Iac, Sb, 23, (byte)'H', (byte)'I', Iac, Se);
            output.Should().BeEmpty();
            writes.Should().BeEmpty();
            received.Should().Be("HI");
            sut.LastLocation.Should().Be("HI");
        }

        [Fact]
        public async Task IacEor_WithoutAgreement_IsNop()
        {
            // RFC 885: EOR not in effect means received IAC EOR is a NOP —
            // no hook, no reply, no data.
            var fired = 0;
            var (output, writes, _) = await ReadOnceAsync(h => h.EorReceived += () => fired++, Iac, 239);
            output.Should().BeEmpty();
            writes.Should().BeEmpty();
            fired.Should().Be(0);
        }

        [Fact]
        public async Task IacEor_WhenAgreed_SurfacesEvent_WithoutReplyOrData()
        {
            var fired = 0;
            var (output, writes, _) = await ReadOnceAsync(
              h => h.EorReceived += () => fired++, Iac, Will, 25, Iac, 239);
            output.Should().BeEmpty();
            Concat(writes).Should().Equal(Iac, Do, 25);
            fired.Should().Be(1);
        }

        [Fact]
        public async Task SendEor_WithoutDoEor_ReturnsFalseSendingNothing()
        {
            using var stream = new ScriptedStream();
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1);
            (await sut.SendEorAsync()).Should().BeFalse();
            stream.ByteWrites.Should().BeEmpty();
        }

        [Fact]
        public async Task SendEor_AfterDoEor_SendsIacEor()
        {
            using var stream = new ScriptedStream(Iac, Do, 25);
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1);
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().BeEmpty();
            (await sut.SendEorAsync()).Should().BeTrue();
            stream.ByteWrites.Should().HaveCount(2);
            stream.ByteWrites[0].Should().Equal(Iac, Will, 25);
            stream.ByteWrites[1].Should().Equal(Iac, 239);
        }

        [Fact]
        public async Task WillLineflow_AsServer_SendsRestartXonByDefault()
        {
            var (output, writes, _) = await ReadOnceAsync(h => h.SendLineflowAsServer = true, Iac, Will, 33);
            output.Should().BeEmpty();
            writes.Should().HaveCount(2);
            writes[0].Should().Equal(Iac, Do, 33);
            writes[1].Should().Equal(Iac, Sb, 33, 3, Iac, Se);
        }

        [Fact]
        public async Task WillLineflow_AsClient_SendsNoSb()
        {
            var (output, writes, _) = await ReadOnceAsync(_ => { }, Iac, Will, 33);
            output.Should().BeEmpty();
            Concat(writes).Should().Equal(Iac, Do, 33);
        }

        [Fact]
        public async Task SbLineflow_RestartAny_ClearsXonOnlyKeepingFlowOn()
        {
            byte? received = null;
            var (output, writes, sut) = await ReadOnceAsync(
              h => h.LineflowReceived += mode => received = mode,
              Iac, Will, 33, Iac, Sb, 33, 2, Iac, Se);
            output.Should().BeEmpty();
            Concat(writes).Should().Equal(Iac, Do, 33);
            received.Should().Be(2);
            sut.LineflowXonAny.Should().BeFalse();
            sut.LineflowEnabled.Should().BeTrue();
        }

        [Fact]
        public async Task SbLineflow_Off_DisablesFlowKeepingRestartMode()
        {
            var (output, _, sut) = await ReadOnceAsync(
              _ => { }, Iac, Will, 33, Iac, Sb, 33, 0, Iac, Se);
            output.Should().BeEmpty();
            sut.LineflowEnabled.Should().BeFalse();
            sut.LineflowXonAny.Should().BeFalse();
        }

        [Fact]
        public async Task SbComPort_SurfacesRawPayloadWithoutReply()
        {
            // RFC 2217 framing level only: the payload is surfaced for the
            // caller; modem-line semantics are not implemented.
            byte[]? received = null;
            var (output, writes, _) = await ReadOnceAsync(
              h => h.ComPortReceived += payload => received = payload,
              Iac, Sb, 44, 5, 3, Iac, Se);
            output.Should().BeEmpty();
            writes.Should().BeEmpty();
            received.Should().Equal(5, 3);
        }

        [Fact]
        public async Task DoRcte_RefusedWithWont()
        {
            // RFC 726 RCTE is not implemented (obsolete remote-echo control):
            // both directions are refused.
            var (output, writes, _) = await ReadOnceAsync(_ => { }, Iac, Do, 7);
            output.Should().BeEmpty();
            Concat(writes).Should().Equal(Iac, Wont, 7);
        }

        [Fact]
        public async Task SbLineflow_UnknownMode_IgnoredWithoutEvent()
        {
            var fired = 0;
            var (output, _, _) = await ReadOnceAsync(
              h => h.LineflowReceived += _ => fired++, Iac, Will, 33, Iac, Sb, 33, 9, Iac, Se);
            output.Should().BeEmpty();
            fired.Should().Be(0);
        }

        [Fact]
        public async Task DoNewEnvironment_AnsweredWill()
        {
            var (output, writes, _) = await ReadOnceAsync(_ => { }, Iac, Do, 39);
            output.Should().BeEmpty();
            Concat(writes).Should().Equal(Iac, Will, 39);
        }

        [Fact]
        public async Task SendNewEnvironment_IsAnsweredOnOption39()
        {
            var (output, writes, _) = await ReadOnceAsync(
              h => h.EnvironmentUser = "bob",
              Iac, Sb, 39, 1, 0, (byte)'U', (byte)'S', (byte)'E', (byte)'R', Iac, Se);
            output.Should().BeEmpty();
            writes.Should().HaveCount(1);
            writes[0].Take(4).Should().Equal(Iac, Sb, 39, 0);
            writes[0].Should().Contain((byte)'b');
        }

        [Fact]
        public async Task DoCharset_AnsweredWill()
        {
            var (output, writes, _) = await ReadOnceAsync(_ => { }, Iac, Do, 42);
            output.Should().BeEmpty();
            Concat(writes).Should().Equal(Iac, Will, 42);
        }

        [Fact]
        public async Task SbCharsetRequest_KnownOffer_AnswersAccepted()
        {
            string? accepted = null;
            var rejected = 0;
            var (output, writes, sut) = await ReadOnceAsync(
              h =>
              {
                  h.CharsetAccepted += name => accepted = name;
                  h.CharsetRejected += () => rejected++;
              },
              Iac, Sb, 42, 1, 32, (byte)'U', (byte)'T', (byte)'F', (byte)'-', (byte)'8', Iac, Se);
            output.Should().BeEmpty();
            writes.Should().HaveCount(1);
            writes[0].Should().Equal(Iac, Sb, 42, 2, (byte)'U', (byte)'T', (byte)'F', (byte)'-', (byte)'8', Iac, Se);
            accepted.Should().Be("UTF-8");
            rejected.Should().Be(0);
            sut.NegotiatedCharset.Should().Be("UTF-8");
        }

        [Fact]
        public async Task SbCharsetRequest_UnknownOffer_AnswersRejected()
        {
            string? accepted = null;
            var rejected = 0;
            var (output, writes, sut) = await ReadOnceAsync(
              h =>
              {
                  h.CharsetAccepted += name => accepted = name;
                  h.CharsetRejected += () => rejected++;
              },
              Iac, Sb, 42, 1, 32, (byte)'X', Iac, Se);
            output.Should().BeEmpty();
            writes.Should().HaveCount(1);
            writes[0].Should().Equal(Iac, Sb, 42, 3, Iac, Se);
            accepted.Should().BeNull();
            rejected.Should().Be(1);
            sut.NegotiatedCharset.Should().BeNull();
        }

        [Fact]
        public async Task SbCharsetAccepted_RecordsCharsetWithoutReply()
        {
            string? accepted = null;
            var (output, writes, sut) = await ReadOnceAsync(
              h => h.CharsetAccepted += name => accepted = name,
              Iac, Sb, 42, 2, (byte)'U', (byte)'T', (byte)'F', (byte)'-', (byte)'8', Iac, Se);
            output.Should().BeEmpty();
            writes.Should().BeEmpty();
            accepted.Should().Be("UTF-8");
            sut.NegotiatedCharset.Should().Be("UTF-8");
        }

        [Fact]
        public async Task SbCharsetTTableIs_AnsweredTTableRejected()
        {
            var (output, writes, _) = await ReadOnceAsync(
              _ => { }, Iac, Sb, 42, 4, Iac, Se);
            output.Should().BeEmpty();
            writes.Should().HaveCount(1);
            writes[0].Should().Equal(Iac, Sb, 42, 7, Iac, Se);
        }

        [Fact]
        public async Task SbCharsetTTableRejected_ClearsPendingWithoutReply()
        {
            var (output, writes, sut) = await ReadOnceAsync(
              h => h.CharsetRequestPending = true, Iac, Sb, 42, 7, Iac, Se);
            output.Should().BeEmpty();
            writes.Should().BeEmpty();
            sut.CharsetRequestPending.Should().BeFalse();
        }

        [Fact]
        public async Task SbCharsetAccepted_SwitchesEncodingAndForceBinary()
        {
            var (output, writes, sut) = await ReadOnceAsync(
              _ => { }, Iac, Sb, 42, 2, (byte)'U', (byte)'T', (byte)'F', (byte)'-', (byte)'8', Iac, Se);
            output.Should().BeEmpty();
            writes.Should().BeEmpty();
            sut.NegotiatedCharset.Should().Be("UTF-8");
            sut.TextEncoding.Should().NotBeNull();
            sut.TextEncoding!.WebName.Should().Be("utf-8");
            sut.ForceBinaryDecoding.Should().BeTrue();
        }

        [Fact]
        public async Task SbCharsetRequest_Simultaneous_ServerRole_AnswersRejected()
        {
            string? accepted = null;
            var (output, writes, _) = await ReadOnceAsync(
              h =>
              {
                  h.IsServerRole = true;
                  h.CharsetRequestPending = true;
                  h.CharsetAccepted += name => accepted = name;
              },
              Iac, Sb, 42, 1, 32, (byte)'U', (byte)'T', (byte)'F', (byte)'-', (byte)'8', Iac, Se);
            output.Should().BeEmpty();
            writes.Should().HaveCount(1);
            writes[0].Should().Equal(Iac, Sb, 42, 3, Iac, Se);
            accepted.Should().BeNull();
        }

        [Fact]
        public async Task SbCharsetRequest_Simultaneous_ClientRole_AnswersPeerRequest()
        {
            string? accepted = null;
            var (output, writes, _) = await ReadOnceAsync(
              h =>
              {
                  h.IsServerRole = false;
                  h.CharsetRequestPending = true;
                  h.CharsetAccepted += name => accepted = name;
              },
              Iac, Sb, 42, 1, 32, (byte)'U', (byte)'T', (byte)'F', (byte)'-', (byte)'8', Iac, Se);
            output.Should().BeEmpty();
            writes.Should().HaveCount(1);
            writes[0].Should().Equal(Iac, Sb, 42, 2, (byte)'U', (byte)'T', (byte)'F', (byte)'-', (byte)'8', Iac, Se);
            accepted.Should().Be("UTF-8");
        }

        [Fact]
        public async Task SbCharsetAccepted_WithoutTtype_IsRecorded()
        {
            var (output, writes, sut) = await ReadOnceAsync(
              _ => { }, Iac, Will, 42, Iac, Wont, 24,
              Iac, Sb, 42, 2, (byte)'U', (byte)'T', (byte)'F', (byte)'-', (byte)'8', Iac, Se);
            output.Should().BeEmpty();
            sut.NegotiatedCharset.Should().Be("UTF-8");
            writes.Should().ContainSingle().Subject.Should().Equal(Iac, Do, 42);
        }

        [Fact]
        public async Task RequestCharsetAsync_SecondCallWhilePending_ReturnsFalse()
        {
            using var stream = new ScriptedStream(Iac, Will, 42);
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1);
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().BeEmpty();
            Concat([.. stream.ByteWrites]).Should().Equal(Iac, Do, 42);
            (await sut.RequestCharsetAsync()).Should().BeTrue();
            (await sut.RequestCharsetAsync()).Should().BeFalse();
            stream.Enqueue(Iac, Sb, 42, 2, (byte)'U', (byte)'T', (byte)'F', (byte)'-', (byte)'8', Iac, Se);
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().BeEmpty();
            sut.NegotiatedCharset.Should().Be("UTF-8");
            (await sut.RequestCharsetAsync()).Should().BeTrue();
        }

        [Fact]
        public async Task CharsetSelector_Hook_OverridesDefaultSelection()
        {
            var (output, writes, _) = await ReadOnceAsync(
              h => h.CharsetSelector = _ => "US-ASCII",
              Iac, Sb, 42, 1, 32, (byte)'U', (byte)'T', (byte)'F', (byte)'-', (byte)'8', Iac, Se);
            output.Should().BeEmpty();
            writes.Should().HaveCount(1);
            writes[0].Should().Equal(
              Iac, Sb, 42, 2, (byte)'U', (byte)'S', (byte)'-', (byte)'A', (byte)'S',
              (byte)'C', (byte)'I', (byte)'I', Iac, Se);
        }

        [Fact]
        public async Task CharsetSelector_Hook_ReturningNull_Rejects()
        {
            var rejected = 0;
            var (output, writes, _) = await ReadOnceAsync(
              h =>
              {
                  h.CharsetSelector = _ => null;
                  h.CharsetRejected += () => rejected++;
              },
              Iac, Sb, 42, 1, 32, (byte)'U', (byte)'T', (byte)'F', (byte)'-', (byte)'8', Iac, Se);
            output.Should().BeEmpty();
            writes.Should().HaveCount(1);
            writes[0].Should().Equal(Iac, Sb, 42, 3, Iac, Se);
            rejected.Should().Be(1);
        }

        [Fact]
        public async Task DoMccp2_RefusedByDefault()
        {
            var (output, writes, _) = await ReadOnceAsync(_ => { }, Iac, Do, 86);
            output.Should().BeEmpty();
            Concat(writes).Should().Equal(Iac, Wont, 86);
        }

        [Fact]
        public async Task DoMccp2_AgreedWhenEnabled()
        {
            var (output, writes, _) = await ReadOnceAsync(h => h.EnableMccp = true, Iac, Do, 86);
            output.Should().BeEmpty();
            Concat(writes).Should().Equal(Iac, Will, 86);
        }

        [Fact]
        public async Task EmptySbMccp2_ArmsFlag_AndFiresStartHook()
        {
            var fired = 0;
            var (output, writes, sut) = await ReadOnceAsync(
              h =>
              {
                  h.EnableMccp = true;
                  h.Mccp2StartReceived += () => fired++;
              },
              Iac, Will, 86, Iac, Sb, 86, Iac, Se);
            output.Should().BeEmpty();
            Concat(writes).Should().Equal(Iac, Do, 86);
            sut.Mccp2Active.Should().BeTrue();
            fired.Should().Be(1);
        }

        [Fact]
        public async Task DoMccp1_AlwaysRefused_EvenWhenEnabled()
        {
            var (output, writes, _) = await ReadOnceAsync(h => h.EnableMccp = true, Iac, Do, 85);
            output.Should().BeEmpty();
            Concat(writes).Should().Equal(Iac, Wont, 85);
        }

        [Fact]
        public async Task NonEmptySbMccp2_IgnoredWithoutArming()
        {
            var (output, _, sut) = await ReadOnceAsync(
              h => h.EnableMccp = true, Iac, Will, 86, Iac, Sb, 86, 1, Iac, Se);
            output.Should().BeEmpty();
            sut.Mccp2Active.Should().BeFalse();
        }

        [Fact]
        public async Task DoGmcp_AgreedByDefault()
        {
            var (output, writes, _) = await ReadOnceAsync(_ => { }, Iac, Do, 201);
            output.Should().BeEmpty();
            Concat(writes).Should().Equal(Iac, Will, 201);
        }

        [Fact]
        public async Task DoGmcp_DeclinedWhenMudDisabled()
        {
            var (output, writes, _) = await ReadOnceAsync(h => h.EnableMudOptions = false, Iac, Do, 201);
            output.Should().BeEmpty();
            Concat(writes).Should().Equal(Iac, Wont, 201);
        }

        [Fact]
        public async Task SbGmcp_SurfacesOptionAndPayload()
        {
            (int Option, byte[] Payload)? received = null;
            var (output, writes, _) = await ReadOnceAsync(
              h => h.MudSubnegotiationReceived += (option, payload) => received = (option, payload),
              Iac, Will, 201, Iac, Sb, 201, (byte)'h', (byte)'i', Iac, Se);
            output.Should().BeEmpty();
            Concat(writes).Should().Equal(Iac, Do, 201);
            received.Should().NotBeNull();
            received!.Value.Option.Should().Be(201);
            received.Value.Payload.Should().Equal((byte)'h', (byte)'i');
        }

        [Theory]
        [InlineData(37)]
        [InlineData(38)]
        [InlineData(45)]
        [InlineData(46)]
        [InlineData(85)]
        [InlineData(92)]
        public async Task DoRefusedOption_AnsweredWont(int option)
        {
            var (output, writes, _) = await ReadOnceAsync(_ => { }, Iac, Do, option);
            output.Should().BeEmpty();
            Concat(writes).Should().Equal((byte)Iac, (byte)Wont, (byte)option);
        }

        [Theory]
        [InlineData(37)]
        [InlineData(38)]
        [InlineData(45)]
        [InlineData(46)]
        [InlineData(85)]
        [InlineData(92)]
        public async Task WillRefusedOption_AnsweredDont(int option)
        {
            var (output, writes, _) = await ReadOnceAsync(_ => { }, Iac, Will, option);
            output.Should().BeEmpty();
            Concat(writes).Should().Equal((byte)Iac, (byte)Dont, (byte)option);
        }
    }
}
