namespace telnet_cs.Tests
{
    using System;
    using System.Linq;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using FluentAssertions;
    using Xunit;
    using telnet_cs.Client;
    using telnet_cs.IO;
    using telnet_cs.Protocol;

    public class ControlSignalTests
    {
        private static Client MakeClient(ScriptedStream stream)
        {
            return new Client(stream, new CancellationToken());
        }

        private static async Task<(string Output, ScriptedStream Stream)> ReadWithStreamAsync(params int[] reads)
        {
            var stream = new ScriptedStream(reads);
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1);
            var output = await sut.ReadAsync(TimeSpan.FromMilliseconds(50));
            return (output, stream);
        }

        public static TheoryData<Commands, byte> SendableCommands => new()
    {
      { Commands.NoOperation, 241 },
      { Commands.Break, 243 },
      { Commands.InterruptProcess, 244 },
      { Commands.AbortOutput, 245 },
      { Commands.AreYouThere, 246 },
      { Commands.EraseCharacter, 247 },
      { Commands.EraseLine, 248 },
      { Commands.GoAhead, 249 },
    };

        [Theory]
        [MemberData(nameof(SendableCommands))]
        public async Task SendCommand_WritesIacFramedPair(Commands command, byte code)
        {
            using var stream = new ScriptedStream();
            using var sut = MakeClient(stream);
            await sut.SendCommand(command);
            stream.ByteWrites.Should().ContainSingle(w => w.SequenceEqual(new byte[] { 255, code }));
        }

        public static TheoryData<Commands> RejectedCommands => new()
    {
      Commands.Do,
      Commands.Dont,
      Commands.Will,
      Commands.Wont,
      Commands.Subnegotiation,
      Commands.SubnegotiationEnd,
      Commands.InterpretAsCommand,
      Commands.DataMark, // DM travels out-of-band via SendSynchAsync, never in-band.
      (Commands)99,
    };

        [Theory]
        [MemberData(nameof(RejectedCommands))]
        public async Task SendCommand_RejectsNegotiationVerbs(Commands command)
        {
            using var stream = new ScriptedStream();
            using var sut = MakeClient(stream);
            var before = stream.ByteWrites.Count;
            Func<Task> act = () => sut.SendCommand(command);
            await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
            stream.ByteWrites.Should().HaveCount(before);
        }

        [Fact]
        public async Task Ayt_GetsProofAliveReply()
        {
            var (output, stream) = await ReadWithStreamAsync(255, 246);
            output.Should().BeEmpty();
            stream.ByteWrites.Should().ContainSingle().Which.Should().Equal(Encoding.ASCII.GetBytes("[AYT received]\r\n"));
        }

        [Fact]
        public async Task Ao_IsConsumedSilently()
        {
            var (output, stream) = await ReadWithStreamAsync(255, 245);
            output.Should().BeEmpty();
            stream.ByteWrites.Should().BeEmpty();
        }

        [Fact]
        public async Task Ec_ErasesLastChar()
        {
            var (output, _) = await ReadWithStreamAsync(65, 66, 255, 247);
            output.Should().Be("A");
        }

        [Fact]
        public async Task Ec_OnEmptyBuffer_IsNoop()
        {
            var (output, _) = await ReadWithStreamAsync(255, 247);
            output.Should().BeEmpty();
        }

        [Fact]
        public async Task El_ErasesToLastNewline()
        {
            var (output, _) = await ReadWithStreamAsync(65, 66, 13, 10, 67, 68, 255, 248);
            output.Should().Be("AB\r\n");
        }

        [Fact]
        public async Task El_WithNoNewline_ClearsAll()
        {
            var (output, _) = await ReadWithStreamAsync(65, 66, 255, 248);
            output.Should().BeEmpty();
        }

        [Fact]
        public async Task El_OnEmptyBuffer_IsNoop()
        {
            var (output, _) = await ReadWithStreamAsync(255, 248);
            output.Should().BeEmpty();
        }

        private static async Task<string> ReadWithEncodingAsync(Encoding encoding, params int[] reads)
        {
            using var stream = new ScriptedStream(reads);
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1) { TextEncoding = encoding };
            return await sut.ReadAsync(TimeSpan.FromMilliseconds(50));
        }

        [Fact]
        public async Task Ec_KeepsRawBytesInSync()
        {
            // Decoded from rawBytes (not sb): stale bytes would surface here.
            (await ReadWithEncodingAsync(Encoding.Latin1, 65, 66, 67, 255, 247)).Should().Be("AB");
        }

        [Fact]
        public async Task El_KeepsRawBytesInSync()
        {
            (await ReadWithEncodingAsync(Encoding.Latin1, 65, 66, 13, 10, 67, 255, 248)).Should().Be("AB\r\n");
        }

        [Fact]
        public async Task Brk_SurfacesMarker()
        {
            var (output, _) = await ReadWithStreamAsync(255, 243);
            output.Should().Be("[BRK]");
        }

        [Theory]
        [InlineData(241)] // NOP
        [InlineData(242)] // DM in normal mode stays a NOP
        public async Task SwallowedCommands_StaySilent(int verb)
        {
            var (output, stream) = await ReadWithStreamAsync(255, verb);
            output.Should().BeEmpty();
            stream.ByteWrites.Should().BeEmpty();
        }

        [Fact]
        public async Task StraySe_IsDeliveredAsDataWithoutReply()
        {
            // Decided (conflict 16): a bare IAC SE with no open SB block
            // delivers 0xF0 as data (telnetlib3 parity), never a reply.
            var (output, stream) = await ReadWithStreamAsync(255, 240);
            output.Should().Be("ð");
            stream.ByteWrites.Should().BeEmpty();
        }

        [Fact]
        public async Task GaUnsuppressed_RaisesNotification()
        {
            // RFC 858 §5: with SGA never negotiated, GA is the NVT turn-taking
            // signal — surfaced through the hook, never as data or wire replies.
            using var stream = new ScriptedStream(255, 249);
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1);
            var fired = 0;
            sut.GoAheadReceived = () => fired++;
            var output = await sut.ReadAsync(TimeSpan.FromMilliseconds(50));
            output.Should().BeEmpty();
            fired.Should().Be(1);
            stream.ByteWrites.Should().BeEmpty();
        }

        [Fact]
        public async Task GaUnsuppressed_SurfacesThroughClient()
        {
            using (GlobalStateGuard.SkipProactive(true))
            {
                using var stream = new ScriptedStream(255, 249);
                using var sut = MakeClient(stream);
                var fired = 0;
                sut.GoAheadReceived += (_, _) => fired++;
                (await sut.ReadAsync(TimeSpan.FromMilliseconds(100))).Should().BeEmpty();
                fired.Should().Be(1);
            }
        }

        [Fact]
        public async Task GaSuppressedWhenSgaAgreed_Silent()
        {
            // Peer WILL SGA (agreed): inbound GA is a NOP — silent, no event.
            using var stream = new ScriptedStream(255, 251, 3, 255, 249);
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1);
            var fired = 0;
            sut.GoAheadReceived = () => fired++;
            var output = await sut.ReadAsync(TimeSpan.FromMilliseconds(50));
            output.Should().BeEmpty();
            fired.Should().Be(0);
            stream.ByteWrites.Should().ContainSingle().Which.Should().Equal(new byte[] { 255, 253, 3 });
        }

        [Fact]
        public async Task SendSynch_RequiresTcpStream()
        {
            using var stream = new ScriptedStream();
            using var sut = MakeClient(stream);
            Func<Task> act = () => sut.SendSynchAsync();
            await act.Should().ThrowAsync<NotSupportedException>();
        }
    }
}
