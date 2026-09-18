namespace telnet_cs.Tests
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using FakeItEasy;
    using FluentAssertions;
    using Xunit;
    using telnet_cs.Client;
    using telnet_cs.Protocol;
    using telnet_cs.Server;
    using telnet_cs.Transport;

    public class TelnetCommandsTests
    {
        [Theory]
        [InlineData(Commands.Break)]
        [InlineData(Commands.InterruptProcess)]
        [InlineData(Commands.AbortOutput)]
        [InlineData(Commands.AreYouThere)]
        [InlineData(Commands.EraseCharacter)]
        [InlineData(Commands.EraseLine)]
        [InlineData(Commands.GoAhead)]
        [InlineData(Commands.NoOperation)]
        [InlineData(Commands.EndOfFile)]
        [InlineData(Commands.Suspend)]
        [InlineData(Commands.Abort)]
        public void IsStandaloneControl_ControlCommand_ReturnsTrue(Commands command)
        {
            TelnetCommands.IsStandaloneControl(command).Should().BeTrue();
        }

        [Theory]
        [InlineData(Commands.Do)]
        [InlineData(Commands.Dont)]
        [InlineData(Commands.Will)]
        [InlineData(Commands.Wont)]
        [InlineData(Commands.Subnegotiation)]
        [InlineData(Commands.SubnegotiationEnd)]
        [InlineData(Commands.InterpretAsCommand)]
        [InlineData(Commands.EndOfRecord)]
        [InlineData(Commands.DataMark)]
        public void IsStandaloneControl_NegotiationVerb_ReturnsFalse(Commands command)
        {
            TelnetCommands.IsStandaloneControl(command).Should().BeFalse();
        }

        [Fact]
        public void FrameVerb_VerbAndBody_PrefixesVerbByte()
        {
            CharsetProtocol.FrameVerb(CharsetProtocol.Request, [32, 65]).Should().Equal([1, 32, 65]);
            CharsetProtocol.FrameVerb(CharsetProtocol.Accepted, []).Should().Equal([2]);
        }

        [Theory]
        [InlineData(Commands.Do)]
        [InlineData(Commands.Will)]
        [InlineData(Commands.Subnegotiation)]
        [InlineData(Commands.InterpretAsCommand)]
        public async Task SendCommand_NegotiationVerb_ThrowsAndSendsNothing(Commands verb)
        {
            var fake = A.Fake<IByteStream>();
            A.CallTo(() => fake.Connected).Returns(true);
            using var client = new Client(fake, TimeSpan.FromMilliseconds(10), default);
            Func<Task> act = () => client.SendCommand(verb);
            await act.Should().ThrowAsync<ArgumentOutOfRangeException>().WithParameterName("command");
            A.CallTo(() => fake.WriteAsync(A<byte[]>.Ignored, A<int>.Ignored, A<int>.Ignored, A<CancellationToken>.Ignored)).MustNotHaveHappened();
        }

        [Theory]
        [InlineData(Commands.Do)]
        [InlineData(Commands.Will)]
        [InlineData(Commands.Subnegotiation)]
        [InlineData(Commands.InterpretAsCommand)]
        public async Task SendCommand_ServerSideNegotiationVerb_ThrowsAndSendsNothing(Commands verb)
        {
            using var stream = new ScriptedStream();
            using var session = new ServerSession(stream, new TelnetServerOptions(), CancellationToken.None);
            Func<Task> act = () => session.SendCommand(verb);
            await act.Should().ThrowAsync<ArgumentOutOfRangeException>().WithParameterName("command");
            stream.ByteWrites.Should().BeEmpty();
        }
    }
}
