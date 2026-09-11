namespace telnet_cs.Tests
{
    using FakeItEasy;
    using FluentAssertions;
    using Xunit;
    using telnet_cs.Transport;

    public class AuditStreamRegressionTests
    {
        [Fact]
        public void GetStreamReturnsCachedInstance()
        {
            using var server = new DummyTelnetServer();
            using var socket = new telnet_cs.Transport.TcpClient(server.IPAddress.ToString(), server.Port);
            socket.GetStream().Should().BeSameAs(socket.GetStream());
        }

        [Fact]
        public void SocketOptionsPassthrough()
        {
            var socket = A.Fake<ISocket>();
            A.CallTo(() => socket.GetStream()).Returns(A.Fake<INetworkStream>());
            using var sut = new TcpByteStream(socket);
            A.CallTo(() => socket.NoDelay).Returns(true);
            A.CallTo(() => socket.KeepAlive).Returns(true);
            A.CallTo(() => socket.SendTimeout).Returns(321);
            sut.NoDelay.Should().BeTrue();
            sut.KeepAlive.Should().BeTrue();
            sut.SendTimeout.Should().Be(321);
            sut.NoDelay = false;
            sut.KeepAlive = false;
            sut.SendTimeout = 654;
            A.CallToSet(() => socket.NoDelay).To(false).MustHaveHappened();
            A.CallToSet(() => socket.KeepAlive).To(false).MustHaveHappened();
            A.CallToSet(() => socket.SendTimeout).To(654).MustHaveHappened();
        }

        [Fact]
        public void ReadByteWhenDisconnectedReturnsMinusOne()
        {
            var socket = A.Fake<ISocket>();
            A.CallTo(() => socket.Connected).Returns(false);
            var stream = A.Fake<INetworkStream>();
            A.CallTo(() => socket.GetStream()).Returns(stream);
            using var sut = new TcpByteStream(socket);
            sut.ReadByte().Should().Be(-1);
            A.CallTo(() => stream.ReadByte()).MustNotHaveHappened();
        }
    }
}
