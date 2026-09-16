// Phase 4 COMPORT + MUD-option pins over a live pair: the session
// solicits (DO) and the client answers from its accept policy. The server
// agrees every MUD option and COMPORT by default (handler defaults flow
// straight through); the client agrees COMPORT + GMCP + ZMP by default and
// gates the rest on EnableMudOptions. Payload paths (GMCP hello, ZMP
// ident/check/support, MSDP/MSSP stores, hostile truncations) stay
// scripted — no public API emits MUD subnegotiations, so there is no live
// way to put one on the wire. MTTS detection is a TTYPE-chain property,
// pinned live in TtypeChainIntegrationTests via the effective type.
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

    public class ComPortMudIntegrationTests
    {
        private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

        [Fact]
        public async Task ComPort_SessionSolicits_AgreesBothSides()
        {
            var (client, session, guard) = LiveExchange.CreatePair();
            using (client)
            using (session)
            using (guard)
            {
                await session.RequestEnableAsync(Options.COMPortControl);
                await LiveExchange.PumpUntilAsync(
                    client, session,
                    () => session.Negotiation.IsEnabledByPeer((int)Options.COMPortControl)
                        && client.Negotiation.IsEnabledByUs((int)Options.COMPortControl),
                    Budget);

                session.Negotiation.IsEnabledByPeer((int)Options.COMPortControl).Should().BeTrue();
                client.Negotiation.IsEnabledByUs((int)Options.COMPortControl).Should().BeTrue();
            }
        }

        [Fact]
        public async Task ComPort_ClientOptOut_RefusedByPeerOnSession()
        {
            var (client, session, guard) = LiveExchange.CreatePair(
                configureClient: options => options.EnableComPort = false);
            using (client)
            using (session)
            using (guard)
            {
                await session.RequestEnableAsync(Options.COMPortControl);
                await LiveExchange.PumpUntilAsync(
                    client, session,
                    () => session.Negotiation.WasRefusedByPeer((int)Options.COMPortControl),
                    Budget);

                session.Negotiation.WasRefusedByPeer((int)Options.COMPortControl).Should().BeTrue();
            }
        }

        [Theory]
        [InlineData((int)Options.Gmcp)]
        [InlineData((int)Options.Zmp)]
        [InlineData((int)Options.Msdp)]
        [InlineData((int)Options.Mssp)]
        [InlineData((int)Options.Msp)]
        [InlineData((int)Options.Mxp)]
        [InlineData((int)Options.Aardwolf)]
        [InlineData((int)Options.Atcp)]
        public async Task MudOption_SessionSolicits_AgreesBothSides(int option)
        {
            // Beyond GMCP/ZMP the client needs EnableMudOptions; the session
            // agrees the whole family unconditionally.
            var (client, session, guard) = LiveExchange.CreatePair(
                configureClient: options => options.EnableMudOptions = true);
            using (client)
            using (session)
            using (guard)
            {
                await session.RequestEnableAsync((Options)option);
                await LiveExchange.PumpUntilAsync(
                    client, session,
                    () => session.Negotiation.IsEnabledByPeer(option)
                        && client.Negotiation.IsEnabledByUs(option),
                    Budget);

                session.Negotiation.IsEnabledByPeer(option).Should().BeTrue();
                client.Negotiation.IsEnabledByUs(option).Should().BeTrue();
            }
        }

        [Fact]
        public async Task MudOption_DefaultClient_DeclinesNonGmcpZmp()
        {
            // The client stack declines MSDP out of the box (EnableMudOptions
            // defaults off); GMCP/ZMP ride their own default-on switches.
            var (client, session, guard) = LiveExchange.CreatePair();
            using (client)
            using (session)
            using (guard)
            {
                await session.RequestEnableAsync(Options.Msdp);
                await LiveExchange.PumpUntilAsync(
                    client, session,
                    () => session.Negotiation.WasRefusedByPeer((int)Options.Msdp),
                    Budget);

                session.Negotiation.WasRefusedByPeer((int)Options.Msdp).Should().BeTrue();
            }
        }

        [Fact]
        public async Task MudOption_DefaultClient_AgreesGmcp()
        {
            var (client, session, guard) = LiveExchange.CreatePair();
            using (client)
            using (session)
            using (guard)
            {
                await session.RequestEnableAsync(Options.Gmcp);
                await LiveExchange.PumpUntilAsync(
                    client, session,
                    () => session.Negotiation.IsEnabledByPeer((int)Options.Gmcp)
                        && client.Negotiation.IsEnabledByUs((int)Options.Gmcp),
                    Budget);

                session.Negotiation.IsEnabledByPeer((int)Options.Gmcp).Should().BeTrue();
                client.Negotiation.IsEnabledByUs((int)Options.Gmcp).Should().BeTrue();
            }
        }
    }
}
