namespace telnet_cs.Tests
{
  using System;
  using System.Diagnostics;
  using System.Diagnostics.CodeAnalysis;
  using System.Threading;
  using System.Threading.Tasks;
  using FakeItEasy;
  using FluentAssertions;
  using Xunit;
  using telnet_cs.IO;
  using telnet_cs.Protocol;
  using telnet_cs.Transport;

  [ExcludeFromCodeCoverage]
  public class ByteStreamHandlerTests
  {
    [Fact]
    public async Task UnconnectedByteStreamShouldReturnEmptyResponse()
    {
      using var sut = new ByteStreamHandler(A.Fake<IByteStream>());
      (await sut.ReadAsync(new TimeSpan())).Should().Be(string.Empty);
    }

    [Fact]
    public async Task ByteStreamShouldReturnEmptyResponse()
    {
      var socket = A.Fake<ISocket>();
      using (var networkStream = A.Fake<INetworkStream>())
      {
        A.CallTo(() => socket.GetStream()).Returns(networkStream);
        A.CallTo(() => socket.Connected).Returns(true);
        var isFirst = true;
        A.CallTo(() => socket.Available).ReturnsLazily(() =>
        {
          if (isFirst)
          {
            isFirst = false;
            return 1;
          }
          return 0;
        });
        using var tcpByteStream = new TcpByteStream(socket);
        A.CallTo(() => networkStream.ReadByte()).ReturnsNextFromSequence(-1);
        tcpByteStream.Connected.Should().BeTrue();
        using var sut = new ByteStreamHandler(tcpByteStream);

        var response = await sut.ReadAsync(TimeSpan.FromMilliseconds(10));

        response.Should().BeEmpty();
      }
    }

    [Fact]
    public async Task ByteStreamShouldReturnCharA()
    {
      var socket = A.Fake<ISocket>();
      using (var networkStream = A.Fake<INetworkStream>())
      {
        A.CallTo(() => socket.GetStream()).Returns(networkStream);
        A.CallTo(() => socket.Connected).Returns(true);
        var isFirst = true;
        A.CallTo(() => socket.Available).ReturnsLazily(() =>
        {
          if (isFirst)
          {
            isFirst = false;
            return 1;
          }
          return 0;
        });
        using var tcpByteStream = new TcpByteStream(socket);
        A.CallTo(() => networkStream.ReadByte()).ReturnsNextFromSequence(65);
        tcpByteStream.Connected.Should().BeTrue();
        using var sut = new ByteStreamHandler(tcpByteStream);

        var response = await sut.ReadAsync(TimeSpan.FromMilliseconds(10));

        response.Should().Be("A");
      }
    }

    [Fact]
    public async Task ByteStreamShouldReturnEscapedIac()
    {
      var socket = A.Fake<ISocket>();
      using (var networkStream = A.Fake<INetworkStream>())
      {
        A.CallTo(() => socket.GetStream()).Returns(networkStream);
        A.CallTo(() => socket.Connected).Returns(true);
        var isFirst = true;
        A.CallTo(() => socket.Available).ReturnsLazily(() =>
        {
          if (isFirst)
          {
            isFirst = false;
            return 1;
          }
          return 0;
        });
        using var tcpByteStream = new TcpByteStream(socket);
        A.CallTo(() => networkStream.ReadByte()).ReturnsNextFromSequence(new int[] { (int)Commands.InterpretAsCommand, (int)Commands.InterpretAsCommand });
        tcpByteStream.Connected.Should().BeTrue();
        using var sut = new ByteStreamHandler(tcpByteStream);

        var response = await sut.ReadAsync(TimeSpan.FromMilliseconds(10));
        response.Should().Be("\u00ff");
      }
    }

