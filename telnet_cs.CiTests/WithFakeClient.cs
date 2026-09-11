namespace telnet_cs.CiTests
{
  using System;
  using System.Diagnostics;
  using System.Diagnostics.CodeAnalysis;
  using System.Threading;
  using System.Threading.Tasks;
  using FakeItEasy;
  using FluentAssertions;
  using Xunit;

  [ExcludeFromCodeCoverage]
  public class WithFakeClient
  {
    [Fact]
    public async Task GivenByteStreamWillNeverRespondWhenTerminatedReadShouldWaitRoughlyOneTimeout()
    {
      var timeout = new TimeSpan(0, 0, 6);
      var millisecondTolerance = 750;
      var byteStream = A.Fake<IByteStream>();
      A.CallTo(() => byteStream.Connected).Returns(true);

      using (var sut = new Client(byteStream, new TimeSpan(0, 0, 0, 0, 1), default))
      {
        var start = DateTime.Now;
        sut.MillisecondReadDelay = 1;
        await sut.TerminatedReadAsync(".", timeout, 1);
        DateTime.Now.Subtract(start).Should().BeCloseTo(timeout.Add(TimeSpan.FromMilliseconds(millisecondTolerance)), TimeSpan.FromMilliseconds(millisecondTolerance));
      }
    }

    [Fact]
    public async Task ShouldWaitRoughlyOneMillisecondSpin()
    {
      var millisecondsSpin = 5000;
      var millisecondTolerance = 750;
      var hasResponded = false;
      var stopwatch = new Stopwatch();
      var byteStream = ArrangeByteStreamToRespondWithTerminationOnceAfterMillisecondSpin();

      using (var sut = new Client(byteStream, new TimeSpan(0, 0, 0, 0, 1), default))
      {
        stopwatch.Start();
        await sut.TerminatedReadAsync(".", new TimeSpan(0, 0, 0, 3), millisecondsSpin);

        stopwatch.Elapsed.Should().BeCloseTo(TimeSpan.FromMilliseconds(millisecondsSpin + millisecondTolerance), TimeSpan.FromMilliseconds(millisecondTolerance));
      }

      IByteStream ArrangeByteStreamToRespondWithTerminationOnceAfterMillisecondSpin()
      {
        byteStream = A.Fake<IByteStream>();
        A.CallTo(() => byteStream.Connected).Returns(true);
        A.CallTo(() => byteStream.Available).ReturnsLazily(() =>
        {
          if (stopwatch.ElapsedMilliseconds >= millisecondsSpin && !hasResponded)
          {
            return 1;
          }

          return 0;
        });
        A.CallTo(() => byteStream.ReadByte()).ReturnsLazily(() =>
        {
          if (stopwatch.ElapsedMilliseconds >= millisecondsSpin && !hasResponded)
          {
            hasResponded = true;
          }

          return '.';
        });
        return byteStream;
      }
    }

    [Fact]
    public async Task ClientShouldReturnUponCancellation()
    {
      var byteStream = A.Fake<IByteStream>();
      A.CallTo(() => byteStream.Connected).Returns(true);
      using (var cancellationToken = new CancellationTokenSource())
      {
        var stopwatch = new Stopwatch();
        using (var sut = new Client(byteStream, new TimeSpan(0, 0, 0, 0, 1), default))
        {
          cancellationToken.CancelAfter(100);
          await sut.ReadAsync(TimeSpan.FromMilliseconds(1000));

          stopwatch.ElapsedMilliseconds.Should().BeLessThan(500);
        }
      }

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
