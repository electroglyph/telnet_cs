namespace telnet_cs.Tests
{
    using System;
    using System.Diagnostics.CodeAnalysis;
    using FakeItEasy;
    using FluentAssertions;
    using Xunit;
    using telnet_cs.Client;
    using telnet_cs.Transport;

    [ExcludeFromCodeCoverage]
    public class WithUnconnectableClient
    {
        [Fact]
        public void Ctor_UnconnectedStream_ThrowsUnableToConnect()
        {
            var byteStream = A.Fake<IByteStream>();
            A.CallTo(() => byteStream.Connected).Returns(false);
            Client? sut = null;
            Action act = () => sut = new Client(byteStream, new TimeSpan(0, 0, 0, 0, 1), default);

            act.Should().Throw<InvalidOperationException>().WithMessage("Unable to connect to the host.");
            sut.Should().BeNull();
        }
    }
}
