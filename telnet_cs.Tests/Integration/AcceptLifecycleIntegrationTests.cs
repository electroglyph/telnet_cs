// Phase 4 accept-lifecycle pins (loopback): restart rebinds the same
// port and concurrent clients each get a session. Sequential accept,
// raw-close, and Stop-closes-sessions stay on ServerAcceptTests.
namespace telnet_cs.Tests
{
    using System;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using FluentAssertions;
    using Xunit;
    using telnet_cs.Client;
    using telnet_cs.Server;

    public class AcceptLifecycleIntegrationTests
    {
        [Fact]
        public async Task AcceptSessionAsync_Restart_RebindsSamePort()
        {
            int port;
            using (var first = new TelnetServer(0))
            {
                first.Start();
                port = first.Port;
                port.Should().BeInRange(1, 65535);
                first.Stop();
            }

            // An explicit port rebinds after stop: the restart serves the
            // same port and accepts again.
            using var server = new TelnetServer(port);
            server.Start();
            server.Port.Should().Be(port);
            var acceptTask = server.AcceptSessionAsync(CancellationToken.None);
            using var client = await Client.ConnectAsync("127.0.0.1", server.Port);
            using var session = await acceptTask;
            session.IsConnected.Should().BeTrue();
            client.IsConnected.Should().BeTrue();
        }

        [Fact]
        public async Task AcceptSessionAsync_ConcurrentClients_EachGetsASession()
        {
            const int clients = 3;
            using var server = new TelnetServer(0);
            server.Start();
            var acceptTasks = Enumerable.Range(0, clients)
                .Select(_ => server.AcceptSessionAsync(CancellationToken.None))
                .ToArray();
            var connected = new Client[clients];
            try
            {
                for (int i = 0; i < clients; i++)
                {
                    connected[i] = await Client.ConnectAsync("127.0.0.1", server.Port);
                }

                var completed = await Task.WhenAll(acceptTasks).WaitAsync(TimeSpan.FromSeconds(10));
                completed.Should().HaveCount(clients);
                foreach (var session in completed)
                {
                    session.IsConnected.Should().BeTrue();
                    session.Dispose();
                }

                foreach (var client in connected)
                {
                    client.IsConnected.Should().BeTrue();
                }
            }
            finally
            {
                foreach (var client in connected)
                {
                    client?.Dispose();
                }
            }
        }

    }
}
