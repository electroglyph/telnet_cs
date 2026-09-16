namespace telnet_cs.Tests
{
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using FluentAssertions;
    using Xunit;

    /// <summary>
    /// Pins the <see cref="telnet_cs.Fuzz.FuzzStream"/> test hooks: EOF-on-drain
    /// disconnects a drained stream while writes keep sinking, and the one-shot
    /// transport faults throw exactly once.
    /// </summary>
    public class FuzzStreamTests
    {
        [Fact]
        public void EofOnDrain_DrainedStream_ReadsDisconnected()
        {
            var stream = new telnet_cs.Fuzz.FuzzStream { EofOnDrain = true };
            stream.Enqueue([0x41]);

            stream.ReadByte().Should().Be(0x41);
            stream.Connected.Should().BeFalse();
        }

        [Fact]
        public async Task EofOnDrain_AfterDisconnect_WritesStillSink()
        {
            var stream = new telnet_cs.Fuzz.FuzzStream { EofOnDrain = true };

            await stream.WriteByteAsync(0x42, CancellationToken.None);

            stream.OutboundSignature().Count.Should().Be(1);
        }

        [Fact]
        public void ThrowOnRead_ThrowsOnceThenDrains()
        {
            var stream = new telnet_cs.Fuzz.FuzzStream { ThrowOnRead = true };
            stream.Enqueue([0x41]);

            var act = () => stream.ReadByte();
            act.Should().Throw<IOException>("first read takes the injected fault");
            stream.ReadByte().Should().Be(0x41, "the fault is one-shot");
        }

        [Fact]
        public async Task ThrowOnWrite_ThrowsOnceThenSinks()
        {
            var stream = new telnet_cs.Fuzz.FuzzStream { ThrowOnWrite = true };

            var act = () => stream.WriteByteAsync(0x41, CancellationToken.None);
            (await act.Should().ThrowAsync<IOException>()).WithMessage("*fault*");
            await stream.WriteByteAsync(0x41, CancellationToken.None);

            stream.OutboundSignature().Count.Should().Be(1, "only the post-fault write sinks");
        }
    }
}
