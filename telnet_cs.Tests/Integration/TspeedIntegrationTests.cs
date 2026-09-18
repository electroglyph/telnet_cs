// Phase 4 TSPEED pins over a live pair: the session sends DO TSPEED,
// the real Client WILLs it, and RequestTerminalSpeedAsync collects the
// IS — default value, zero-stripping, RFC 1079 <tx>,<rx> asymmetry, and
// strict digit validation (malformed client setting yields null).
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

    public class TspeedIntegrationTests
    {
        private static async Task<(Client Client, ServerSession Session, IDisposable Guard)> OpenSpeedAgreedPairAsync(
            Action<TelnetClientOptions>? configureClient = null)
        {
            var pair = await LiveExchange.CreatePairAsync(null, configureClient);
            await pair.Session.RequestEnableAsync(Options.TerminalSpeed, CancellationToken.None);
            await LiveExchange.PumpUntilAsync(
                pair.Client,
                pair.Session,
                () => pair.Session.Negotiation.IsEnabledByPeer((int)Options.TerminalSpeed),
                TimeSpan.FromSeconds(10));
            pair.Session.Negotiation.IsEnabledByPeer((int)Options.TerminalSpeed).Should().BeTrue();
            return pair;
        }

        [Fact]
        public async Task RequestTerminalSpeedAsync_DefaultClient_Returns38400Pair()
        {
            var pair = await OpenSpeedAgreedPairAsync();
            using (pair.Client)
            using (pair.Session)
            using (pair.Guard)
            {
                var collect = pair.Session.RequestTerminalSpeedAsync(TimeSpan.FromSeconds(10));
                (await LiveExchange.CollectAsync(collect, pair.Client, TimeSpan.FromSeconds(15)))
                    .Should().Be("38400,38400");
            }
        }

        [Fact]
        public async Task RequestTerminalSpeedAsync_LeadingZerosAndWhitespace_Stripped()
        {
            var pair = await OpenSpeedAgreedPairAsync(o => o.TerminalSpeed = " 038400,0038400 ");
            using (pair.Client)
            using (pair.Session)
            using (pair.Guard)
            {
                var collect = pair.Session.RequestTerminalSpeedAsync(TimeSpan.FromSeconds(10));
                (await LiveExchange.CollectAsync(collect, pair.Client, TimeSpan.FromSeconds(15)))
                    .Should().Be("38400,38400");
            }
        }

        [Fact]
        public async Task RequestTerminalSpeedAsync_AsymmetricSpeeds_TxFirstPerRfc1079()
        {
            var pair = await OpenSpeedAgreedPairAsync(o => o.TerminalSpeed = "9600,38400");
            using (pair.Client)
            using (pair.Session)
            using (pair.Guard)
            {
                var collect = pair.Session.RequestTerminalSpeedAsync(TimeSpan.FromSeconds(10));
                (await LiveExchange.CollectAsync(collect, pair.Client, TimeSpan.FromSeconds(15)))
                    .Should().Be("9600,38400");
            }
        }

        [Fact]
        public async Task RequestTerminalSpeedAsync_MalformedClientSetting_YieldsNull()
        {
            var pair = await OpenSpeedAgreedPairAsync(o => o.TerminalSpeed = "fast,38400");
            using (pair.Client)
            using (pair.Session)
            using (pair.Guard)
            {
                var collect = pair.Session.RequestTerminalSpeedAsync(TimeSpan.FromSeconds(2));
                (await LiveExchange.CollectAsync(collect, pair.Client, TimeSpan.FromSeconds(10)))
                    .Should().BeNull();
            }
        }
    }
}
