// Phase 4 ENVIRON pins over a live pair: a real Client answers the
// session's NEW/OLD_ENVIRON SENDs, spontaneous INFO updates land in the
// session store, DISPLAY never rides SB answers, and MaxEnvironVars keeps
// the first entries. Keys are upper-cased and empty values dropped by the
// session store, so assertions use that shape.
namespace telnet_cs.Tests
{
    using System;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using FluentAssertions;
    using Xunit;
    using telnet_cs.Client;
    using telnet_cs.Protocol;
    using telnet_cs.Server;

    public class EnvironIntegrationTests
    {
        private static async Task AgreeNewEnvironAsync(Client client, ServerSession session)
        {
            await session.SendOpeningPresetAsync(CancellationToken.None);
            await LiveExchange.PumpUntilAsync(
                client,
                session,
                () => session.Negotiation.IsEnabledByPeer((int)Options.TerminalType),
                TimeSpan.FromSeconds(10));
            session.Negotiation.IsEnabledByPeer((int)Options.TerminalType).Should().BeTrue();
            await LiveExchange.PumpUntilAsync(
                client,
                session,
                () => session.Negotiation.IsEnabledByPeer((int)Options.NewEnvironment),
                TimeSpan.FromSeconds(10));
            session.Negotiation.IsEnabledByPeer((int)Options.NewEnvironment).Should().BeTrue();
        }

        [Fact]
        public async Task RequestNewEnvironment_LiveExchange_ReturnsUserAndUserVars()
        {
            var pair = await LiveExchange.CreatePairAsync(null, o =>
            {
                o.EnvironmentUser = "liveuser";
                o.EnvironmentUserVars["LIVEVAR"] = "liveval";
            });
            using (pair.Client)
            using (pair.Session)
            using (pair.Guard)
            {
                await AgreeNewEnvironAsync(pair.Client, pair.Session);
                var result = await LiveExchange.CollectAsync(
                    pair.Session.RequestNewEnvironmentAsync(TimeSpan.FromSeconds(5)),
                    pair.Client,
                    TimeSpan.FromSeconds(10));

                result.Should().Contain(kv => kv.Key == "USER" && kv.Value == "liveuser");
                result.Should().Contain(kv => kv.Key == "LIVEVAR" && kv.Value == "liveval");
                pair.Session.ClientNewEnvironment.Should().Contain(kv => kv.Key == "USER" && kv.Value == "liveuser");
            }
        }

        [Fact]
        public async Task NewEnviron_SbAnswer_NeverCarriesDisplay()
        {
            // Even with EnvironmentDisplay configured, the SB IS carries no
            // DISPLAY (empty values are dropped by the session store); the
            // value only rides spontaneous INFO.
            var pair = await LiveExchange.CreatePairAsync(null, o => o.EnvironmentDisplay = "livehost:0");
            using (pair.Client)
            using (pair.Session)
            using (pair.Guard)
            {
                await AgreeNewEnvironAsync(pair.Client, pair.Session);
                var result = await LiveExchange.CollectAsync(
                    pair.Session.RequestNewEnvironmentAsync(TimeSpan.FromSeconds(5)),
                    pair.Client,
                    TimeSpan.FromSeconds(10));

                result.Should().NotContainKey("DISPLAY");
            }
        }

        [Fact]
        public async Task RequestEnvironment_OldFormRoundTrips()
        {
            var pair = await LiveExchange.CreatePairAsync(null, o => o.EnvironmentUser = "olduser");
            using (pair.Client)
            using (pair.Session)
            using (pair.Guard)
            {
                await pair.Session.SendOpeningPresetAsync(CancellationToken.None);
                await pair.Session.RequestEnableAsync(Options.OldEnvironment, CancellationToken.None);
                await LiveExchange.PumpUntilAsync(
                    pair.Client,
                    pair.Session,
                    () => pair.Session.Negotiation.IsEnabledByPeer((int)Options.OldEnvironment),
                    TimeSpan.FromSeconds(10));
                pair.Session.Negotiation.IsEnabledByPeer((int)Options.OldEnvironment).Should().BeTrue();

                var result = await LiveExchange.CollectAsync(
                    pair.Session.RequestEnvironmentAsync(TimeSpan.FromSeconds(5)),
                    pair.Client,
                    TimeSpan.FromSeconds(10));

                result.Should().Contain(kv => kv.Key == "USER" && kv.Value == "olduser");
                pair.Session.ClientEnvironment.Should().Contain(kv => kv.Key == "USER" && kv.Value == "olduser");
            }
        }

        [Fact]
        public async Task SpontaneousInfo_ChangedValue_LandsInClientEnvironment()
        {
            var pair = await LiveExchange.CreatePairAsync(null, o => o.EnvironmentUser = "liveuser");
            using (pair.Client)
            using (pair.Session)
            using (pair.Guard)
            {
                await AgreeNewEnvironAsync(pair.Client, pair.Session);
                await LiveExchange.PumpUntilAsync(
                    pair.Client,
                    pair.Session,
                    () => pair.Session.ClientNewEnvironment.Any(kv => kv.Key == "USER" && kv.Value == "liveuser"),
                    TimeSpan.FromSeconds(10));
                pair.Session.ClientNewEnvironment.Should().Contain(kv => kv.Key == "USER" && kv.Value == "liveuser");

                pair.Client.Settings.EnvironmentUser = "changed";
                await LiveExchange.PumpUntilAsync(
                    pair.Client,
                    pair.Session,
                    () => pair.Session.ClientNewEnvironment.Any(kv => kv.Key == "USER" && kv.Value == "changed"),
                    TimeSpan.FromSeconds(10));

                pair.Session.ClientNewEnvironment.Should().Contain(kv => kv.Key == "USER" && kv.Value == "changed");
            }
        }

        [Fact]
        public async Task EnvironCap_MaxEnvironVars_KeepsFirstEntries()
        {
            var options = new TelnetServerOptions { MaxEnvironVars = 2 };
            var pair = await LiveExchange.CreatePairAsync(options, o =>
            {
                o.EnvironmentUser = "liveuser";
                o.EnvironmentUserVars["VAR_A"] = "a";
                o.EnvironmentUserVars["VAR_B"] = "b";
                o.EnvironmentUserVars["VAR_C"] = "c";
            });
            using (pair.Client)
            using (pair.Session)
            using (pair.Guard)
            {
                await AgreeNewEnvironAsync(pair.Client, pair.Session);
                var result = await LiveExchange.CollectAsync(
                    pair.Session.RequestNewEnvironmentAsync(TimeSpan.FromSeconds(5)),
                    pair.Client,
                    TimeSpan.FromSeconds(10));

                result.Count.Should().BeLessThanOrEqualTo(2);
                result.Should().Contain(kv => kv.Key == "USER" && kv.Value == "liveuser");
            }
        }
    }
}
