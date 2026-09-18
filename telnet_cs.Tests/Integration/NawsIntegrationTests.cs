// Phase 4 NAWS pins over a live pair: agreement lands the client's
// size on the session, RefreshWindowSizeAsync re-announces a resize, a
// 0x0 size arrives as-is (RFC 1073 "unspecified"), and out-of-range
// dimensions clamp to the 0-65535 wire range.
namespace telnet_cs.Tests
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using FluentAssertions;
    using Xunit;
    using telnet_cs.Client;
    using telnet_cs.Protocol;
    using telnet_cs.Server;

    public class NawsIntegrationTests
    {
        private static async Task<(Client Client, ServerSession Session, IDisposable Guard)> OpenNawsAgreedPairAsync(
            Action<TelnetClientOptions>? configureClient = null)
        {
            var pair = await LiveExchange.CreatePairAsync(null, configureClient);
            await pair.Session.SendOpeningPresetAsync(CancellationToken.None);
            await LiveExchange.PumpUntilAsync(
                pair.Client,
                pair.Session,
                () => pair.Session.ClientWindowSize.HasValue,
                TimeSpan.FromSeconds(10));
            return pair;
        }

        [Fact]
        public async Task NegotiateLive_ConfiguredSize_LandsOnSession()
        {
            var pair = await OpenNawsAgreedPairAsync(o =>
            {
                o.WindowWidth = 132;
                o.WindowHeight = 50;
            });
            using (pair.Client)
            using (pair.Session)
            using (pair.Guard)
            {
                pair.Session.Negotiation.IsEnabledByPeer((int)Options.WindowSize).Should().BeTrue();
                pair.Session.ClientWindowSize.Should().Be(((ushort)132, (ushort)50));
            }
        }

        [Fact]
        public async Task RefreshWindowSizeAsync_Resize_ReannouncesToSession()
        {
            var pair = await OpenNawsAgreedPairAsync(o =>
            {
                o.WindowWidth = 100;
                o.WindowHeight = 30;
            });
            using (pair.Client)
            using (pair.Session)
            using (pair.Guard)
            {
                pair.Session.ClientWindowSize.Should().Be(((ushort)100, (ushort)30));

                pair.Client.Settings.WindowWidth = 132;
                pair.Client.Settings.WindowHeight = 50;
                await pair.Client.RefreshWindowSizeAsync(CancellationToken.None);
                await LiveExchange.PumpUntilAsync(
                    pair.Client,
                    pair.Session,
                    () => pair.Session.ClientWindowSize == ((ushort)132, (ushort)50),
                    TimeSpan.FromSeconds(10));
                pair.Session.ClientWindowSize.Should().Be(((ushort)132, (ushort)50));
            }
        }

        [Fact]
        public async Task NegotiateLive_ZeroSize_SentAsIs()
        {
            var pair = await OpenNawsAgreedPairAsync(o =>
            {
                o.WindowWidth = 0;
                o.WindowHeight = 0;
            });
            using (pair.Client)
            using (pair.Session)
            using (pair.Guard)
            {
                pair.Session.ClientWindowSize.Should().Be(((ushort)0, (ushort)0));
            }
        }

        [Fact]
        public async Task NegotiateLive_OutOfRangeSize_ClampsToWireRange()
        {
            var pair = await OpenNawsAgreedPairAsync(o =>
            {
                o.WindowWidth = 100000;
                o.WindowHeight = -5;
            });
            using (pair.Client)
            using (pair.Session)
            using (pair.Guard)
            {
                pair.Session.ClientWindowSize.Should().Be(((ushort)65535, (ushort)0));
            }
        }
    }
}
