// Public-surface pins for InMemoryPipe.Create(): the hermetic pair behind
// the public name honors the IByteStream contract and carries a live
// client<->server exchange with no sockets. Transport edge semantics
// (timeout exceptions, drain-before-EOF) stay pinned on
// DuplexIntegrationTests; this file only proves the public entry point
// delivers the same linked pair.
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

    public class InMemoryPipeTests
    {
        [Fact]
        public async Task Create_BytesWrittenOnA_ArriveOnBAndViceVersa()
        {
            var (a, b) = InMemoryPipe.Create();
            using (a)
            using (b)
            {
                await a.WriteAsync(new byte[] { 65, 66 }, 0, 2, CancellationToken.None);
                b.Available.Should().Be(2);
                b.ReadByte().Should().Be(65);
                b.ReadByte().Should().Be(66);

                await b.WriteByteAsync(42, CancellationToken.None);
                a.Available.Should().Be(1);
                a.ReadByte().Should().Be(42);
            }
        }

        [Fact]
        public async Task Create_ClientServer_RealSessionsExchangeText()
        {
            // Both ends are full IByteStream implementations: two live
            // sessions read and write across the public pair.
            var (clientStream, serverStream) = InMemoryPipe.Create();
            using var writer = new ServerSession(serverStream, new TelnetServerOptions(), CancellationToken.None);
            using var reader = new ServerSession(clientStream, new TelnetServerOptions(), CancellationToken.None);
            await writer.WriteAsync("hello", CancellationToken.None);
            (await reader.ReadAsync(TimeSpan.FromSeconds(5))).Should().Be("hello");
        }

        [Fact]
        public async Task Create_ClientServer_NegotiatesTtype()
        {
            // A real Client against a real ServerSession completes TTYPE
            // agreement over the public pair. The client only answers while
            // its read path is driven, so both ends are pumped with a
            // bounded wait.
            using (GlobalStateGuard.SkipProactive(true))
            {
                var (clientStream, serverStream) = InMemoryPipe.Create();
                using var session = new ServerSession(serverStream, new TelnetServerOptions(), CancellationToken.None);
                using var client = new Client(clientStream, CancellationToken.None);
                await session.SendOpeningPresetAsync(CancellationToken.None);

                var sw = Stopwatch.StartNew();
                while (!session.Negotiation.IsEnabledByPeer((int)Options.TerminalType) && sw.Elapsed < TimeSpan.FromSeconds(10))
                {
                    await client.ReadAsync(TimeSpan.FromMilliseconds(50));
                    await session.ReadAsync(TimeSpan.FromMilliseconds(50));
                }

                session.Negotiation.IsEnabledByPeer((int)Options.TerminalType).Should().BeTrue();
            }
        }

        [Fact]
        public async Task Create_CloseOnOneEnd_PeerSeesEndOfStream()
        {
            var (a, b) = InMemoryPipe.Create();
            using (a)
            using (b)
            {
                await a.WriteAsync(new byte[] { 7 }, 0, 1, CancellationToken.None);
                a.Close();

                // The in-flight byte drains before end-of-stream.
                b.ReadByte().Should().Be(7);
                b.ReadByte().Should().Be(-1);
            }
        }
    }
}
