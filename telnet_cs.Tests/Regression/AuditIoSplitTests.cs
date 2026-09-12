namespace telnet_cs.Tests
{
    using System;
    using System.Linq;
    using System.Net.Sockets;
    using System.Threading;
    using System.Threading.Tasks;
    using FluentAssertions;
    using Xunit;
    using telnet_cs.IO;
    using telnet_cs.Transport;

    public class AuditIoSplitTests
    {
        private const int Iac = 255;
        private const int Sb = 250;
        private const int Se = 240;
        private const int Will = 251;
        private const int Do = 253;
        private const int Wont = 252;

        private static readonly TimeSpan Slice = TimeSpan.FromMilliseconds(50);

        private static async Task<string> ReadOnceAsync(ByteStreamHandler sut)
        {
            return await sut.ReadAsync(Slice);
        }

        [Fact]
        public async Task SplitIacDo_SurfacesWillReplyAfterContinuation()
        {
            using var stream = new ScriptedStream(Iac);
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1);
            (await ReadOnceAsync(sut)).Should().BeEmpty();
            stream.Enqueue(Do, 3);
            (await ReadOnceAsync(sut)).Should().BeEmpty();
            stream.ByteWrites.SelectMany(w => w).ToArray().Should().Equal(Iac, Will, 3);
        }

        [Fact]
        public async Task SplitIacSb_SurfacesWontReplyAfterContinuation()
        {
            using var stream = new ScriptedStream(Iac, Sb);
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1);
            (await ReadOnceAsync(sut)).Should().BeEmpty();
            stream.Enqueue(31, 0, 0, 80, 0, 24, Iac, Se);
            (await ReadOnceAsync(sut)).Should().BeEmpty();
            stream.ByteWrites.Should().ContainSingle().Which.Should().Equal(new byte[] { Iac, Wont, 31 });
        }

        [Fact]
        public async Task SplitIacVerb_SurfacesWillReplyAfterContinuation()
        {
            using var stream = new ScriptedStream(Iac, Do);
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1);
            (await ReadOnceAsync(sut)).Should().BeEmpty();
            stream.Enqueue(3);
            (await ReadOnceAsync(sut)).Should().BeEmpty();
            stream.ByteWrites.SelectMany(w => w).ToArray().Should().Equal(Iac, Will, 3);
        }

        [Fact]
        public async Task SplitCrNul_AcrossReads_CollapsesToSingleCr()
        {
            using var stream = new ScriptedStream(65, 13);
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1);
            (await ReadOnceAsync(sut)).Should().Be("A\r");
            stream.Enqueue(0);
            (await ReadOnceAsync(sut)).Should().BeEmpty();
        }

        [Fact]
        public async Task SplitSbTerminator_ResumesFrameAndDeliversTrailingData()
        {
            using var stream = new ScriptedStream(Iac, Sb, 31, 1, Iac);
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1);
            (await ReadOnceAsync(sut)).Should().BeEmpty();
            stream.Enqueue(Se, 65);
            (await ReadOnceAsync(sut)).Should().Be("A");
        }

        [Fact]
        public async Task NestedSb_InnerFrameStillDispatched()
        {
            using var stream = new ScriptedStream(Iac, Sb, 24, 65, Iac, Sb, 31, 0, 0, 80, 0, 24, Iac, Se);
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1);
            (await ReadOnceAsync(sut)).Should().BeEmpty();
            stream.ByteWrites.Should().Contain(w => w.SequenceEqual(new byte[] { Iac, Wont, 31 }));
        }

        [Fact]
        public async Task EmptyMccpSb_WithoutAgreement_DoesNotSwallowData()
        {
            using var stream = new ScriptedStream(Iac, Sb, 86, Iac, Se, 72, 69, 76, 76, 79);
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1);
            (await ReadOnceAsync(sut)).Should().Be("HELLO");
        }

        [Fact]
        public async Task MisalignedSlc_DoesNotThrowOutOfRead()
        {
            using var stream = new ScriptedStream(Iac, Sb, 34, 3, 3, 2, 5, 9, Iac, Se);
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1);
            Func<Task<string>> read = () => ReadOnceAsync(sut);
            await read.Should().NotThrowAsync();
        }

        [Fact]
        public async Task SocketError_ReturnsPartialInsteadOfThrowing()
        {
            using var stream = new SocketFailingStream();
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1);
            Func<Task<string>> read = () => ReadOnceAsync(sut);
            await read.Should().NotThrowAsync();
            (await ReadOnceAsync(sut)).Should().BeEmpty();
        }

        private sealed class SocketFailingStream : IByteStream
        {
            public int Available => 1;

            public bool Connected => true;

            public int ReceiveTimeout { get; set; }

            public void Close()
            {
            }

            public void Dispose()
            {
            }

            public int ReadByte()
            {
                throw new SocketException((int)SocketError.ConnectionReset);
            }

            public Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                return Task.CompletedTask;
            }

            public Task WriteAsync(string value, CancellationToken cancellationToken)
            {
                return Task.CompletedTask;
            }

            public Task WriteByteAsync(byte value, CancellationToken cancellationToken)
            {
                return Task.CompletedTask;
            }
        }
    }
}
