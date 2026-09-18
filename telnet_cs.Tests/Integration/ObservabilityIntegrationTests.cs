// Phase 4 observability pins over live endpoints: the auth-exhausted log
// fires without the secret on a real duplex exchange, and the server
// status detail line follows traffic (the idle half — at most one detail
// on no change — is pinned loopback in LogCodeContractTests). Aggregate
// cadence plus per-session change-only detail complete the row live.
namespace telnet_cs.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using FluentAssertions;
    using Xunit;
    using telnet_cs.Client;
    using telnet_cs.Server;
    using telnet_cs.Transport;

    public class ObservabilityIntegrationTests
    {
        private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

        [Fact]
        public async Task AuthExhausted_BadCredentials_LogsWithoutSecret()
        {
            var logs = new List<string>();
            var options = new TelnetServerOptions
            {
                MaxLoginAttempts = 1,
                LoginAttemptDelay = TimeSpan.Zero,
                Log = m => { lock (logs) { logs.Add(m); } },
            };
            var guard = GlobalStateGuard.SkipProactive(true);
            var (clientStream, serverStream) = InMemoryPipe.Create();
            using var session = new ServerSession(serverStream, options, CancellationToken.None);
            using var client = await Client.CreateAsync(clientStream, TimeSpan.FromSeconds(30), CancellationToken.None);
            using (guard)
            {
                var authTask = session.AuthenticateAsync(
                    (u, p) => Task.FromResult(false),
                    TimeSpan.FromSeconds(10));
                await client.TerminatedReadAsync("login: ", Budget);
                await client.WriteLineAsync("alice");
                await client.TerminatedReadAsync("Password: ", Budget);
                await client.WriteLineAsync("s3cret-pw");
                (await authTask).Should().BeFalse();

                string all;
                lock (logs)
                {
                    all = string.Concat(logs);
                }

                all.Should().Contain("auth-exhausted:");
                all.Should().Contain("bad-credentials");
                all.Should().NotContain("s3cret-pw");
            }
        }

        [Fact]
        public async Task StatusDetail_TrafficDrawsDetailLines()
        {
            // Loopback (status is server-owned cadence): with live traffic
            // between ticks, per-session detail lines flow; the aggregate
            // ticks regardless.
            var logs = new List<string>();
            var options = new TelnetServerOptions
            {
                StatusInterval = TimeSpan.FromMilliseconds(200),
                Log = m => { lock (logs) { logs.Add(m); } },
            };
            using var server = new TelnetServer(0, options);
            server.Start();
            var acceptTask = server.AcceptSessionAsync(CancellationToken.None);
            using var client = await Client.ConnectAsync("127.0.0.1", server.Port);
            using var session = await acceptTask;

            for (int i = 0; i < 5; i++)
            {
                await client.WriteAsync("tick", CancellationToken.None);
                (await session.ReadAsync(TimeSpan.FromSeconds(5))).Should().Be("tick");
                await Task.Delay(250);
            }

            int aggregates;
            int details;
            lock (logs)
            {
                aggregates = logs.Count(m => m.StartsWith("sessions=", StringComparison.Ordinal));
                details = logs.Count(m => m.Contains("(rx=", StringComparison.Ordinal));
            }

            aggregates.Should().BeGreaterThanOrEqualTo(2, "the aggregate logs every tick");
            details.Should().BeGreaterThanOrEqualTo(1, "traffic changes draw detail lines");
        }
    }
}
