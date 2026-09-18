// Phase 4 timeout + background-pump pins over a live pair: idle-close
// with notice, traffic re-arm, SetTimeout without an activity stamp, the
// handshake deadline, and the pump answering/buffering for a real Client
// while nobody reads. The scripted pins cover the same shapes against
// ScriptedStream; only the live transport (timers vs real bytes) is new.
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

    public class TimeoutPumpIntegrationTests
    {
        private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

        private static async Task<(Client Client, ServerSession Session, IDisposable Guard)> CreateTimeoutPairAsync(
            TelnetServerOptions? serverOptions = null)
        {
            var guard = GlobalStateGuard.SkipProactive(true);
            var (clientStream, serverStream) = InMemoryPipe.Create();
            var session = new ServerSession(
                serverStream, serverOptions ?? new TelnetServerOptions(), CancellationToken.None);
            var client = await Client.CreateAsync(clientStream, TimeSpan.FromSeconds(30), CancellationToken.None);
            return (client, session, guard);
        }

        [Fact]
        public async Task IdleTimeout_QuietPair_ClosesWithNotice()
        {
            var options = new TelnetServerOptions { IdleTimeout = TimeSpan.FromMilliseconds(300) };
            var (client, session, guard) = await CreateTimeoutPairAsync(options);
            using (client)
            using (session)
            using (guard)
            {
                (await client.TerminatedReadAsync("Timeout.", Budget)).Should().Contain("Timeout.");
                session.IsConnected.Should().BeFalse();
                session.IsIdleTimedOut.Should().BeTrue();
            }
        }

        [Fact]
        public async Task IdleTimeout_TrafficRearmsDeadline()
        {
            var options = new TelnetServerOptions { IdleTimeout = TimeSpan.FromMilliseconds(500) };
            var (client, session, guard) = await CreateTimeoutPairAsync(options);
            using (client)
            using (session)
            using (guard)
            {
                // Three traffic bursts inside the window: still alive well
                // past the first deadline, then closes once quiet.
                for (int i = 0; i < 3; i++)
                {
                    await client.WriteAsync("ping", CancellationToken.None);
                    (await session.ReadAsync(TimeSpan.FromSeconds(5))).Should().Be("ping");
                    await Task.Delay(150);
                }

                session.IsConnected.Should().BeTrue();
                (await client.TerminatedReadAsync("Timeout.", Budget)).Should().Contain("Timeout.");
                session.IsConnected.Should().BeFalse();
            }
        }

        [Fact]
        public async Task TimeoutSet_RearmsWithoutActivityStamp()
        {
            var options = new TelnetServerOptions { IdleTimeout = Timeout.InfiniteTimeSpan };
            var (client, session, guard) = await CreateTimeoutPairAsync(options);
            using (client)
            using (session)
            using (guard)
            {
                // Arming a timeout is not itself peer activity.
                var before = session.Context.LastActivityUtc;
                session.Timeout = TimeSpan.FromMilliseconds(300);
                session.Timeout.Should().Be(TimeSpan.FromMilliseconds(300));
                session.Context.LastActivityUtc.Should().Be(before);

                (await client.TerminatedReadAsync("Timeout.", Budget)).Should().Contain("Timeout.");
                session.IsConnected.Should().BeFalse();
            }
        }

        [Fact]
        public async Task HandshakeTimeout_SilentPeer_ClosesWithNotice()
        {
            var options = new TelnetServerOptions { HandshakeTimeout = TimeSpan.FromMilliseconds(400) };
            var (client, session, guard) = await CreateTimeoutPairAsync(options);
            using (client)
            using (session)
            using (guard)
            {
                (await client.TerminatedReadAsync("Handshake timeout.", Budget)).Should().Contain("Handshake timeout.");
                session.IsConnected.Should().BeFalse();
            }
        }

        [Fact]
        public async Task HandshakeTimeout_FirstRead_CompletesAndSurvives()
        {
            var options = new TelnetServerOptions { HandshakeTimeout = TimeSpan.FromMilliseconds(400) };
            var (client, session, guard) = await CreateTimeoutPairAsync(options);
            using (client)
            using (session)
            using (guard)
            {
                // Any non-empty read completes the handshake: the deadline
                // passes quietly afterwards.
                await client.WriteAsync("hi", CancellationToken.None);
                (await session.ReadAsync(TimeSpan.FromSeconds(5))).Should().Be("hi");
                await Task.Delay(800);
                session.IsConnected.Should().BeTrue();
            }
        }

        [Fact]
        public async Task Pump_NoReader_AdvancesTtypeForRealClient()
        {
            // The session side never issues a single read, yet the pump
            // advances the TTYPE exchange against a real Client: the
            // opening DO draws the client's WILL, the pump's SEND probe
            // draws IS answers, and the chain lands — the live form of the
            // raw-wire PumpBackground pin.
            var (client, session, guard) = await CreateTimeoutPairAsync();
            using (client)
            using (session)
            using (guard)
            {
                await session.SendOpeningPresetAsync(CancellationToken.None);
                await LiveExchange.PumpUntilAsync(
                    client, session,
                    () => session.ClientTerminalTypes.Count > 0,
                    Budget,
                    pumpSession: false);
                session.ClientTerminalTypes.Should().NotBeEmpty();
            }
        }

        [Fact]
        public async Task Pump_NoReader_BuffersTextForNextRead()
        {
            // Pump-buffered text merges into the next explicit ReadAsync:
            // never lost, never duplicated.
            var (client, session, guard) = await CreateTimeoutPairAsync();
            using (client)
            using (session)
            using (guard)
            {
                await client.WriteAsync("buffered", CancellationToken.None);
                await Task.Delay(1000);
                (await session.ReadAsync(TimeSpan.FromSeconds(5))).Should().Be("buffered");
                (await session.ReadAsync(TimeSpan.FromMilliseconds(300))).Should().BeEmpty();
            }
        }
    }
}
