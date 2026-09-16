// Hermetic client<->server pins over the in-memory DuplexPipe pair: no
// sockets, no ports. Anything asserting two live endpoints (agreement,
// interleaved reads/writes, close propagation) lives here; single-side
// scripts stay on ScriptedStream.
namespace telnet_cs.Tests
{
    using System;
    using System.Diagnostics;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using FluentAssertions;
    using Xunit;
    using telnet_cs.Client;
    using telnet_cs.Protocol;
    using telnet_cs.Server;
    using telnet_cs.Transport;

    public class DuplexIntegrationTests
    {
        [Fact]
        public async Task Duplex_WriteOnOneEnd_ReadsOnOther()
        {
            // Transport level: bytes (including a bare IAC byte, which only
            // the session layer escapes) cross in both directions.
            var (streamA, streamB) = DuplexPipe.Create();
            using (streamA)
            using (streamB)
            {
                await streamA.WriteAsync(new byte[] { 65, 255, 66 }, 0, 3, CancellationToken.None);
                streamB.Available.Should().Be(3);
                streamB.ReadByte().Should().Be(65);
                streamB.ReadByte().Should().Be(255);
                streamB.ReadByte().Should().Be(66);

                await streamB.WriteByteAsync(42, CancellationToken.None);
                streamA.Available.Should().Be(1);
                streamA.ReadByte().Should().Be(42);
            }

            // Session level: a Latin-1 ÿ (byte 255) doubles on the wire and
            // undoubles on receipt, between two live sessions.
            var (writerSide, readerSide) = DuplexPipe.Create();
            using var writer = new ServerSession(writerSide, new TelnetServerOptions(), CancellationToken.None);
            using var reader = new ServerSession(readerSide, new TelnetServerOptions(), CancellationToken.None);
            await writer.WriteAsync("aÿb", CancellationToken.None);
            (await reader.ReadAsync(TimeSpan.FromSeconds(5))).Should().Be("aÿb");
        }

        [Fact]
        public async Task Duplex_ClientServer_NegotiateTtypeNaws()
        {
            // Real Client against a real ServerSession: the DO TTYPE opening
            // preset advances (DO NAWS follows), the TTYPE chain collects,
            // and the client's window size lands — with no loopback. The
            // client only answers while its read path is driven, so both
            // ends are pumped with bounded waits.
            using (GlobalStateGuard.SkipProactive(true))
            {
                var (clientStream, serverStream) = DuplexPipe.Create();
                using var session = new ServerSession(serverStream, new TelnetServerOptions(), CancellationToken.None);
                using var client = new Client(clientStream, CancellationToken.None);
                client.Settings.WindowWidth = 100;
                client.Settings.WindowHeight = 30;
                await session.SendOpeningPresetAsync(CancellationToken.None);

                var sw = Stopwatch.StartNew();
                while (!session.Negotiation.IsEnabledByPeer((int)Options.TerminalType) && sw.Elapsed < TimeSpan.FromSeconds(10))
                {
                    await client.ReadAsync(TimeSpan.FromMilliseconds(50));
                    await session.ReadAsync(TimeSpan.FromMilliseconds(50));
                }

                session.Negotiation.IsEnabledByPeer((int)Options.TerminalType).Should().BeTrue();

                var collectTask = session.RequestTerminalTypesAsync(TimeSpan.FromSeconds(5));
                sw.Restart();
                while (!collectTask.IsCompleted && sw.Elapsed < TimeSpan.FromSeconds(10))
                {
                    await client.ReadAsync(TimeSpan.FromMilliseconds(50));
                }

                (await collectTask).Should().NotBeEmpty();
                session.ClientTerminalTypes.Should().NotBeEmpty();

                sw.Restart();
                while (session.ClientWindowSize is null && sw.Elapsed < TimeSpan.FromSeconds(10))
                {
                    await client.ReadAsync(TimeSpan.FromMilliseconds(50));
                    await session.ReadAsync(TimeSpan.FromMilliseconds(50));
                }

                session.ClientWindowSize.HasValue.Should().BeTrue();
                session.ClientWindowSize.GetValueOrDefault().Should().Be(((ushort)100, (ushort)30));
            }
        }

        [Fact]
        public async Task Duplex_Close_PropagatesBothDirections()
        {
            var (streamA, streamB) = DuplexPipe.Create();
            using (streamA)
            using (streamB)
            {
                streamA.Connected.Should().BeTrue();
                streamB.Connected.Should().BeTrue();

                await streamA.WriteAsync(new byte[] { 1, 2, 3 }, 0, 3, CancellationToken.None);
                streamA.Close();
                streamA.Connected.Should().BeFalse();

                // In-flight bytes still drain before end-of-stream.
                streamB.Available.Should().Be(3);
                streamB.ReadByte().Should().Be(1);
                streamB.ReadByte().Should().Be(2);
                streamB.ReadByte().Should().Be(3);
                streamB.ReadByte().Should().Be(-1);

                streamB.Close();
                streamB.Connected.Should().BeFalse();
                streamA.ReadByte().Should().Be(-1);
            }
        }

        [Fact]
        public void Duplex_ReadByte_TimeoutExpires_ThrowsIOException()
        {
            // The IByteStream contract: timeout expirations surface as
            // IOException, not -1 (-1 is end-of-stream only).
            var (streamA, streamB) = DuplexPipe.Create();
            using (streamA)
            using (streamB)
            {
                streamB.ReceiveTimeout = 50;
                Action read = () => streamB.ReadByte();
                read.Should().Throw<IOException>();
            }
        }
    }
}
