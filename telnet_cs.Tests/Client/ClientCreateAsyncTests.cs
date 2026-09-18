namespace telnet_cs.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using FakeItEasy;
    using FluentAssertions;
    using Xunit;
    using telnet_cs.Client;
    using telnet_cs.Protocol;
    using telnet_cs.Transport;

    public class ClientCreateAsyncTests
    {
        private static IByteStream ConnectedFake()
        {
            var fake = A.Fake<IByteStream>();
            A.CallTo(() => fake.Connected).Returns(true);
            return fake;
        }

        [Fact]
        public async Task CreateAsync_NullStream_ThrowsArgumentNull()
        {
            Func<Task> act = () => Client.CreateAsync(null!, TimeSpan.FromMilliseconds(10));
            await act.Should().ThrowAsync<ArgumentNullException>().WithParameterName("byteStream");
        }

        [Fact]
        public async Task CreateAsync_UnconnectedStream_ThrowsUnableToConnect()
        {
            var fake = A.Fake<IByteStream>();
            A.CallTo(() => fake.Connected).Returns(false);
            Func<Task> act = () => Client.CreateAsync(fake, TimeSpan.FromMilliseconds(20));
            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Unable to connect to the host.");
        }

        [Fact]
        public async Task CreateAsync_PreCancelledToken_ThrowsOperationCanceled()
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            var fake = A.Fake<IByteStream>();
            A.CallTo(() => fake.Connected).Returns(false);
            Func<Task> act = () => Client.CreateAsync(fake, TimeSpan.FromSeconds(5), cts.Token);
            await act.Should().ThrowAsync<OperationCanceledException>();
        }

        [Fact]
        public async Task CreateAsync_SkipByDefault_SendsNothing()
        {
            using (GlobalStateGuard.SkipProactive(true))
            {
                var fake = ConnectedFake();
                using var _ = await Client.CreateAsync(fake, TimeSpan.FromMilliseconds(50), default);
                A.CallTo(() => fake.WriteAsync(A<byte[]>.Ignored, A<int>.Ignored, A<int>.Ignored, A<CancellationToken>.Ignored)).MustNotHaveHappened();
            }
        }

        [Fact]
        public async Task CreateAsync_NullOptions_BehavesLikeEmpty()
        {
            using (GlobalStateGuard.SkipProactive(true))
            {
                var fake = ConnectedFake();
                using var _ = await Client.CreateAsync(fake, TimeSpan.FromMilliseconds(50), default, null, skipProactiveNegotiation: false);
                A.CallTo(() => fake.WriteAsync(A<byte[]>.Ignored, A<int>.Ignored, A<int>.Ignored, A<CancellationToken>.Ignored)).MustNotHaveHappened();
            }
        }

        [Fact]
        public async Task CreateAsync_ProactiveOptIn_SendsSuppressGoAhead()
        {
            using (GlobalStateGuard.SkipProactive(false))
            {
                var fake = ConnectedFake();
                using var _ = await Client.CreateAsync(fake, TimeSpan.FromMilliseconds(50), default, [], skipProactiveNegotiation: false);
                A.CallTo(() => fake.WriteAsync(A<byte[]>.Ignored, 0, 3, A<CancellationToken>.Ignored))
                  .WhenArgumentsMatch(o => o[0] is byte[] b && b.SequenceEqual(Client.SuppressGoAheadBuffer))
                  .MustHaveHappened();
            }
        }

        [Fact]
        public async Task CreateAsync_CustomOptions_SendsExactTriplesInOrder()
        {
            using (GlobalStateGuard.SkipProactive(true))
            {
                var fake = ConnectedFake();
                var writes = new List<byte[]>();
                A.CallTo(() => fake.WriteAsync(A<byte[]>.Ignored, A<int>.Ignored, A<int>.Ignored, A<CancellationToken>.Ignored))
                  .Invokes(call => writes.Add(((byte[])call.Arguments[0]!).ToArray()));
                using var _ = await Client.CreateAsync(
                    fake,
                    TimeSpan.FromMilliseconds(50),
                    default,
                    [(Commands.Do, Options.Echo), (Commands.Will, Options.WindowSize)],
                    skipProactiveNegotiation: false);
                writes.Should().HaveCount(2);
                writes[0].Should().Equal([255, 253, 1]);
                writes[1].Should().Equal([255, 251, 31]);
            }
        }
    }
}
