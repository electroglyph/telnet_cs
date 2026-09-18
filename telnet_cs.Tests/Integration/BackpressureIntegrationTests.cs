// Phase 4 backpressure + storm-shedding pins over a live pair: an
// oversized ENVIRON answer trips the value cap, repeat refused requests
// draw a single WONT (remembered-refusal dup drop), and a tiny
// decompression cap fails an MCCP3 stream while the session survives. All
// assert the log-code contract alongside endpoint survival; crafted-burst
// internals (100fps trip, corrupt-path refusal bytes) stay scripted —
// they need raw frame timing no public client API can emit.
namespace telnet_cs.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using FluentAssertions;
    using Xunit;
    using telnet_cs.Client;
    using telnet_cs.Protocol;
    using telnet_cs.Server;
    using telnet_cs.Transport;

    public class BackpressureIntegrationTests
    {
        private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

        private static async Task<(Client Client, ServerSession Session, WireTap ClientTap, WireTap SessionTap, IDisposable Guard, List<string> Logs)> CreateLoggedPairAsync(
            TelnetServerOptions? serverOptions = null,
            Action<TelnetClientOptions>? configureClient = null)
        {
            var logs = new List<string>();
            var guard = GlobalStateGuard.SkipProactive(true);
            var (clientStream, serverStream) = InMemoryPipe.Create();
            var clientTap = new WireTap(clientStream);
            var sessionTap = new WireTap(serverStream);
            var options = serverOptions ?? new TelnetServerOptions();
            var prior = options.Log;
            options.Log = m => { lock (logs) { logs.Add(m); } prior?.Invoke(m); };
            var session = new ServerSession(sessionTap, options, CancellationToken.None);
            var client = await Client.CreateAsync(clientTap, TimeSpan.FromSeconds(30), CancellationToken.None);
            configureClient?.Invoke(client.Settings);
            return (client, session, clientTap, sessionTap, guard, logs);
        }

        private static bool HasPrefix(List<string> logs, string prefix)
        {
            lock (logs)
            {
                foreach (var m in logs)
                {
                    if (m.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        [Fact]
        public async Task EnvironCap_OversizedClientAnswer_LogsAndSurvives()
        {
            var options = new TelnetServerOptions { MaxEnvironValueChars = 4 };
            var (client, session, _, _, guard, logs) = await CreateLoggedPairAsync(
                options, o => o.EnvironmentUserVars["BIGVAR"] = new string('x', 100));
            using (client)
            using (session)
            using (guard)
            {
                await session.SendOpeningPresetAsync(CancellationToken.None);
                await LiveExchange.PumpUntilAsync(
                    client, session,
                    () => session.Negotiation.IsEnabledByPeer((int)Options.NewEnvironment),
                    Budget);
                session.Negotiation.IsEnabledByPeer((int)Options.NewEnvironment).Should().BeTrue();

                await LiveExchange.CollectAsync(
                    session.RequestNewEnvironmentAsync(TimeSpan.FromSeconds(5)),
                    client,
                    Budget);
                HasPrefix(logs, "environ-cap:").Should().BeTrue();

                // The session survives the cap: text still flows.
                await session.WriteAsync("alive", CancellationToken.None);
                (await client.ReadAsync(TimeSpan.FromSeconds(5))).Should().Be("alive");
            }
        }

        [Fact]
        public async Task RefusedOption_RepeatRequests_DrawSingleWont()
        {
            // Storm shedding without a crafted burst: five DO ECHOs
            // against OfferEcho=false draw exactly one WONT — the
            // remembered refusal drops the dups. (The 100fps log-trip half
            // needs a raw frame burst no public client API can emit, so it
            // stays scripted.)
            var options = new TelnetServerOptions { OfferEcho = false };
            var (client, session, _, sessionTap, guard, _) = await CreateLoggedPairAsync(options);
            using (client)
            using (session)
            using (guard)
            {
                for (int i = 0; i < 5; i++)
                {
                    await client.RequestEnableAsync(Options.Echo, CancellationToken.None);
                }

                await LiveExchange.PumpUntilAsync(
                    client, session,
                    () => Wire.CountFrames(sessionTap.WrittenBytes, Wire.Iac, Wire.Wont, (byte)(int)Options.Echo) > 0,
                    Budget);
                await LiveExchange.PumpForAsync(client, session, TimeSpan.FromSeconds(1));
                Wire.CountFrames(sessionTap.WrittenBytes, Wire.Iac, Wire.Wont, (byte)(int)Options.Echo).Should().Be(1);
            }
        }

        [Fact]
        public async Task DecompressionCap_TinyCap_FailsMccp3StreamLive()
        {
            // MCCP3 direction (the client compresses, the session
            // inflates): a 10-byte outstanding cap trips on the first real
            // chunk, the stream fails, and WONT goes out.
            var options = new TelnetServerOptions { OfferMccp3 = true, MaxDecompressedBytes = 10 };
            var (client, session, clientTap, _, guard, logs) = await CreateLoggedPairAsync(options);
            using (client)
            using (session)
            using (guard)
            {
                await session.SendOpeningPresetAsync(CancellationToken.None);
                await LiveExchange.PumpUntilAsync(
                    client, session,
                    () => session.Negotiation.IsEnabledByUs((int)Options.Mccp3)
                        && client.Negotiation.IsEnabledByPeer((int)Options.Mccp3),
                    Budget);
                await LiveExchange.PumpUntilAsync(
                    client, session,
                    () => Wire.ContainsFrame(clientTap.WrittenBytes, Wire.Iac, Wire.Sb, (byte)(int)Options.Mccp3, Wire.Iac, Wire.Se),
                    Budget);

                await client.WriteAsync("mccp3-over-tiny-cap-" + new string('z', 300), CancellationToken.None);
                await LiveExchange.PumpUntilAsync(
                    client, session,
                    () => HasPrefix(logs, "mccp-output-cap:"),
                    Budget);
                HasPrefix(logs, "mccp-output-cap:").Should().BeTrue();

                // The session survives the failed stream: its own writes
                // still reach the peer as plaintext.
                // (The corrupt-path DONT goes out at an async flush point of
                // the detecting read, which a live read boundary can strand
                // handler-locally; that shape stays scripted, where one read
                // detects and flushes deterministically.)
                await session.WriteAsync("alive", CancellationToken.None);
                (await client.ReadAsync(TimeSpan.FromSeconds(5))).Should().Be("alive");
            }
        }
    }
}
