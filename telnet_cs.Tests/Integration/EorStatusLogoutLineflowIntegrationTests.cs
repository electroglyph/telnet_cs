// Phase 4 EOR / TIMING-MARK / STATUS / LOGOUT / LFLOW pins over a live
// pair: a real Client against a real ServerSession, both ends tapped so
// wire frames are asserted alongside endpoint state. EOR markers and the
// LOGOUT close hook are observable only as bytes / connection state (the
// receive hooks are internal), so those pins assert the wire image, not
// handler callbacks.
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
    using telnet_cs.Transport;

    public class EorStatusLogoutLineflowIntegrationTests
    {
        private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

        private static (Client Client, ServerSession Session, WireTap ClientTap, WireTap SessionTap, IDisposable Guard) CreateTappedPair(
            TelnetServerOptions? serverOptions = null,
            Action<TelnetClientOptions>? configureClient = null)
        {
            var guard = GlobalStateGuard.SkipProactive(true);
            var (clientStream, serverStream) = InMemoryPipe.Create();
            var clientTap = new WireTap(clientStream);
            var sessionTap = new WireTap(serverStream);
            var session = new ServerSession(
                sessionTap, serverOptions ?? new TelnetServerOptions(), CancellationToken.None);
            var client = new Client(clientTap, CancellationToken.None);
            configureClient?.Invoke(client.Settings);
            return (client, session, clientTap, sessionTap, guard);
        }

        [Fact]
        public async Task SendEorAsync_WithoutAgreement_ReturnsFalseSendingNothing()
        {
            var (client, session, clientTap, _, guard) = CreateTappedPair();
            using (client)
            using (session)
            using (guard)
            {
                (await client.SendEorAsync()).Should().BeFalse();
                clientTap.WrittenBytes.Should().BeEmpty();
            }
        }

        [Fact]
        public async Task SendEorAsync_AfterSessionDo_SendsBareMarker()
        {
            // The session asks for EORs (DO); the client WILLs, then its
            // marker goes out as a bare IAC EOR with no SB framing.
            var (client, session, clientTap, sessionTap, guard) = CreateTappedPair();
            using (client)
            using (session)
            using (guard)
            {
                await session.RequestEnableAsync(Options.EndOfRecord);
                await LiveExchange.PumpUntilAsync(
                    client, session,
                    () => Wire.ContainsFrame(
                        clientTap.WrittenBytes, Wire.Iac, Wire.Will, (byte)(int)Options.EndOfRecord),
                    Budget);

                (await client.SendEorAsync()).Should().BeTrue();
                await LiveExchange.PumpUntilAsync(
                    client, session,
                    () => Wire.ContainsFrame(clientTap.WrittenBytes, Wire.Iac, 239),
                    Budget);

                // The marker is written by the client, so it is the client
                // tap that carries it (taps record writes, not reads).
                Wire.ContainsFrame(clientTap.WrittenBytes, Wire.Iac, 239).Should().BeTrue();
            }
        }

        [Fact]
        public async Task SendTimingMarkAsync_RoundTrip_AgreesBothSidesAndRepings()
        {
            // Client DO TM: the session answers WILL (a bare stateless
            // reply — TM is a ping, not an agreement, so the session mirror
            // never latches), the client records the peer, and a repeat
            // after agreement goes out again instead of being suppressed.
            var (client, session, clientTap, sessionTap, guard) = CreateTappedPair();
            using (client)
            using (session)
            using (guard)
            {
                await client.SendTimingMarkAsync();
                await LiveExchange.PumpUntilAsync(
                    client, session,
                    () => Wire.ContainsFrame(
                        sessionTap.WrittenBytes, Wire.Iac, Wire.Will, (byte)(int)Options.TimingMark)
                        && client.Negotiation.IsEnabledByPeer((int)Options.TimingMark),
                    Budget);

                Wire.ContainsFrame(
                    sessionTap.WrittenBytes, Wire.Iac, Wire.Will, (byte)(int)Options.TimingMark)
                    .Should().BeTrue();
                client.Negotiation.IsEnabledByPeer((int)Options.TimingMark).Should().BeTrue();

                await client.SendTimingMarkAsync();
                await LiveExchange.PumpForAsync(client, session, TimeSpan.FromMilliseconds(300));
                Wire.CountFrames(clientTap.WrittenBytes, Wire.Iac, Wire.Do, (byte)(int)Options.TimingMark)
                    .Should().Be(2);
            }
        }

        [Fact]
        public async Task Status_SessionDo_ClientVolunteersSnapshotReportLands()
        {
            // RFC 859 initiation live, server-solicited shape: the session's
            // DO draws the client's WILL plus an immediate voluntary IS
            // snapshot (no SEND needed); the session files it as
            // PeerStatusReport. EOR is agreed first so the snapshot has a
            // deterministic row: the reference renders every touched option,
            // and a bare STATUS-only exchange volunteers an empty-but-filed
            // snapshot.
            var (client, session, _, _, guard) = CreateTappedPair();
            using (client)
            using (session)
            using (guard)
            {
                await session.RequestEnableAsync(Options.EndOfRecord);
                await LiveExchange.PumpUntilAsync(
                    client, session,
                    () => client.Negotiation.IsEnabledByUs((int)Options.EndOfRecord),
                    Budget);

                await session.RequestEnableAsync(Options.Status);
                await LiveExchange.PumpUntilAsync(
                    client, session, () => session.PeerStatusReport is not null, Budget);

                session.PeerStatusReport.Should().NotBeNull();
                session.PeerStatusReport.Should().Contain(
                    item => item.Verb == Commands.Will && item.Option == (byte)(int)Options.EndOfRecord);
                session.Negotiation.IsEnabledByPeer((int)Options.Status).Should().BeTrue();
                client.Negotiation.IsEnabledByUs((int)Options.Status).Should().BeTrue();
            }
        }

        [Fact]
        public async Task Logout_DoReceived_ClosesSession()
        {
            // Server role: the session closes its stream on DO LOGOUT; only
            // the close is observable live (the hook is internal).
            var (client, session, _, _, guard) = CreateTappedPair();
            using (client)
            using (session)
            using (guard)
            {
                await client.RequestEnableAsync(Options.Logout);
                await LiveExchange.PumpUntilAsync(
                    client, session, () => !session.IsConnected, Budget);

                session.IsConnected.Should().BeFalse();
            }
        }

        [Fact]
        public async Task Logout_SessionDo_IgnoredSilentlyByClient()
        {
            // Client role: DO LOGOUT earns no reply and no close on either
            // end (reference: the client end raises instead).
            var (client, session, clientTap, _, guard) = CreateTappedPair();
            using (client)
            using (session)
            using (guard)
            {
                await session.RequestEnableAsync(Options.Logout);
                await LiveExchange.PumpForAsync(client, session, TimeSpan.FromSeconds(1));

                session.IsConnected.Should().BeTrue();
                client.IsConnected.Should().BeTrue();
                Wire.ContainsFrame(clientTap.WrittenBytes, Wire.Iac, Wire.Will, (byte)(int)Options.Logout)
                    .Should().BeFalse();
                Wire.ContainsFrame(clientTap.WrittenBytes, Wire.Iac, Wire.Wont, (byte)(int)Options.Logout)
                    .Should().BeFalse();
            }
        }

        [Fact]
        public async Task SendLineflowModeAsync_GatedOnPeerWillThenSendsMode()
        {
            // Server-only guard live: no peer WILL means false with nothing
            // sent; after the client answers the session's DO with WILL, the
            // RESTART_ANY mode SB reaches the client. The mode frame is
            // written by the session, so it is the session tap that carries
            // it (taps record writes, not reads).
            var (client, session, _, sessionTap, guard) = CreateTappedPair();
            using (client)
            using (session)
            using (guard)
            {
                (await session.SendLineflowModeAsync(restartOnAny: true)).Should().BeFalse();

                await session.RequestEnableAsync(Options.RemoteFlowControl);
                await LiveExchange.PumpUntilAsync(
                    client, session,
                    () => session.Negotiation.IsEnabledByPeer((int)Options.RemoteFlowControl),
                    Budget);

                (await session.SendLineflowModeAsync(restartOnAny: true)).Should().BeTrue();
                await LiveExchange.PumpUntilAsync(
                    client, session,
                    () => Wire.ContainsFrame(
                        sessionTap.WrittenBytes,
                        Wire.Iac, Wire.Sb, (byte)(int)Options.RemoteFlowControl,
                        LineflowProtocol.RestartAny, Wire.Iac, Wire.Se),
                    Budget);

                Wire.ContainsFrame(
                    sessionTap.WrittenBytes,
                    Wire.Iac, Wire.Sb, (byte)(int)Options.RemoteFlowControl,
                    LineflowProtocol.RestartAny, Wire.Iac, Wire.Se).Should().BeTrue();
            }
        }
    }
}
