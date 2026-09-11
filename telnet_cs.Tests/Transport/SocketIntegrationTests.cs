// Real-socket integration tests. These run unconditionally
// against DummyTelnetServerBase, which now binds 127.0.0.1 on an OS-assigned
// (ephemeral) port, so they are hermetic: no fixed ports, no DNS, and every
// wait is bounded.
namespace telnet_cs.Tests
{
  using System;
  using System.Diagnostics;
  using System.IO;
  using System.Net;
  using System.Net.Sockets;
  using System.Text;
  using System.Threading;
  using System.Threading.Tasks;
  using FluentAssertions;
  using Xunit;
  using telnet_cs.Client;
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
      var login = Encoding.ASCII.GetBytes("username\n");
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
      // consumes "username\n" and replies with the (unread) Password prompt.
      await sut.WriteAsync("username\n", CancellationToken.None);
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
  }
}
