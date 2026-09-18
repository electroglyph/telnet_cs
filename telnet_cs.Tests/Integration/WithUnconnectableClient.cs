namespace telnet_cs.Tests
{
    using System;
    using System.Diagnostics.CodeAnalysis;
    using System.Threading.Tasks;
    using FakeItEasy;
    using FluentAssertions;
    using Xunit;
    using telnet_cs.Client;
    using telnet_cs.Transport;

    [ExcludeFromCodeCoverage]
    public class WithUnconnectableClient
    {
        [Fact]
        public async Task CreateAsync_UnconnectedStream_ThrowsUnableToConnect()
        {
            var byteStream = A.Fake<IByteStream>();
            A.CallTo(() => byteStream.Connected).Returns(false);
            Func<Task> act = () => Client.CreateAsync(byteStream, new TimeSpan(0, 0, 0, 0, 1), default);

            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Unable to connect to the host.");
        }
    }
}