    [Fact]
    public async Task ByteStreamShouldReturnEmpty()
    {
      var socket = A.Fake<ISocket>();
      using (var networkStream = A.Fake<INetworkStream>())
      {
        A.CallTo(() => socket.GetStream()).Returns(networkStream);
        A.CallTo(() => socket.Connected).Returns(true);
        var isFirst = true;
        A.CallTo(() => socket.Available).ReturnsLazily(() =>
        {
          if (isFirst)
          {
            isFirst = false;
            return 1;
          }
          return 0;
        });
        using var tcpByteStream = new TcpByteStream(socket);
        A.CallTo(() => networkStream.ReadByte()).ReturnsNextFromSequence(new int[] { (int)Commands.InterpretAsCommand, -1 });
        tcpByteStream.Connected.Should().BeTrue();
        using var sut = new ByteStreamHandler(tcpByteStream);

        var response = await sut.ReadAsync(TimeSpan.FromMilliseconds(10));

        response.Should().BeEmpty();
      }
    }

    [Fact]
    public async Task WhenIacDoSgaByteStreamShouldReturnEmptyAndReplyIacWill()
    {
      var socket = A.Fake<ISocket>();
      using (var networkStream = A.Fake<INetworkStream>())
      {
        A.CallTo(() => socket.GetStream()).Returns(networkStream);
        A.CallTo(() => socket.Connected).Returns(true);
        var isFirst = true;
        A.CallTo(() => socket.Available).ReturnsLazily(() =>
        {
          if (isFirst)
          {
            isFirst = false;
            return 1;
          }
          return 0;
        });
        using var tcpByteStream = new TcpByteStream(socket);
        A.CallTo(() => networkStream.ReadByte()).ReturnsNextFromSequence(new int[] { (int)Commands.InterpretAsCommand, (int)Commands.Do, (int)Options.SuppressGoAhead });
        tcpByteStream.Connected.Should().BeTrue();
        using var sut = new ByteStreamHandler(tcpByteStream);

        var response = await sut.ReadAsync(TimeSpan.FromMilliseconds(10));
        A.CallTo(() => networkStream.WriteAsync(A<byte[]>.Ignored, 0, 3, A<CancellationToken>.Ignored))
                            .WhenArgumentsMatch(o => o[0] is byte[] param && param[0] == (byte)Commands.InterpretAsCommand && param[1] == (byte)Commands.Will && param[2] == 3)
                            .MustHaveHappened();

        response.Should().BeEmpty();
      }
    }

    [Fact]
    public async Task WhenIacDo1ByteStreamShouldReturnEmptyAndReplyIacWont()
    {
      var socket = A.Fake<ISocket>();
      using (var networkStream = A.Fake<INetworkStream>())
      {
        A.CallTo(() => socket.GetStream()).Returns(networkStream);
        A.CallTo(() => socket.Connected).Returns(true);
        var isFirst = true;
        A.CallTo(() => socket.Available).ReturnsLazily(() =>
        {
          if (isFirst)
          {
            isFirst = false;
            return 1;
          }
          return 0;
        });
        using (var tcpByteStream = new TcpByteStream(socket))
        {
          A.CallTo(() => networkStream.ReadByte()).ReturnsNextFromSequence(new int[] { (int)Commands.InterpretAsCommand, (int)Commands.Do, 1 });
          tcpByteStream.Connected.Should().BeTrue();
          using (var sut = new ByteStreamHandler(tcpByteStream))
          {
            var response = await sut.ReadAsync(TimeSpan.FromMilliseconds(10));
            A.CallTo(() => networkStream.WriteAsync(A<byte[]>.Ignored, 0, 3, A<CancellationToken>.Ignored))
                .WhenArgumentsMatch(o => o[0] is byte[] param && param[0] == (byte)Commands.InterpretAsCommand && param[1] == (byte)Commands.Wont && param[2] == 1)
                .MustHaveHappened();

            response.Should().BeEmpty();
          }
        }
      }
    }

    [Fact]
    public async Task WhenIac2ByteStreamShouldReturnEmptyAndNotReply()
    {
      var socket = A.Fake<ISocket>();
      using (var networkStream = A.Fake<INetworkStream>())
      {
        A.CallTo(() => socket.GetStream()).Returns(networkStream);
        A.CallTo(() => socket.Connected).Returns(true);
        var isFirst = true;
        A.CallTo(() => socket.Available).ReturnsLazily(() =>
        {
          if (isFirst)
          {
            isFirst = false;
            return 1;
          }
          return 0;
        });
        using (var tcpByteStream = new TcpByteStream(socket))
        {
          A.CallTo(() => networkStream.ReadByte()).ReturnsNextFromSequence(new int[] { (int)Commands.InterpretAsCommand, 2 });
          tcpByteStream.Connected.Should().BeTrue();
          using (var sut = new ByteStreamHandler(tcpByteStream))
          {
            var response = await sut.ReadAsync(TimeSpan.FromMilliseconds(10));

            response.Should().BeEmpty();
            A.CallTo(() => networkStream
                .WriteByteAsync(A<byte>._, A<CancellationToken>._)
            ).MustNotHaveHappened();
          }
        }
      }
    }

