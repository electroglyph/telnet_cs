namespace telnet_cs.Tests
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text;
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Threading.Tasks;
    using FakeItEasy;
    using FluentAssertions;
    using Xunit;
    using telnet_cs.Client;
    using telnet_cs.IO;
    using telnet_cs.Transport;

    public class AuditCoverageTopUp2Tests
    {
        [Fact]
        public async Task IOExceptionDuringSubnegotiationAbortsItSilently()
        {
            // TryReadByte maps IOException to -1: the option byte never arrives,
            // so the truncated SB is ignored without reply or throw.
            var fake = A.Fake<IByteStream>();
            A.CallTo(() => fake.Connected).Returns(true);
            var first = true;
            A.CallTo(() => fake.Available).ReturnsLazily(() =>
            {
                if (first)
                {
                    first = false;
                    return 1;
                }

                return 0;
            });
            var reads = new Queue<int>(new[] { 255, 250 });
            A.CallTo(() => fake.ReadByte()).ReturnsLazily(() =>
              reads.Count > 0 ? reads.Dequeue() : throw new IOException("boom"));
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(fake, cts, 1);
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().BeEmpty();
            A.CallTo(() => fake.WriteAsync(A<byte[]>.Ignored, A<int>.Ignored, A<int>.Ignored, A<CancellationToken>.Ignored)).MustNotHaveHappened();
        }

        [Fact]
        public async Task IacAsSubnegotiationOptionIsIgnored()
        {
            using var stream = new ScriptedStream(255, 250, 255);
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1);
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().BeEmpty();
            stream.ByteWrites.Should().BeEmpty();
        }

        [Fact]
        public async Task TruncatedSubnegotiationEndingInIacIsIgnored()
        {
            // IAC with no following byte: the scanner cannot frame, so it aborts.
            using var stream = new ScriptedStream(255, 250, 24, 1, 255);
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1);
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().BeEmpty();
            stream.ByteWrites.Should().BeEmpty();
        }

        [Fact]
        public async Task IacFollowedByDataByteAbortsSubnegotiation()
        {
            // IAC followed by anything but IAC/SE: framing is lost, give up.
            using var stream = new ScriptedStream(255, 250, 24, 1, 255, 65);
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1);
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().BeEmpty();
            stream.ByteWrites.Should().BeEmpty();
        }

        [Fact]
        public async Task EscapedIacAtPayloadCapMarksOverCap()
        {
            // 512 payload bytes fill the cap; the escaped IAC then trips the
            // over-cap branch while the stream stays in sync through IAC SE.
            var reads = new List<int> { 255, 250, 99 };
            for (var i = 0; i < 512; i++)
            {
                reads.Add(65);
            }

            reads.AddRange(new[] { 255, 255, 255, 240 });
            using var stream = new ScriptedStream(reads.ToArray());
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1);
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(100))).Should().BeEmpty();
            stream.ByteWrites.Should().BeEmpty();
        }

        [Fact]
        public async Task NawsEscapesIacBytesInDimensions()
        {
            // A 0xFF dimension byte must be doubled on the wire (RFC 854).
            using var stream = new ScriptedStream(255, 253, 31);
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1);
            sut.WindowWidth = 65535;
            sut.WindowHeight = 24;
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(100))).Should().BeEmpty();
            stream.ByteWrites.Should().HaveCount(2);
            stream.ByteWrites[1].Should().Equal(new byte[]
            {
        255, 250, 31, 0, 255, 255, 255, 255, 0, 24, 255, 240,
            });
        }

        [Fact]
        public async Task HandlerIsWriteConsoleWritesRead()
        {
            using var stream = new ScriptedStream("Hi");
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1);
            sut.IsWriteConsole = true;
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().Be("Hi");
        }

        private sealed class ProbeHandler : ByteStreamHandler
        {
            public ProbeHandler(IByteStream stream, CancellationTokenSource cts)
              : base(stream, cts)
            {
            }

            public void ExposeCancelPendingReads()
            {
                CancelPendingReads();
            }
        }

        [Fact]
        public void HandlerCancelPendingReadsAfterDisposeIsSwallowed()
        {
            // Cancel() on a disposed source throws; the handler must swallow it.
            using var stream = new ScriptedStream();
            var cts = new CancellationTokenSource();
            var sut = new ProbeHandler(stream, cts);
            cts.Dispose();
            Action act = () => sut.ExposeCancelPendingReads();
            act.Should().NotThrow();
            sut.Dispose();
        }

        [Fact]
        public async Task SingleStringCollectionOverloadReads()
        {
            using var stream = new ScriptedStream("Account:");
            using var client = new Client(stream, new CancellationToken());
            var result = await client.TerminatedReadAsync(new[] { "zzz", "Account:" });
            result.Should().Be("Account:");
        }

        [Fact]
        public async Task RegexCollectionShortOverloadsRead()
        {
            using var stream = new ScriptedStream("Password:");
            using var client = new Client(stream, new CancellationToken());
            var oneArg = await client.TerminatedReadAsync(new[] { new Regex("zzz"), new Regex("word") });
            oneArg.Should().Be("Password:");
            using var stream2 = new ScriptedStream("Password:");
            using var client2 = new Client(stream2, new CancellationToken());
            var twoArg = await client2.TerminatedReadAsync(
              new[] { new Regex("zzz"), new Regex("word") }, TimeSpan.FromSeconds(2));
            twoArg.Should().Be("Password:");
        }

        [Fact]
        public async Task FailedRegexMatchLogsToBothHooks()
        {
            var logged = new List<string>();
            var traced = new List<string>();
            using (GlobalStateGuard.Trace(traced.Add))
            {
                using var stream = new ScriptedStream();
                using var client = new Client(stream, new CancellationToken());
                client.Settings.Log = logged.Add;
                var result = await client.TerminatedReadAsync(
                  new Regex("ZZZ-never-matches"), TimeSpan.FromMilliseconds(60), 1);
                result.Should().BeEmpty();
                logged.Should().ContainSingle(x => x.Contains("Failed to match"))
                  .Which.Should().Contain("ZZZ-never-matches");
                traced.Should().ContainSingle(x => x.Contains("Failed to match"))
                  .Which.Should().Contain("ZZZ-never-matches");
            }
        }

        [Fact]
        public async Task PreCancelledReadReturnsEmpty()
        {
            using var stream = new ScriptedStream("AB");
            using var client = new Client(stream, new CancellationToken());
            var result = await client.ReadAsync(TimeSpan.FromSeconds(1), new CancellationToken(true));
            result.Should().BeEmpty();
        }

        [Fact]
        public void ClientCancelPendingReadsAfterDisposeIsSwallowed()
        {
            // Cancel() on the disposed internal source throws; CancelPendingReads swallows.
            using var stream = new ScriptedStream();
            var probe = new AuditClientRegressionTests.ProbeClient(stream);
            probe.Dispose();
            Action act = () => probe.ExposeCancelPendingReads();
            act.Should().NotThrow();
        }

        [Fact]
        public async Task ConnectAsyncTimeoutThrowsInvalidOperation()
        {
            // 192.0.2.1 (TEST-NET-1) never answers: the timeout must fire first.
            Func<Task> act = () => Client.ConnectAsync("192.0.2.1", 1, default, TimeSpan.FromSeconds(1));
            (await act.Should().ThrowAsync<InvalidOperationException>())
              .WithMessage("*192.0.2.1*");
        }

        [Fact]
        public void TcpByteStreamReadByteMapsObjectDisposedToMinusOne()
        {
            var socket = A.Fake<ISocket>();
            A.CallTo(() => socket.Connected).Returns(true);
            var stream = A.Fake<INetworkStream>();
            A.CallTo(() => socket.GetStream()).Returns(stream);
            A.CallTo(() => stream.ReadByte()).Throws(new ObjectDisposedException("s"));
            using var sut = new TcpByteStream(socket);
            sut.ReadByte().Should().Be(-1);
        }

        [Fact]
        public void TcpByteStreamReadByteMapsInvalidOperationToMinusOne()
        {
            var socket = A.Fake<ISocket>();
            A.CallTo(() => socket.Connected).Returns(true);
            var stream = A.Fake<INetworkStream>();
            A.CallTo(() => socket.GetStream()).Returns(stream);
            A.CallTo(() => stream.ReadByte()).Throws(new InvalidOperationException("x"));
            using var sut = new TcpByteStream(socket);
            sut.ReadByte().Should().Be(-1);
        }

        [Fact]
        public void TcpClientSocketOptionsRoundTrip()
        {
            using var server = new DummyTelnetServer();
            using var sut = new telnet_cs.Transport.TcpClient(server.IPAddress.ToString(), server.Port);
            sut.SendTimeout = 1234;
            sut.SendTimeout.Should().Be(1234);
            sut.NoDelay = true;
            sut.NoDelay.Should().BeTrue();
            sut.KeepAlive = true;
            sut.KeepAlive.Should().BeTrue();
        }

        [Fact]
        public async Task NetworkStreamWriteByteAsyncRelays()
        {
            using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
                using var client = new System.Net.Sockets.TcpClient();
                await client.ConnectAsync("127.0.0.1", port);
                using var serverSide = await listener.AcceptTcpClientAsync();
                using var sut = new telnet_cs.Transport.NetworkStream(serverSide.GetStream());
                await sut.WriteByteAsync(0x41, CancellationToken.None);
                var buf = new byte[1];
                var read = await client.GetStream().ReadAsync(buf, 0, 1);
                read.Should().Be(1);
                buf[0].Should().Be(0x41);
            }
            finally
            {
                listener.Stop();
            }
        }

        [Fact]
        public void ToStringWithEncodingDecodes()
        {
            ByteStringConverter.ToString(new byte[] { 0xC3, 0xA9 }, Encoding.UTF8).Should().Be("é");
        }
    }
}
