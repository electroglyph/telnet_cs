namespace telnet_cs.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Threading.Tasks;
    using FakeItEasy;
    using FluentAssertions;
    using Xunit;
    using telnet_cs.Client;
    using telnet_cs.Protocol;
    using telnet_cs.Transport;

    public class ClientEdgeTests
    {
        private static IByteStream ConnectedFake()
        {
            var fake = A.Fake<IByteStream>();
            A.CallTo(() => fake.Connected).Returns(true);
            return fake;
        }

        [Fact]
        public void CtorNullStreamThrowsArgumentNull()
        {
            Action act = () => new Client(null!, new CancellationToken());
            act.Should().Throw<ArgumentNullException>().WithParameterName("byteStream");
        }

        [Fact]
        public void CtorNullOptionsThrowsArgumentNull()
        {
            // PROPER (test.md §3.6): options must be null-checked. Currently fails:
            // the foreach over options throws NullReferenceException.
            var fake = ConnectedFake();
            Action act = () => new Client(fake, TimeSpan.FromMilliseconds(10), default, null!);
            act.Should().Throw<ArgumentNullException>().WithParameterName("options");
        }

        [Fact]
        public void CtorStreamFailureThrowsIOExceptionDirectly()
        {
            // PROPER: a negotiation write failure must surface as its own exception,
            // not wrapped in AggregateException. Currently fails: the ctor's
            // Task.Run(...).Wait() wraps the IOException in an AggregateException.
            // NOTE: FluentAssertions' Throw<T> unwraps single-inner AggregateExceptions,
            // so the raw exception must be captured to pin this (see test.md §8).
            var fake = ConnectedFake();
            A.CallTo(() => fake.WriteAsync(A<byte[]>.Ignored, A<int>.Ignored, A<int>.Ignored, A<CancellationToken>.Ignored))
              .Throws(new IOException("boom"));
            var ex = Record.Exception(() => new Client(fake, TimeSpan.FromMilliseconds(10), default));
            ex.Should().BeOfType<IOException>().Which.Message.Should().Be("boom");
        }

        [Fact]
        public void DisposeCompletesWithoutArtificialDelay()
        {
            // PROPER: Dispose must not sleep ~100ms on an AutoResetEvent.
            // Currently fails: BaseClientCancellable.Dispose always waits 100ms.
            var fake = ConnectedFake();
            var sut = new Client(fake, TimeSpan.FromMilliseconds(10), default);
            var sw = Stopwatch.StartNew();
            sut.Dispose();
            sw.Stop();
            sw.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(50));
        }

        [Fact]
        public void CtorUnconnectedThrowsQuickly()
        {
            var fake = A.Fake<IByteStream>();
            A.CallTo(() => fake.Connected).Returns(false);
            var sw = Stopwatch.StartNew();
            Action act = () => new Client(fake, TimeSpan.FromMilliseconds(20), default);
            act.Should().Throw<InvalidOperationException>().WithMessage("Unable to connect to the host.");
            sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
        }

        [Fact]
        public async Task CtorDefaultSendsSuppressGoAhead()
        {
            using (GlobalStateGuard.SkipProactive(false))
            {
                var fake = ConnectedFake();
                using var _ = new Client(fake, TimeSpan.FromMilliseconds(10), default);
                A.CallTo(() => fake.WriteAsync(A<byte[]>.Ignored, 0, 3, A<CancellationToken>.Ignored))
                  .WhenArgumentsMatch(o => o[0] is byte[] b && b.SequenceEqual(Client.SuppressGoAheadBuffer))
                  .MustHaveHappened();
            }
            await Task.CompletedTask;
        }

        [Fact]
        public async Task CtorSkipNegotiationSendsNothing()
        {
            using (GlobalStateGuard.SkipProactive(true))
            {
                var fake = ConnectedFake();
                using var _ = new Client(fake, TimeSpan.FromMilliseconds(10), default);
                A.CallTo(() => fake.WriteAsync(A<byte[]>.Ignored, A<int>.Ignored, A<int>.Ignored, A<CancellationToken>.Ignored)).MustNotHaveHappened();
            }
            await Task.CompletedTask;
        }

        [Fact]
        public async Task CtorCustomOptionsSendExactTriplesInOrder()
        {
            using (GlobalStateGuard.SkipProactive(true))
            {
                var fake = ConnectedFake();
                var writes = new List<byte[]>();
                A.CallTo(() => fake.WriteAsync(A<byte[]>.Ignored, A<int>.Ignored, A<int>.Ignored, A<CancellationToken>.Ignored))
                  .Invokes(call => writes.Add(((byte[])call.Arguments[0]!).ToArray()));
                using var _ = new Client(fake, TimeSpan.FromMilliseconds(10), default,
                  new[] { (Commands.Do, Options.Echo), (Commands.Will, Options.WindowSize) });
                writes.Should().HaveCount(2);
                writes[0].Should().Equal(new byte[] { 255, 253, 1 });
                writes[1].Should().Equal(new byte[] { 255, 251, 31 });
            }
            await Task.CompletedTask;
        }

        [Fact]
        public async Task WriteLineAppendsLegacyFeed()
        {
            var fake = ConnectedFake();
            using var sut = new Client(fake, TimeSpan.FromMilliseconds(10), default);
            A.CallTo(() => fake.WriteAsync(A<byte[]>.Ignored, A<int>.Ignored, A<int>.Ignored, A<CancellationToken>.Ignored));
            await sut.WriteLineAsync("cmd");
            A.CallTo(() => fake.WriteAsync("cmd\n", A<CancellationToken>.Ignored)).MustHaveHappened();
        }

        [Fact]
        public async Task WriteLineRfc854AppendsCrLf()
        {
            var fake = ConnectedFake();
            using var sut = new Client(fake, TimeSpan.FromMilliseconds(10), default);
            await sut.WriteLineRfc854Async("cmd");
            A.CallTo(() => fake.WriteAsync("cmd\r\n", A<CancellationToken>.Ignored)).MustHaveHappened();
        }

        [Fact]
        public async Task WriteByteArrayRelaysOffsetCount()
        {
            var fake = ConnectedFake();
            using var sut = new Client(fake, TimeSpan.FromMilliseconds(10), default);
            var data = new byte[] { 1, 2, 3 };
            await sut.WriteAsync(data);
            A.CallTo(() => fake.WriteAsync(data, 0, 3, A<CancellationToken>.Ignored)).MustHaveHappened();
        }

        [Fact]
        public async Task WriteNullByteArrayThrowsArgumentNull()
        {
            // PROPER (test.md §3.4): null data must throw ArgumentNullException.
            // Currently fails: data.Length throws NullReferenceException.
            var fake = ConnectedFake();
            using var sut = new Client(fake, TimeSpan.FromMilliseconds(10), default);
            Func<Task> act = () => sut.WriteAsync((byte[])null!);
            await act.Should().ThrowAsync<ArgumentNullException>().WithParameterName("data");
        }

        [Fact]
        public async Task WriteWhenDisconnectedIsNoop()
        {
            var connected = true;
            var fake = A.Fake<IByteStream>();
            A.CallTo(() => fake.Connected).ReturnsLazily(() => connected);
            using var sut = new Client(fake, TimeSpan.FromMilliseconds(10), default);
            connected = false;
            await sut.WriteAsync("hi");
            await sut.WriteAsync(new byte[] { 1 });
            // Proactive SGA happens in ctor while connected; post-disconnect writes must be no-ops.
            A.CallTo(() => fake.WriteAsync(A<string>.Ignored, A<CancellationToken>.Ignored)).MustNotHaveHappened();
            A.CallTo(() => fake.WriteAsync(A<byte[]>.Ignored, A<int>.Ignored, A<int>.Ignored, A<CancellationToken>.Ignored)).MustHaveHappenedOnceExactly();
            // ctor SGA did happen exactly once before disconnect
            A.CallTo(() => fake.WriteAsync(A<byte[]>.Ignored, 0, 3, A<CancellationToken>.Ignored)).MustHaveHappenedOnceExactly();
        }

        [Fact]
        public async Task WriteAfterExternalCancelIsNoop()
        {
            var fake = ConnectedFake();
            using var cts = new CancellationTokenSource();
            using var sut = new Client(fake, TimeSpan.FromMilliseconds(10), cts.Token);
            cts.Cancel();
            await Task.Delay(20); // let Register(CancelPendingReads) propagate
            Fake.ClearRecordedCalls(fake);
            await sut.WriteAsync("hi");
            A.CallTo(() => fake.WriteAsync(A<string>.Ignored, A<CancellationToken>.Ignored)).MustNotHaveHappened();
        }

        [Fact]
        public async Task WritePropagatesStreamException()
        {
            // Propagation holds before and after the semaphore fix; the no-hang
            // half of test.md §3.4 is pinned by SecondWriteAfterFailureCompletesPromptly.
            var fake = ConnectedFake();
            A.CallTo(() => fake.WriteAsync(A<string>.Ignored, A<CancellationToken>.Ignored))
              .ThrowsAsync(new IOException("boom"));
            using var sut = new Client(fake, TimeSpan.FromMilliseconds(10), default);
            Func<Task> act = () => sut.WriteAsync("hi");
            await act.Should().ThrowAsync<IOException>().WithMessage("boom");
        }

        [Fact]
        public async Task SecondWriteAfterFailureCompletesPromptly()
        {
            // PROPER (test.md §3.4): WriteAsync must release the send semaphore in a
            // finally, so a failed write cannot hang the next one. Currently fails:
            // the semaphore stays taken and the second write never completes (the
            // Task.WhenAny guard keeps this red-instead-of-hung).
            var fake = ConnectedFake();
            A.CallTo(() => fake.WriteAsync(A<string>.Ignored, A<CancellationToken>.Ignored))
              .ThrowsAsync(new IOException("boom"));
            using var sut = new Client(fake, TimeSpan.FromMilliseconds(10), default);
            Func<Task> first = () => sut.WriteAsync("hi");
            await first.Should().ThrowAsync<IOException>();
            var second = sut.WriteAsync("again");
            var winner = await Task.WhenAny(second, Task.Delay(TimeSpan.FromSeconds(2)));
            winner.Should().BeSameAs(second, "second write hung: the send semaphore was leaked by the failed write");
            try
            {
                await second;
            }
            catch (IOException)
            {
            }
        }

        [Fact]
        public async Task TerminatedReadDefaultTimeoutOverloadReadsAccountPrompt()
        {
            using var stream = new DummyByteStream();
            using var sut = new Client(stream, new CancellationToken());
            (await sut.TerminatedReadAsync(":")).Should().EndWith(":");
        }

        [Fact]
        public async Task ReadDefaultOverloadReadsAccountPrompt()
        {
            using var stream = new DummyByteStream();
            using var sut = new Client(stream, new CancellationToken());
            (await sut.ReadAsync()).Should().Contain("Account:");
        }

        [Fact]
        public async Task TerminatedReadEmptyTerminatorReturnsAfterFirstRead()
        {
            using var stream = new DummyByteStream();
            using var sut = new Client(stream, new CancellationToken());
            var s = await sut.TerminatedReadAsync(string.Empty, TimeSpan.FromMilliseconds(500), 1);
            s.Should().NotBeNull();
        }

        [Fact]
        public async Task TerminatedReadNullRegexThrowsArgumentNull()
        {
            // PROPER: a null regex is a caller contract violation. Currently fails:
            // IsRegexLocated(null, s) returns false so the read loop runs, then
            // Client.cs:107 calls regex.ToString() for the debug message ->
            // NullReferenceException.
            var fake = A.Fake<IByteStream>();
            A.CallTo(() => fake.Connected).Returns(true);
            using var sut = new Client(fake, TimeSpan.FromMilliseconds(1), default) { MillisecondReadDelay = 1 };
            Func<Task> act = () => sut.TerminatedReadAsync((Regex)null!, TimeSpan.FromMilliseconds(60), 1);
            await act.Should().ThrowAsync<ArgumentNullException>().WithParameterName("regex");
        }

        [Fact]
        public async Task TryLoginFailsFastWhenNoTerminator()
        {
            var fake = A.Fake<IByteStream>();
            A.CallTo(() => fake.Connected).Returns(true);
            using var sut = new Client(fake, TimeSpan.FromMilliseconds(1), default) { MillisecondReadDelay = 1 };
            (await sut.TryLoginAsync("u", "p", 60)).Should().BeFalse();
        }

        [Fact]
        public async Task ReadDoesNotCloseStream()
        {
            // Holds both before and after the handler-ownership fix (§3.5): today
            // Client.ReadAsync intentionally leaks the ByteStreamHandler (CA2000) so
            // the shared stream stays open; after the fix the handler is disposed
            // but must not dispose the stream it does not own.
            var fake = A.Fake<IByteStream>();
            A.CallTo(() => fake.Connected).Returns(true);
            using var sut = new Client(fake, TimeSpan.FromMilliseconds(1), default) { MillisecondReadDelay = 1 };
            await sut.ReadAsync(TimeSpan.FromMilliseconds(20));
            A.CallTo(() => fake.Dispose()).MustNotHaveHappened();
            A.CallTo(() => fake.Close()).MustNotHaveHappened();
        }

        [Fact]
        public void DisposeClosesStreamAndDoubleDisposeIsSafe()
        {
            var fake = ConnectedFake();
            using var sut = new Client(fake, TimeSpan.FromMilliseconds(10), default);
            Action act = () => { sut.Dispose(); sut.Dispose(); };
            act.Should().NotThrow();
            A.CallTo(() => fake.Close()).MustHaveHappened();
        }

        [Fact]
        public void IsConnectedReflectsStream()
        {
            var fake = A.Fake<IByteStream>();
            A.CallTo(() => fake.Connected).Returns(true);
            using var sut = new Client(fake, TimeSpan.FromMilliseconds(10), default);
            sut.IsConnected.Should().BeTrue();
            A.CallTo(() => fake.Connected).Returns(false);
            sut.IsConnected.Should().BeFalse();
        }
    }
}
