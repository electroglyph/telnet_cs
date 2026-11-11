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

    public class SubnegFramingTransportOverloadTests
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
            using CancellationTokenSource cts = new();
            using ByteStreamHandler sut = new(fake, cts, 1);
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().BeEmpty();
            A.CallTo(() => fake.WriteAsync(A<byte[]>.Ignored, A<int>.Ignored, A<int>.Ignored, A<CancellationToken>.Ignored)).MustNotHaveHappened();
        }

        [Fact]
        public async Task IacAsSubnegotiationOptionIsIgnored()
        {
            using ScriptedStream stream = new(255, 250, 255);
            using CancellationTokenSource cts = new();
            using ByteStreamHandler sut = new(stream, cts, 1);
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().BeEmpty();
            stream.ByteWrites.Should().BeEmpty();
        }

        [Fact]
        public async Task TruncatedSubnegotiationEndingInIacIsIgnored()
        {
            // IAC with no following byte: the scanner cannot frame, so it aborts.
            using ScriptedStream stream = new(255, 250, 24, 1, 255);
            using CancellationTokenSource cts = new();
            using ByteStreamHandler sut = new(stream, cts, 1);
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().BeEmpty();
            stream.ByteWrites.Should().BeEmpty();
        }

        [Fact]
        public async Task IacFollowedByDataByteAbortsSubnegotiation()
        {
            // IAC followed by anything but IAC/SE: framing is lost, give up.
            using ScriptedStream stream = new(255, 250, 24, 1, 255, 65);
            using CancellationTokenSource cts = new();
            using ByteStreamHandler sut = new(stream, cts, 1);
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
            using ScriptedStream stream = new(reads.ToArray());
            using CancellationTokenSource cts = new();
            using ByteStreamHandler sut = new(stream, cts, 1);
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(100))).Should().BeEmpty();
            stream.ByteWrites.Should().BeEmpty();
        }

        [Fact]
        public async Task NawsEscapesIacBytesInDimensions()
        {
            // A 0xFF dimension byte must be doubled on the wire (RFC 854).
            using ScriptedStream stream = new(255, 253, 31);
            using CancellationTokenSource cts = new();
            using ByteStreamHandler sut = new(stream, cts, 1);
            sut.WindowWidth = 65535;
            sut.WindowHeight = 24;
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(100))).Should().BeEmpty();
            stream.ByteWrites.Should().HaveCount(2);
            stream.ByteWrites[1].Should().Equal(new byte[]
            {
        255, 250, 31, 255, 255, 255, 255, 0, 24, 255, 240,
            });
        }

        [Fact]
        public async Task HandlerIsWriteConsoleWritesRead()
        {
            using ScriptedStream stream = new("Hi");
            using CancellationTokenSource cts = new();
            using ByteStreamHandler sut = new(stream, cts, 1);
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
            using ScriptedStream stream = new();
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
            using ScriptedStream stream = new("Account:");
            using var client = await Client.CreateAsync(stream, TimeSpan.FromSeconds(30), new CancellationToken());
            var result = await client.TerminatedReadAsync(new[] { "zzz", "Account:" });
            result.Should().Be("Account:");
        }

        [Fact]
        public async Task RegexCollectionShortOverloadsRead()
        {
            using ScriptedStream stream = new("Password:");
            using var client = await Client.CreateAsync(stream, TimeSpan.FromSeconds(30), new CancellationToken());
            var oneArg = await client.TerminatedReadAsync(new[] { new Regex("zzz"), new Regex("word") });
            // "word" matches mid-buffer: the cut keeps "Password", ":" stashes.
            oneArg.Should().Be("Password");
            (await client.ReadAsync(TimeSpan.FromSeconds(2))).Should().Be(":");
            using ScriptedStream stream2 = new("Password:");
            using var client2 = await Client.CreateAsync(stream2, TimeSpan.FromSeconds(30), new CancellationToken());
            var twoArg = await client2.TerminatedReadAsync(
              new[] { new Regex("zzz"), new Regex("word") }, TimeSpan.FromSeconds(2));
            twoArg.Should().Be("Password");
            (await client2.ReadAsync(TimeSpan.FromSeconds(2))).Should().Be(":");
        }

        [Fact]
        public async Task FailedRegexMatchLogsToBothHooks()
        {
            // The wait throws TimeoutException before any "Failed to match"
            // log is written, so neither hook sees it — only the throw itself
            // is pinned.
            var logged = new List<string>();
            var traced = new List<string>();
            using (GlobalStateGuard.Trace(traced.Add))
            {
                using ScriptedStream stream = new();
                using var client = await Client.CreateAsync(stream, TimeSpan.FromSeconds(30), new CancellationToken());
                client.Settings.Log = logged.Add;
                Func<Task> act = () => client.TerminatedReadAsync(
                  new Regex("ZZZ-never-matches"), TimeSpan.FromMilliseconds(60), 1);
                await act.Should().ThrowAsync<TimeoutException>();
                logged.Should().NotContain(x => x.Contains("Failed to match"));
                traced.Should().NotContain(x => x.Contains("Failed to match"));
            }
        }

        [Fact]
        public async Task PreCancelledReadReturnsEmpty()
        {
            using ScriptedStream stream = new("AB");
            using var client = await Client.CreateAsync(stream, TimeSpan.FromSeconds(30), new CancellationToken());
            var result = await client.ReadAsync(TimeSpan.FromSeconds(1), new CancellationToken(true));
            result.Should().BeEmpty();
        }

        [Fact]
        public void ClientCancelPendingReadsAfterDisposeIsSwallowed()
        {
            // Cancel() on the disposed internal source throws; CancelPendingReads swallows.
            using ScriptedStream stream = new();
            var probe = new ClientTerminatorConnectTests.ProbeClient(stream);
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
            using TcpByteStream sut = new(socket);
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
            using TcpByteStream sut = new(socket);
            sut.ReadByte().Should().Be(-1);
        }

        [Fact]
        public void TcpClientSocketOptionsRoundTrip()
        {
            using DummyTelnetServer server = new();
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
