// C1 master-switch pins: with DisableAllNegotiation on, the server emits
// no negotiation bytes (however many flags are set), ignores inbound
// verbs without state/reply, and still passes text. Hermetic: ScriptedStream
// for the unit pins, the DuplexPipe pair for the two-endpoint pin.
namespace telnet_cs.Tests
{
    using System;
    using System.Diagnostics;
    using System.Threading;
    using System.Threading.Tasks;
    using FluentAssertions;
    using Xunit;
    using telnet_cs.Client;
    using telnet_cs.Protocol;
    using telnet_cs.Server;
    using telnet_cs.Transport;

    public class NegotiationSwitchTests
    {
        private static TelnetServerOptions AllFlagsPlusSwitch()
        {
            return new TelnetServerOptions
            {
                DisableAllNegotiation = true,
                OfferEcho = true,
                OfferSuppressGoAhead = true,
                OfferBinary = true,
                RequestTerminalType = true,
                RequestTerminalSpeed = true,
                RequestWindowSize = true,
                RequestEnvironment = true,
                RequestXDisplay = true,
                RequestLinemode = true,
                RequestNewEnvironment = true,
                RequestSendLocation = true,
                RequestCharacterSet = true,
                EnableMccp = true,
                OfferMccp2 = true,
                OfferMccp3 = true,
            };
        }

        [Fact]
        public async Task SendOpeningPresetAsync_AllFlagsPlusSwitch_EmitsZeroBytes()
        {
            using var stream = new ScriptedStream();
            using var session = new ServerSession(stream, AllFlagsPlusSwitch(), CancellationToken.None);
            await session.SendOpeningPresetAsync(CancellationToken.None);
            stream.ByteWrites.Should().BeEmpty();
            stream.SingleByteWrites.Should().BeEmpty();
            stream.StringWrites.Should().BeEmpty();
        }

        [Fact]
        public async Task InboundWillTtype_WithSwitch_SendsNothingAndRecordsNothing()
        {
            using var stream = new ScriptedStream(255, 251, 24);
            using var session = new ServerSession(stream, AllFlagsPlusSwitch(), CancellationToken.None);
            (await session.ReadAsync(TimeSpan.FromMilliseconds(200))).Should().BeEmpty();
            session.Negotiation.IsEnabledByPeer((int)Options.TerminalType).Should().BeFalse();
            session.ClientTerminalTypes.Should().BeEmpty();
            stream.ByteWrites.Should().BeEmpty();
            stream.SingleByteWrites.Should().BeEmpty();
        }

        [Fact]
        public async Task Duplex_DisableAllNegotiation_ZeroBytesBothDirectionsThenTextFlows()
        {
            using (GlobalStateGuard.SkipProactive(true))
            {
                var (clientStream, serverStream) = DuplexPipe.Create();
                using var session = new ServerSession(serverStream, AllFlagsPlusSwitch(), CancellationToken.None);
                using var client = new Client(clientStream, CancellationToken.None);
                await session.SendOpeningPresetAsync(CancellationToken.None);

                var sw = Stopwatch.StartNew();
                while (sw.Elapsed < TimeSpan.FromMilliseconds(300))
                {
                    await client.ReadAsync(TimeSpan.FromMilliseconds(50));
                    await session.ReadAsync(TimeSpan.FromMilliseconds(50));
                }

                serverStream.WrittenBytes.Should().BeEmpty();
                clientStream.WrittenBytes.Should().BeEmpty();

                await client.WriteAsync("hello", CancellationToken.None);
                (await session.ReadAsync(TimeSpan.FromSeconds(5))).Should().Be("hello");
            }
        }
    }
}
