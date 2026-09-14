// Server-role tests for the Phase-A collectors: SNDLOC (23), new-form
// ENVIRON (39), CHARSET (42), LFLOW auto-send (33), and the MTTS
// effective-terminal rule. Each pins exact wire bytes.
namespace telnet_cs.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using FluentAssertions;
    using Xunit;
    using telnet_cs.Server;

    public class ExtendedCollectorsTests
    {
        private const int Iac = 255;
        private const int Sb = 250;
        private const int Se = 240;
        private const int Will = 251;
        private const int Do = 253;

        private static ServerSession NewSession(ScriptedStream stream)
        {
            return new ServerSession(stream, new TelnetServerOptions(), CancellationToken.None);
        }

        private static int[] SbIs(int option, string text)
        {
            var bytes = new List<int> { Iac, Sb, option, 0 };
            foreach (var c in text)
            {
                bytes.Add(c);
            }

            bytes.Add(Iac);
            bytes.Add(Se);
            return [.. bytes];
        }

        [Fact]
        public async Task RequestSendLocationAsync_ReturnsVolunteeredLocation()
        {
            using var stream = new ScriptedStream(Iac, Will, 23, Iac, Sb, 23, 72, 73, Iac, Se);
            using var session = NewSession(stream);
            (await session.RequestSendLocationAsync(TimeSpan.FromSeconds(5))).Should().Be("HI");
            session.ClientLocation.Should().Be("HI");
            // The volunteered WILL advances negotiation, so the DO SNDLOC is
            // followed by the advanced batch (WILL SGA, WILL BINARY, DO NAWS,
            // DO CHARSET).
            stream.ByteWrites.Should().HaveCount(5);
            stream.ByteWrites[0].Should().Equal(Iac, Do, 23);
            stream.ByteWrites[1].Should().Equal(Iac, Will, 3);
            stream.ByteWrites[2].Should().Equal(Iac, Will, 0);
            stream.ByteWrites[3].Should().Equal(Iac, Do, 31);
            stream.ByteWrites[4].Should().Equal(Iac, Do, 42);
        }

        [Fact]
        public async Task RequestCharsetAsync_SendsRequest_AndReturnsAccepted()
        {
            using var stream = new ScriptedStream(Iac, Sb, 42, 2, 85, 84, 70, 45, 56, Iac, Se);
            using var session = NewSession(stream);
            (await session.RequestCharsetAsync(TimeSpan.FromSeconds(5))).Should().Be("UTF-8");
            session.ClientCharset.Should().Be("UTF-8");
            var request = stream.ByteWrites.Should().ContainSingle().Subject;
            request.Take(5).Should().Equal(Iac, Sb, 42, 1, 32);
        }

        [Fact]
        public async Task SpontaneousCharsetAccepted_IsLatchedWithoutRequest()
        {
            using var stream = new ScriptedStream(Iac, Sb, 42, 2, 85, 84, 70, 45, 56, Iac, Se);
            using var session = NewSession(stream);
            (await session.ReadAsync(TimeSpan.FromMilliseconds(500))).Should().BeEmpty();
            session.ClientCharset.Should().Be("UTF-8");
            stream.ByteWrites.Should().BeEmpty();
        }

        [Fact]
        public async Task AcceptedCharset_SwitchesReadDecodingWithoutBinary()
        {
            using var stream = new ScriptedStream(
              Iac, Sb, 42, 2, 85, 84, 70, 45, 56, Iac, Se);
            using var session = NewSession(stream);
            (await session.RequestCharsetAsync(TimeSpan.FromSeconds(5))).Should().Be("UTF-8");
            stream.Enqueue(0xC3, 0xA9);
            (await session.ReadAsync(TimeSpan.FromMilliseconds(500))).Should().Be("é");
        }

        [Fact]
        public async Task SimultaneousCharsetRequest_WhileOursOutstanding_AnswersRejected()
        {
            // Deterministic ordering: our REQUEST must genuinely be
            // outstanding before the peer's REQUEST arrives. Prequeueing it
            // lets the constructor-started background pump answer first
            // with ACCEPTED (or beat our REQUEST to the wire), which is
            // correct for an idle session but not what this test pins.
            using var stream = new ScriptedStream();
            using var session = NewSession(stream);
            var task = session.RequestCharsetAsync(TimeSpan.FromMilliseconds(200));
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (stream.ByteWrites.Count == 0 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(1);
            }

            stream.ByteWrites.Should().NotBeEmpty("our CHARSET REQUEST must be outstanding before the peer's arrives");
            stream.Enqueue(Iac, Sb, 42, 1, 32, 85, 84, 70, 45, 56, Iac, Se);
            (await task).Should().BeNull();
            stream.ByteWrites.Should().HaveCount(2);
            stream.ByteWrites[0].Take(5).Should().Equal(Iac, Sb, 42, 1, 32);
            stream.ByteWrites[1].Should().Equal(Iac, Sb, 42, 3, Iac, Se);
        }

        [Fact]
        public async Task RequestNewEnvironmentAsync_ReturnsIsEntries()
        {
            using var stream = new ScriptedStream(Iac, Sb, 39, 0, 0, 65, 1, 66, Iac, Se);
            using var session = NewSession(stream);
            var result = await session.RequestNewEnvironmentAsync(TimeSpan.FromSeconds(5));
            result.Should().ContainSingle(kv => kv.Key == "A" && kv.Value == "B");
            session.ClientNewEnvironment.Should().ContainSingle(kv => kv.Key == "A" && kv.Value == "B");
            var request = stream.ByteWrites.Should().ContainSingle().Subject;
            request.Take(3).Should().Equal(Iac, Sb, 39);
        }

        [Fact]
        public async Task RequestTerminalTypesAsync_MttsThirdEntry_EffectiveTypeIsSecond()
        {
            using var stream = new ScriptedStream(
              [.. SbIs(24, "XTERM256"), .. SbIs(24, "XTERM"), .. SbIs(24, "MTTS 137"), .. SbIs(24, "MTTS 137")]);
            using var session = NewSession(stream);
            var chain = await session.RequestTerminalTypesAsync(TimeSpan.FromSeconds(5));
            chain.Should().Equal("XTERM256", "XTERM", "MTTS 137");
            session.ClientEffectiveTerminalType.Should().Be("XTERM");
        }

        [Fact]
        public async Task RequestTerminalTypesAsync_WithoutMtts_EffectiveTypeIsFirst()
        {
            using var stream = new ScriptedStream([.. SbIs(24, "XTERM"), .. SbIs(24, "XTERM")]);
            using var session = NewSession(stream);
            var chain = await session.RequestTerminalTypesAsync(TimeSpan.FromSeconds(5));
            chain.Should().Equal("XTERM");
            session.ClientEffectiveTerminalType.Should().Be("XTERM");
        }

        [Fact]
        public async Task WillLineflow_AsServer_AgreesAndVolunteersRestartXon()
        {
            using var stream = new ScriptedStream(Iac, Will, 33);
            using var session = NewSession(stream);
            (await session.ReadAsync(TimeSpan.FromMilliseconds(500))).Should().BeEmpty();
            // The volunteered RESTART-XON SB (no DO reply — the reference
            // probes instead of acknowledging), then the advanced batch the
            // agreement releases (SGA, BINARY, NAWS, CHARSET).
            stream.ByteWrites.Should().HaveCount(5);
            stream.ByteWrites[0].Should().Equal(Iac, Sb, 33, 3, Iac, Se);
            stream.ByteWrites[1].Should().Equal(Iac, Will, 3);
            stream.ByteWrites[2].Should().Equal(Iac, Will, 0);
            stream.ByteWrites[3].Should().Equal(Iac, Do, 31);
            stream.ByteWrites[4].Should().Equal(Iac, Do, 42);
        }

        [Fact]
        public async Task SendLineflowModeAsync_AfterWillLineflow_SendsMode()
        {
            using var stream = new ScriptedStream(Iac, Will, 33);
            using var session = NewSession(stream);
            (await session.ReadAsync(TimeSpan.FromMilliseconds(500))).Should().BeEmpty();
            (await session.SendLineflowModeAsync(true)).Should().BeTrue();
            // Volunteered SB + advanced batch, then the explicit mode SB
            // (no DO reply anywhere — the WILL is recorded, not answered).
            stream.ByteWrites.Should().HaveCount(6);
            stream.ByteWrites[1].Should().Equal(Iac, Will, 3);
            stream.ByteWrites[2].Should().Equal(Iac, Will, 0);
            stream.ByteWrites[3].Should().Equal(Iac, Do, 31);
            stream.ByteWrites[4].Should().Equal(Iac, Do, 42);
            stream.ByteWrites[5].Should().Equal(Iac, Sb, 33, 2, Iac, Se);
        }

        [Fact]
        public async Task SendLineflowModeAsync_WithoutWillLineflow_SendsNothing()
        {
            using var stream = new ScriptedStream();
            using var session = NewSession(stream);
            (await session.SendLineflowModeAsync(true)).Should().BeFalse();
            stream.ByteWrites.Should().BeEmpty();
        }
    }
}
