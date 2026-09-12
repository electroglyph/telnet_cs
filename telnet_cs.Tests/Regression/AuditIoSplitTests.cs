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
            // RFC 854 (command structure): DO <opt> is one 3-byte IAC DO opt
            // unit on a byte stream, so framing cannot depend on segment
            // boundaries; RFC 1143 section 7 answers a received DO with WILL
            // when the option is agreeable (3 = SGA). telnetlib3 keeps the
            // equivalent partial-command state across feed_byte calls.
            using var stream = new ScriptedStream(Iac);
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1);
            (await ReadOnceAsync(sut)).Should().BeEmpty();
            stream.Enqueue(Do, 3);
            (await ReadOnceAsync(sut)).Should().BeEmpty();
            stream.ByteWrites.SelectMany(w => w).ToArray().Should().Equal(Iac, Will, 3);
        }

        [Fact]
        public async Task SplitIacSb_ResumesFrameAndAnswersUnhandledOption()
        {
            // RFC 854: a subnegotiation is IAC SB ... IAC SE, one framing
            // unit across segments like any other command. The option 99
            // payload [65] is not SEND (1), so the generic fallback answers
            // WONT 99; option 99 is used because NAWS (31) carries a binary
            // width/height body per RFC 1073 and never answers WONT to SB.
            using var stream = new ScriptedStream(Iac, Sb);
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1);
            (await ReadOnceAsync(sut)).Should().BeEmpty();
            stream.Enqueue(99, 65, Iac, Se);
            (await ReadOnceAsync(sut)).Should().BeEmpty();
            stream.ByteWrites.Should().ContainSingle().Which.Should().Equal(new byte[] { Iac, Wont, 99 });
        }

        [Fact]
        public async Task SplitIacVerb_SurfacesWillReplyAfterContinuation()
        {
            // Same framing rule as SplitIacDo: IAC DO split across reads is
            // one command (RFC 854 command structure, RFC 1143 section 7).
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
            // RFC 854 (NVT printer/keyboard): CR NUL is how a lone carriage
            // return travels, and a NUL after CR is stripped. TCP is a byte
            // stream, so the rule holds when CR and NUL arrive in different
            // reads; telnetlib3 likewise withholds a trailing bare CR and
            // trims the NUL. The second read must therefore be empty.
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
            // RFC 854: the frame ends at IAC SE wherever the boundary falls,
            // and bytes after it are ordinary stream data (here "A").
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
            // RFC 854 defines no nesting: the outer frame is dropped, but the
            // inner frame is complete (IAC SB 99 [65] IAC SE) and is scanned
            // fresh like the reference (which clears its SB buffer and
            // re-buffers from the inner SB). The inner dispatch then follows
            // the normal stray-payload rule (WONT 99 for non-SEND); see F-N10.
            using var stream = new ScriptedStream(Iac, Sb, 24, 1, Iac, Sb, 99, 65, Iac, Se);
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1);
            (await ReadOnceAsync(sut)).Should().BeEmpty();
            stream.ByteWrites.Should().ContainSingle().Which.Should().Equal(new byte[] { Iac, Wont, 99 });
        }

        [Fact]
        public async Task EmptyMccpSb_WithoutAgreement_DoesNotSwallowData()
        {
            // MCCP v2 starts compression only after WILL COMPRESS2 / DO
            // COMPRESS2 agreement, via the server's empty IAC SB COMPRESS2
            // IAC SE. With no such agreement this SB is meaningless, so it
            // must be ignored and the following bytes stay plaintext.
            using var stream = new ScriptedStream(Iac, Sb, 86, Iac, Se, 72, 69, 76, 76, 79);
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1);
            (await ReadOnceAsync(sut)).Should().Be("HELLO");
        }

        [Fact]
        public async Task MisalignedSlc_ThrowsInvalidDataOutOfRead()
        {
            // telnetlib3 raises out of feed_byte on a misaligned SLC tail
            // ("SLC buffer wrong size: expect multiple of 3"); this stack
            // documents the same fail-fast contract rather than silently
            // swallowing a malformed peer frame. Open question whether the
            // read loop should instead contain per-SB errors; changing that
            // would diverge from the reference.
            using var stream = new ScriptedStream(Iac, Sb, 34, 3, 3, 2, 5, 9, Iac, Se);
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1);
            Func<Task<string>> read = () => ReadOnceAsync(sut);
            await read.Should().ThrowAsync<InvalidDataException>();
        }

        [Fact]
        public async Task SocketError_PropagatesToCaller()
        {
            // Reassessment: the handler documents propagation ("anything
            // else the stream throws, notably SocketException,
            // propagates") and both callers (client, server session) catch
            // SocketException at their layer, so throwing here is the
            // intended boundary, not a missed partial return.
            using var stream = new SocketFailingStream();
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1);
            Func<Task<string>> read = () => ReadOnceAsync(sut);
            await read.Should().ThrowAsync<SocketException>();
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
