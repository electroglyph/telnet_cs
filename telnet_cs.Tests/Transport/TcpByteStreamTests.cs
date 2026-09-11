namespace telnet_cs.Tests
{
    using System;
    using Xunit;
    using FluentAssertions;
    using FakeItEasy;
    using System.Threading.Tasks;
    using System.Threading;
    using telnet_cs.Transport;

    public class TcpByteStreamTests
    {
        [Fact]
        public void ShouldConstructWithAFakedSocket()
        {
            var socket = A.Fake<ISocket>();
#pragma warning disable CS8600 // Converting null literal or possible null value to non-nullable type.
            TcpByteStream sut = null;
#pragma warning restore CS8600 // Converting null literal or possible null value to non-nullable type.
            Action act = () => sut = new TcpByteStream(socket);
            act.Should().NotThrow();
            sut.Should().NotBeNull();
            sut?.Dispose();
        }

        [Fact]
        public void GivenAFakedSocketACallToReadByteShouldBeRelayed()
        {
            var socket = A.Fake<ISocket>();
            var stream = A.Fake<INetworkStream>();
            A.CallTo(() => socket.GetStream()).Returns(stream);
            A.CallTo(() => socket.Connected).Returns(true);
            using var sut = new TcpByteStream(socket);

            sut.ReadByte();

            A.CallTo(() => stream.ReadByte()).MustHaveHappened();
        }

        [Fact]
        public void TcpByteStreamShouldTerminateAndReleaseDebuggingContext()
        {
            using (var server = new DummyTelnetServer())
            {
                using (var sut = new TcpByteStream(server.IPAddress.ToString(), server.Port))
                {
                    sut.Should().NotBeNull();
                }
            }
        }

        [Fact]
        public void TcpByteStreamShouldConstruct()
        {
            using (var server = new DummyTelnetServer())
            {
                Action act = () =>
                {
                    using var sut = new TcpByteStream(server.IPAddress.ToString(), server.Port);
                };

                act.Should().NotThrow();
            }
        }

        [Fact]
        public void ReceiveTimeoutShouldBeDefaultTo0()
        {
            using (var server = new DummyTelnetServer())
            {
                using (var sut = new TcpByteStream(server.IPAddress.ToString(), server.Port))
                {
                    sut.ReceiveTimeout.Should().Be(0);
                }
            }
        }

        [Fact]
        public async Task GivenAFakedSocketACallToWriteShouldBeRelayed()
        {
            var writtenString = Guid.NewGuid().ToString();
            var socket = A.Fake<ISocket>();
            var stream = A.Fake<INetworkStream>();
            A.CallTo(() => socket.GetStream()).Returns(stream);
            using var sut = new TcpByteStream(socket);

            var cancellationToken = A.Dummy<CancellationToken>();
            await sut.WriteAsync(writtenString, cancellationToken);

            A.CallTo(() => stream.WriteAsync(A<byte[]>.Ignored, 0, writtenString.Length, cancellationToken)).MustHaveHappened();
        }

        [Fact]
        public async Task GivenAFakedSocketACallToWriteByteShouldBeRelayed()
        {
            var writtenByte = new byte();
            var socket = A.Fake<ISocket>();
            var stream = A.Fake<INetworkStream>();
            A.CallTo(() => socket.GetStream()).Returns(stream);
            using var sut = new TcpByteStream(socket);

            var cancellationToken = A.Dummy<CancellationToken>();
            await sut.WriteByteAsync(writtenByte, cancellationToken);

            A.CallTo(() => stream.WriteByteAsync(writtenByte, cancellationToken)).MustHaveHappened();
        }
    }
}
