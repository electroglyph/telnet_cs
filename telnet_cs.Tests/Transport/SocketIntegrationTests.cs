// Real-socket integration tests. These run unconditionally
// against DummyTelnetServerBase, which now binds 127.0.0.1 on an OS-assigned
// (ephemeral) port, so they are hermetic: no fixed ports, no DNS, and every
// wait is bounded.
namespace telnet_cs.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Net.Sockets;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using FluentAssertions;
    using Xunit;
    using telnet_cs.Client;
    using telnet_cs.IO;
    using telnet_cs.Protocol;
    using telnet_cs.Transport;

    public class SocketIntegrationTests
    {
        [Fact]
        public void DummyServersGetDistinctLoopbackEphemeralPorts()
        {
            using var first = new DummyTelnetServer();
            using var second = new DummyTelnetServer();
            first.IPAddress.Should().Be(IPAddress.Loopback);
            second.IPAddress.Should().Be(IPAddress.Loopback);
            first.Port.Should().BeInRange(1, 65535);
            second.Port.Should().BeInRange(1, 65535);
            second.Port.Should().NotBe(first.Port);
        }

        [Fact]
        public void RealTcpClientExposesConnectionPropertiesAndPrompt()
        {
            using var server = new DummyTelnetServer();
            using var tcp = new telnet_cs.Transport.TcpClient(server.IPAddress.ToString(), server.Port);
            tcp.Connected.Should().BeTrue();
            tcp.ReceiveTimeout.Should().Be(0);
            tcp.Available.Should().BeGreaterOrEqualTo(0);
            using var stream = tcp.GetStream();
            stream.Should().NotBeNull();
            // The dummy server sends "Account:" immediately after Accept.
            stream.ReadByte().Should().Be((byte)'A');
            tcp.ReceiveTimeout = 250;
            tcp.ReceiveTimeout.Should().Be(250);
        }

        [Fact]
        public async Task RealNetworkStreamWriteElicitsPasswordPrompt()
        {
            using var server = new DummyTelnetServer();
            using var tcp = new telnet_cs.Transport.TcpClient(server.IPAddress.ToString(), server.Port);
            tcp.ReceiveTimeout = 2000;
            using var stream = tcp.GetStream();
            var login = Encoding.ASCII.GetBytes("username\r\n");
            await stream.WriteAsync(login, 0, login.Length, CancellationToken.None);
            var sb = new StringBuilder();
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < TimeSpan.FromSeconds(5))
            {
                int b;
                try
                {
                    b = stream.ReadByte();
                }
                catch (IOException)
                {
                    break; // receive timeout: server never answered
                }

                if (b == -1)
                {
                    break;
                }

                sb.Append((char)b);
                if (sb.ToString().Contains("Password:", StringComparison.Ordinal))
                {
                    break;
                }
            }

            sb.ToString().Should().Contain("Password:");
        }

        [Fact]
        public async Task RealTcpByteStreamReadsPromptWritesLoginAndSetsTimeout()
        {
            using var server = new DummyTelnetServer();
            using var sut = new TcpByteStream(server.IPAddress.ToString(), server.Port);
            sut.Connected.Should().BeTrue();
            sut.ReceiveTimeout = 500;
            sut.ReceiveTimeout.Should().Be(500);
            sut.ReadByte().Should().Be((byte)'A');
            // Covers the real TcpByteStream.WriteAsync(string) path; the server
            // consumes "username\r\n" and replies with the (unread) Password prompt.
            await sut.WriteAsync("username\r\n", CancellationToken.None);
            server.IsListening.Should().BeTrue();
        }

        [Fact]
        public async Task SendSynchAsync_DeliversDmOutOfBand()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var byteStream = new TcpByteStream("127.0.0.1", port);
            using Socket server = await listener.AcceptSocketAsync();
            using var sut = new Client(byteStream, TimeSpan.FromSeconds(5), CancellationToken.None);
            await sut.SendSynchAsync();
            server.ReceiveTimeout = 5000;
            var buf = new byte[16];
            var got = server.Receive(buf, SocketFlags.OutOfBand);
            got.Should().Be(1);
            buf[0].Should().Be(242); // DM, the Synch data-mark octet.
        }

        [Fact]
        public async Task SynchReceive_DiscardsUntilInBandDm()
        {
            // Live RFC 854 Synch: urgent DM triggers discard mode on the reader;
            // pre-DM in-band data is dropped, post-DM data surfaces. The urgent
            // byte leads the in-band payload by 300ms, so the trigger always wins
            // the race deterministically.
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var sender = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await sender.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port));
            using Socket accepted = await listener.AcceptSocketAsync();
            // The reader wraps the accepted end of the sender's connection (not a
            // second dial, which would sit unaccepted in the backlog).
            using var sysClient = new System.Net.Sockets.TcpClient { Client = accepted };
            using var tcp = new telnet_cs.Transport.TcpClient(sysClient);
            using var byteStream = new TcpByteStream(tcp);
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(byteStream, cts, 1);
            _ = sender.Send(new byte[] { 242 }, SocketFlags.OutOfBand);
            await Task.Delay(300);
            _ = sender.Send(Encoding.ASCII.GetBytes("JUNK"));
            _ = sender.Send(new byte[] { 255, 242 });
            _ = sender.Send(Encoding.ASCII.GetBytes("AFTER"));
            (await sut.ReadAsync(TimeSpan.FromSeconds(1))).Should().Be("AFTER");
            sut.InSynchDiscard.Should().BeFalse();
        }

        [Fact]
        public async Task SendCommand_FlushIn_SendsUrgentDmOverLoopback()
        {
            // Live RFC 1184 §5.8 FLUSHIN: a sent BRK whose SLC row carries
            // FLUSHIN emits the IAC BRK in-band plus an urgent DM out-of-band.
            using (GlobalStateGuard.SkipProactive(true))
            {
                using var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                var port = ((IPEndPoint)listener.LocalEndpoint).Port;
                using var byteStream = new TcpByteStream("127.0.0.1", port);
                using Socket server = await listener.AcceptSocketAsync();
                using var sut = new Client(byteStream, TimeSpan.FromSeconds(5), CancellationToken.None);
                _ = server.Send(new byte[] { 255, 253, 34 });
                (await sut.ReadAsync(TimeSpan.FromSeconds(1))).Should().BeEmpty();
                sut.Negotiation.IsEnabledByUs((int)Options.LineMode).Should().BeTrue();
                sut.SetLinemodeEntry(2, 2, 7, 64);
                await sut.SendCommand(Commands.Break);
                server.ReceiveTimeout = 5000;
                var urgent = new byte[16];
                server.Receive(urgent, SocketFlags.OutOfBand).Should().Be(1);
                urgent[0].Should().Be(242); // DM, the Synch data-mark octet.
                var inbound = new List<byte>();
                var chunk = new byte[16];
                while (inbound.Count < 5)
                {
                    int n = server.Receive(chunk);
                    if (n == 0)
                    {
                        break;
                    }

                    inbound.AddRange(chunk.Take(n));
                }

                // DO LINEMODE earns WILL plus the automatic SLC import; the
                // BRK itself follows in-band.
                inbound.Should().Equal(
                  255, 251, 34,
                  255, 250, 34, 3, 0, 3, 0, 255, 240,
                  255, 243);
            }
        }

        [Fact]
        public async Task TryConsumeUrgent_WithoutPendingByte_ReturnsNullFast()
        {
            // No OOB byte is ever sent: the poll gate says none pending and the
            // consume returns null without touching the (blocking) receive.
            // Bounded by WhenAny, not by trust: a regression to a blocking
            // receive would trip the 2s guard instead of hanging the suite.
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var sender = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await sender.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port));
            using Socket accepted = await listener.AcceptSocketAsync();
            using var sysClient = new System.Net.Sockets.TcpClient { Client = accepted };
            using var tcp = new telnet_cs.Transport.TcpClient(sysClient);

            var consumeTask = Task.Run(() => tcp.TryConsumeUrgent());
            var completed = await Task.WhenAny(
                consumeTask,
                Task.Delay(TimeSpan.FromSeconds(2)));
            completed.Should().Be(consumeTask);
            (await consumeTask).Should().BeNull();
        }

        [Fact]
        public async Task ReceiveUrgentAsync_AfterGracefulClose_ThrowsEndOfStream()
        {
            // Peer closes with no urgent byte in flight: the 0-byte receive
            // surfaces EndOfStreamException, never a phantom 0x00 Synch.
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var sender = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await sender.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port));
            using Socket accepted = await listener.AcceptSocketAsync();
            using var sysClient = new System.Net.Sockets.TcpClient { Client = accepted };
            using var tcp = new telnet_cs.Transport.TcpClient(sysClient);
            sender.Shutdown(SocketShutdown.Both);
            sender.Close();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            Func<Task> act = () => tcp.ReceiveUrgentAsync(cts.Token);
            await act.Should().ThrowAsync<EndOfStreamException>();
        }
    }
}
