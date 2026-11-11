namespace telnet_cs.Tests
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using FakeItEasy;
    using FluentAssertions;
    using Xunit;
    using telnet_cs.Transport;

    public class TcpByteStreamPassthroughTests
    {
        private static (TcpByteStream sut, ISocket socket, INetworkStream stream) Make()
        {
            var socket = A.Fake<ISocket>();
            var stream = A.Fake<INetworkStream>();
            A.CallTo(() => socket.GetStream()).Returns(stream);
            A.CallTo(() => socket.Connected).Returns(true);
            return (new TcpByteStream(socket), socket, stream);
        }

        [Fact]
        public void AvailableAndConnectedPassthrough()
        {
            var (sut, socket, _) = Make();
            using (sut)
            {
                A.CallTo(() => socket.Available).Returns(7);
                A.CallTo(() => socket.Connected).Returns(true);
                sut.Available.Should().Be(7);
                sut.Connected.Should().BeTrue();
            }
        }

        [Fact]
        public void ReceiveTimeoutGetSetPassthrough()
        {
            var (sut, socket, _) = Make();
            using (sut)
            {
                A.CallTo(() => socket.ReceiveTimeout).Returns(123);
                sut.ReceiveTimeout.Should().Be(123);
                sut.ReceiveTimeout = 456;
                A.CallToSet(() => socket.ReceiveTimeout).To(456).MustHaveHappened();
            }
        }

        [Fact]
        public void ReadByteRelaysToStream()
        {
            var (sut, _, stream) = Make();
            using (sut)
            {
                A.CallTo(() => stream.ReadByte()).Returns(65);
                sut.ReadByte().Should().Be(65);
            }
        }

        [Fact]
        public async Task WriteBufferRelaysToStream()
        {
            var (sut, _, stream) = Make();
            using (sut)
            {
                var buf = new byte[] { 1, 2, 3 };
                var ct = new CancellationToken();
                await sut.WriteAsync(buf, 0, 3, ct);
                A.CallTo(() => stream.WriteAsync(buf, 0, 3, ct)).MustHaveHappened();
            }
        }

        [Fact]
        public void WriteBuffer_NullBuffer_ThrowsArgumentNullException()
        {
            var (sut, _, _) = Make();
            using (sut)
            {
                Action act = () => sut.WriteAsync(null!, 0, 0, CancellationToken.None);
                act.Should().Throw<ArgumentNullException>().WithParameterName("buffer");
            }
        }

        [Theory]
        [InlineData(-1, 1)]
        [InlineData(0, -1)]
        [InlineData(2, 2)]
        public void WriteBuffer_OutOfRangeSlice_ThrowsArgumentException(int offset, int count)
        {
            var (sut, _, _) = Make();
            using (sut)
            {
                var buf = new byte[] { 1, 2, 3 };
                Action act = () => sut.WriteAsync(buf, offset, count, CancellationToken.None);
                act.Should().Throw<ArgumentException>();
            }
        }

        [Fact]
        public async Task WriteByteRelaysToStream()
        {
            var (sut, _, stream) = Make();
            using (sut)
            {
                var ct = new CancellationToken();
                await sut.WriteByteAsync(9, ct);
                A.CallTo(() => stream.WriteByteAsync(9, ct)).MustHaveHappened();
            }
        }

        [Fact]
        public void CloseRelaysAndDisposeDoesNotDisposeUnownedSocket()
        {
            var (sut, socket, _) = Make();
            sut.Close();
            A.CallTo(() => socket.Close()).MustHaveHappenedOnceExactly();
            sut.Dispose();
            A.CallTo(() => socket.Close()).MustHaveHappenedOnceExactly();
            A.CallTo(() => socket.Dispose()).MustNotHaveHappened();
        }

        [Fact]
        public void DisposeOwned_DisposesSocket()
        {
            var (_, socket, _) = Make();
            using TcpByteStream sut = new(socket, takeOwnership: true);
            sut.Dispose();
            A.CallTo(() => socket.Close()).MustHaveHappened();
            A.CallTo(() => socket.Dispose()).MustHaveHappened();
        }

        [Fact]
        public void Construct_WithFakedSocket_DoesNotThrow()
        {
            var socket = A.Fake<ISocket>();
            TcpByteStream? sut = null;
            Action act = () => sut = new TcpByteStream(socket);
            act.Should().NotThrow();
            sut.Should().NotBeNull();
            sut?.Dispose();
        }

        [Fact]
        public void Construct_AgainstLiveServer_DoesNotThrowAndDisposes()
        {
            using DummyTelnetServer server = new();
            TcpByteStream? sut = null;
            Action act = () => sut = new TcpByteStream(server.IPAddress.ToString(), server.Port);
            act.Should().NotThrow();
            sut.Should().NotBeNull();
            sut?.Dispose();
        }

        [Fact]
        public void ReceiveTimeout_DefaultsToZero()
        {
            using DummyTelnetServer server = new();
            using TcpByteStream sut = new(server.IPAddress.ToString(), server.Port);
            sut.ReceiveTimeout.Should().Be(0);
        }
    }
}