    [Fact]
    public async Task WhenIacDont1ByteStreamShouldReturnEmptyAndNotReply()
    {
      var socket = A.Fake<ISocket>();
      using (var networkStream = A.Fake<INetworkStream>())
      {
        A.CallTo(() => socket.GetStream()).Returns(networkStream);
        A.CallTo(() => socket.Connected).Returns(true);
        var isFirst = true;
        A.CallTo(() => socket.Available).ReturnsLazily(() =>
        {
          if (isFirst)
          {
            isFirst = false;
            return 1;
          }
          return 0;
        });
        using (var tcpByteStream = new TcpByteStream(socket))
        {
          A.CallTo(() => networkStream.ReadByte()).ReturnsNextFromSequence(new int[] { (int)Commands.InterpretAsCommand, (int)Commands.Dont, 1 });
          tcpByteStream.Connected.Should().BeTrue();
          using (var sut = new ByteStreamHandler(tcpByteStream))
          {
            var response = await sut.ReadAsync(TimeSpan.FromMilliseconds(10));

            response.Should().BeEmpty();
            A.CallTo(() => networkStream
                .WriteByteAsync(A<byte>._, A<CancellationToken>._)
                ).MustNotHaveHappened();
          }
        }
      }
    }

    [Fact]
    public async Task WhenIacDontSgaByteStreamShouldReturnEmptyAndNotReply()
    {
      var socket = A.Fake<ISocket>();
      using (var networkStream = A.Fake<INetworkStream>())
      {
        A.CallTo(() => socket.GetStream()).Returns(networkStream);
        A.CallTo(() => socket.Connected).Returns(true);
        var isFirst = true;
        A.CallTo(() => socket.Available).ReturnsLazily(() =>
        {
          if (isFirst)
          {
            isFirst = false;
            return 1;
          }
          return 0;
        });
        using (var tcpByteStream = new TcpByteStream(socket))
        {
          A.CallTo(() => networkStream.ReadByte()).ReturnsNextFromSequence(new int[] { (int)Commands.InterpretAsCommand, (int)Commands.Dont, (int)Options.SuppressGoAhead });
          tcpByteStream.Connected.Should().BeTrue();
          using (var sut = new ByteStreamHandler(tcpByteStream))
          {
            var response = await sut.ReadAsync(TimeSpan.FromMilliseconds(10));
            response.Should().BeEmpty();
            A.CallTo(() => networkStream
                .WriteByteAsync(A<byte>._, A<CancellationToken>._)
                ).MustNotHaveHappened();
          }
        }
      }
    }

    [Fact]
    public async Task ByteStreamShouldReturnUponCancellation()
    {
      var socket = A.Fake<ISocket>();
      using (var networkStream = A.Fake<INetworkStream>())
      {
        A.CallTo(() => socket.GetStream()).Returns(networkStream);
        A.CallTo(() => socket.Connected).Returns(true);
        A.CallTo(() => socket.Available).Returns(1);
        using (var tcpByteStream = new TcpByteStream(socket))
        {
          A.CallTo(() => networkStream.ReadByte()).Returns(142);
          tcpByteStream.Connected.Should().BeTrue();
          using (var cancellationToken = new CancellationTokenSource())
          {
            var stopwatch = new Stopwatch();
            using (var sut = new ByteStreamHandler(tcpByteStream, cancellationToken))
            {

              cancellationToken.CancelAfter(100);
              await sut.ReadAsync(TimeSpan.FromMilliseconds(1000));

              stopwatch.ElapsedMilliseconds.Should().BeLessThan(500);
            }
          }
        }
      }
    }
  }
}
