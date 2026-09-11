namespace telnet_cs.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using FakeItEasy;
    using FluentAssertions;
    using Xunit;
    using telnet_cs.IO;

    public class AuditHandlerRegressionTests
    {
        private static ByteStreamHandler MakeHandler(ScriptedStream stream, out CancellationTokenSource cts, int readDelayMs = 1)
        {
            cts = new CancellationTokenSource();
            return new ByteStreamHandler(stream, cts, readDelayMs);
        }

        [Fact]
        public void CtorNullStreamThrows()
        {
            Action act = () => new ByteStreamHandler(null!);
            act.Should().Throw<ArgumentNullException>().WithParameterName("byteStream");
        }

        [Fact]
        public void CtorNullTokenSourceThrows()
        {
            using var stream = new ScriptedStream();
            Action act = () => new ByteStreamHandler(stream, null!);
            act.Should().Throw<ArgumentNullException>().WithParameterName("internalCancellation");
        }

        [Fact]
        public async Task NawsReplyIsBinaryRfc1073()
        {
            using var stream = new ScriptedStream(255, 253, 31);
            using var handler = MakeHandler(stream, out var cts);
            using (cts)
            {
                handler.WindowWidth = 132;
                handler.WindowHeight = 37;
                (await handler.ReadAsync(TimeSpan.FromMilliseconds(100))).Should().BeEmpty();
            }

            var expected = new byte[] { 255, 250, 31, 0, 0, 132, 0, 37, 255, 240 };
            stream.ByteWrites.Should().HaveCount(2);
            stream.ByteWrites[1].Should().Equal(expected);
        }

        [Fact]
        public async Task DuplicateNegotiationIsSuppressed()
        {
            // RFC 1143-lite: the same (verb, option) twice in a row gets one reply.
            using var stream = new ScriptedStream(255, 253, 3, 255, 253, 3);
            using var handler = MakeHandler(stream, out var cts);
            using (cts)
            {
                (await handler.ReadAsync(TimeSpan.FromMilliseconds(100))).Should().BeEmpty();
            }

            var replies = stream.ByteWrites.Where(b => b.Length == 3 && b[0] == 255 && b[1] == 251 && b[2] == 3).ToList();
            replies.Should().HaveCount(1);
        }

        [Fact]
        public async Task BinaryOptionIsAgreed()
        {
            using var stream = new ScriptedStream(255, 253, 0); // DO TransmitBinary
            using var handler = MakeHandler(stream, out var cts);
            using (cts)
            {
                (await handler.ReadAsync(TimeSpan.FromMilliseconds(100))).Should().BeEmpty();
            }

            stream.ByteWrites.Should().ContainSingle()
              .Which.Should().Equal(new byte[] { 255, 251, 0 });
        }

        [Fact]
        public async Task IacInOptionPositionIsIgnored()
        {
            using var stream = new ScriptedStream(255, 253, 255);
            using var handler = MakeHandler(stream, out var cts);
            using (cts)
            {
                (await handler.ReadAsync(TimeSpan.FromMilliseconds(100))).Should().BeEmpty();
            }

            stream.ByteWrites.Should().BeEmpty();
        }

        [Fact]
        public async Task TruncatedOptionIsIgnored()
        {
            using var stream = new ScriptedStream(255, 253);
            using var handler = MakeHandler(stream, out var cts);
            using (cts)
            {
                (await handler.ReadAsync(TimeSpan.FromMilliseconds(100))).Should().BeEmpty();
            }

            stream.ByteWrites.Should().BeEmpty();
        }

        [Fact]
        public async Task LongSubnegotiationResynchronises()
        {
            // 600-byte payload exceeds the 512 cap: consumed for resync, ignored,
            // and the trailing text still parses.
            var script = new List<int> { 255, 250, 31, 1 };
            for (var i = 0; i < 600; i++)
            {
                script.Add(65);
            }

            script.AddRange(new[] { 255, 240, 72, 73 });
            using var stream = new ScriptedStream(script.ToArray());
            using var handler = MakeHandler(stream, out var cts);
            string result;
            using (cts)
            {
                result = await handler.ReadAsync(TimeSpan.FromMilliseconds(200));
            }

            result.Should().Be("HI");
            stream.ByteWrites.Should().BeEmpty();
        }

        [Fact]
        public async Task EscapedIacInsideSubnegotiation()
        {
            using var stream = new ScriptedStream(255, 250, 24, 1, 255, 255, 255, 240, 79, 75);
            using var handler = MakeHandler(stream, out var cts);
            string result;
            using (cts)
            {
                result = await handler.ReadAsync(TimeSpan.FromMilliseconds(100));
            }

            result.Should().Be("OK");
            var expected = new byte[] { 255, 250, 24, 0 }
              .Concat(Encoding.ASCII.GetBytes("vt100"))
              .Concat(new byte[] { 255, 240 }).ToArray();
            stream.ByteWrites.Should().ContainSingle()
              .Which.Should().Equal(expected);
        }

        [Fact]
        public async Task TruncatedSubnegotiationAbortsSilently()
        {
            using var stream = new ScriptedStream(255, 250, 24, 1);
            using var handler = MakeHandler(stream, out var cts);
            using (cts)
            {
                (await handler.ReadAsync(TimeSpan.FromMilliseconds(100))).Should().BeEmpty();
            }

            stream.ByteWrites.Should().BeEmpty();
        }

        [Fact]
        public async Task MalformedSubnegotiationSendsWont()
        {
            using var stream = new ScriptedStream(255, 250, 24, 0, 255, 240);
            using var handler = MakeHandler(stream, out var cts);
            using (cts)
            {
                (await handler.ReadAsync(TimeSpan.FromMilliseconds(100))).Should().BeEmpty();
            }

            stream.ByteWrites.Should().ContainSingle()
              .Which.Should().Equal(new byte[] { 255, 252, 24 });
        }

        [Fact]
        public async Task BackspaceDeletesPreviousChar()
        {
            using var stream = new ScriptedStream(65, 66, 8, 67);
            using var handler = MakeHandler(stream, out var cts);
            string result;
            using (cts)
            {
                result = await handler.ReadAsync(TimeSpan.FromMilliseconds(100));
            }

            result.Should().Be("AC");
        }

        [Fact]
        public async Task BackspaceOnEmptyIsHarmless()
        {
            using var stream = new ScriptedStream(8, 65);
            using var handler = MakeHandler(stream, out var cts);
            string result;
            using (cts)
            {
                result = await handler.ReadAsync(TimeSpan.FromMilliseconds(100));
            }

            result.Should().Be("A");
        }

        [Fact]
        public async Task CrNulMeansBareCr()
        {
            using var stream = new ScriptedStream(65, 13, 0, 66);
            using var handler = MakeHandler(stream, out var cts);
            string result;
            using (cts)
            {
                result = await handler.ReadAsync(TimeSpan.FromMilliseconds(100));
            }

            result.Should().Be("A\rB");
        }

        [Fact]
        public async Task CrLfPassesThrough()
        {
            using var stream = new ScriptedStream(65, 13, 10, 66);
            using var handler = MakeHandler(stream, out var cts);
            string result;
            using (cts)
            {
                result = await handler.ReadAsync(TimeSpan.FromMilliseconds(100));
            }

            result.Should().Be("A\r\nB");
        }

        [Fact]
        public async Task BellSuppressedWhenDisabled()
        {
            using var stream = new ScriptedStream(7);
            using var handler = MakeHandler(stream, out var cts);
            using (cts)
            {
                handler.EnableBell = false;
                (await handler.ReadAsync(TimeSpan.FromMilliseconds(100))).Should().BeEmpty();
            }
        }

        [Fact]
        public async Task TerminalTypeIsLatin1Encoded()
        {
            // é must go out as single byte 0xE9 (Latin-1), not UTF-8 0xC3 0xA9.
            using var stream = new ScriptedStream(255, 250, 24, 1, 255, 240);
            using var handler = MakeHandler(stream, out var cts);
            using (cts)
            {
                handler.TerminalType = "caf\u00E9";
                (await handler.ReadAsync(TimeSpan.FromMilliseconds(100))).Should().BeEmpty();
            }

            var expected = new byte[] { 255, 250, 24, 0, 99, 97, 102, 233, 255, 240 };
            stream.ByteWrites.Should().ContainSingle()
              .Which.Should().Equal(expected);
        }

        [Fact]
        public async Task LogHookCapturesProtocolNotes()
        {
            using var stream = new ScriptedStream(21);
            var logged = new List<string>();
            using var handler = MakeHandler(stream, out var cts);
            string result;
            using (cts)
            {
                handler.Log = logged.Add;
                result = await handler.ReadAsync(TimeSpan.FromMilliseconds(100));
            }

            result.Should().Contain("NAK");
            logged.Should().Contain(m => m.Contains("NAK"));
        }

        [Fact]
        public async Task ReceiveTimeoutIsClampedToIntMax()
        {
            using var stream = new ScriptedStream(88); // 'X'
            using var cts = new CancellationTokenSource(50);
            using var handler = new ByteStreamHandler(stream, cts, 1);
            var result = await handler.ReadAsync(TimeSpan.FromMilliseconds((double)int.MaxValue + 1000));
            result.Should().Be("X");
            stream.LastReceiveTimeout.Should().Be(int.MaxValue);
        }

        [Fact]
        public async Task CancelledHandlerReturnsEmpty()
        {
            using var stream = new ScriptedStream("AB");
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            using var handler = new ByteStreamHandler(stream, cts, 1);
            (await handler.ReadAsync(TimeSpan.FromMilliseconds(100))).Should().BeEmpty();
        }
    }
}
