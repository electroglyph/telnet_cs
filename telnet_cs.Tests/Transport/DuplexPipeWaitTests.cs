namespace telnet_cs.Tests
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using FluentAssertions;
    using Xunit;
    using telnet_cs.Transport;

    public class DuplexPipeWaitTests
    {
        [Fact]
        public async Task WaitForData_PeerWrite_ReturnsTrue()
        {
            var (endA, endB) = DuplexPipe.Create();
            using (endA)
            using (endB)
            {
                endA.WaitForData(TimeSpan.FromMilliseconds(20), CancellationToken.None).Should().BeFalse();
                await endB.WriteByteAsync(42, CancellationToken.None);
                endA.WaitForData(TimeSpan.FromSeconds(5), CancellationToken.None).Should().BeTrue();
            }
        }

        [Fact]
        public void WaitForData_Timeout_ReturnsFalse()
        {
            var (endA, endB) = DuplexPipe.Create();
            using (endA)
            using (endB)
            {
                endA.WaitForData(TimeSpan.FromMilliseconds(20), CancellationToken.None).Should().BeFalse();
            }
        }

        [Fact]
        public async Task WaitForData_PeerClose_WakesWaiter()
        {
            var (endA, endB) = DuplexPipe.Create();
            using (endA)
            using (endB)
            {
                var pending = Task.Run(() => endA.WaitForData(TimeSpan.FromSeconds(5), CancellationToken.None));
                await Task.Delay(50);
                endB.Close();
                (await pending).Should().BeTrue();
            }
        }

        [Fact]
        public void WaitForData_CancelledToken_ThrowsOperationCanceled()
        {
            var (endA, endB) = DuplexPipe.Create();
            using (endA)
            using (endB)
            {
                using var cts = new CancellationTokenSource();
                cts.Cancel();
                Action act = () => endA.WaitForData(TimeSpan.FromSeconds(5), cts.Token);
                act.Should().Throw<OperationCanceledException>();
            }
        }
    }
}
