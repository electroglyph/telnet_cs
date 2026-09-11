namespace telnet_cs.Tests
{
  using System;
  using System.Collections.Generic;
  using System.Threading;
  using System.Threading.Tasks;
  using FakeItEasy;
  using FluentAssertions;
  using Xunit;
  using telnet_cs.IO;
  using telnet_cs.Transport;

  public class AuditCoverageTopUp3Tests
  {
    [Fact]
    public async Task NawsOutOfRangeDimensionsClampTo80x24()
    {
      // Width/height outside 1..65535 fall back to the 80x24 default.
      using var stream = new ScriptedStream(255, 253, 31);
      using var cts = new CancellationTokenSource();
      using var sut = new ByteStreamHandler(stream, cts, 1);
      sut.WindowWidth = 100000;
      sut.WindowHeight = -3;
      (await sut.ReadAsync(TimeSpan.FromMilliseconds(100))).Should().BeEmpty();
      stream.ByteWrites.Should().HaveCount(2);
      stream.ByteWrites[1].Should().Equal(new byte[]
      {
        255, 250, 31, 0, 0, 80, 0, 24, 255, 240,
      });
    }

    [Fact]
    public async Task InvalidOperationDuringSubnegotiationAbortsItSilently()
    {
      // TryReadByte maps InvalidOperationException to -1, like IOException.
      var fake = A.Fake<IByteStream>();
      A.CallTo(() => fake.Connected).Returns(true);
      var first = true;
      A.CallTo(() => fake.Available).ReturnsLazily(() =>
      {
        if (first)
        {
          first = false;
          return 1;
        }

        return 0;
      });
      var reads = new Queue<int>(new[] { 255, 250 });
      A.CallTo(() => fake.ReadByte()).ReturnsLazily(() =>
        reads.Count > 0 ? reads.Dequeue() : throw new InvalidOperationException("boom"));
      using var cts = new CancellationTokenSource();
      using var sut = new ByteStreamHandler(fake, cts, 1);
      (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().BeEmpty();
      A.CallTo(() => fake.WriteAsync(A<byte[]>.Ignored, A<int>.Ignored, A<int>.Ignored, A<CancellationToken>.Ignored)).MustNotHaveHappened();
    }
  }
}
