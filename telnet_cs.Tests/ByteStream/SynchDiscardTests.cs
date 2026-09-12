namespace telnet_cs.Tests
{
    using System;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using FluentAssertions;
    using Xunit;
    using telnet_cs.IO;

    public class SynchDiscardTests
    {
        private static async Task<(string Output, ScriptedStream Stream)> ReadDiscardingAsync(params int[] reads)
        {
            var stream = new ScriptedStream(reads);
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1);
            sut.EnterSynchDiscard();
            var output = await sut.ReadAsync(TimeSpan.FromMilliseconds(50));
            return (output, stream);
        }

        [Fact]
        public async Task DropsDataUntilDm()
        {
            // Pre-DM bytes vanish; DM ends the scan and later data surfaces.
            var (output, stream) = await ReadDiscardingAsync(65, 66, 255, 242, 67);
            output.Should().Be("C");
            stream.ByteWrites.Should().BeEmpty();
        }

        [Fact]
        public async Task EcElSwallowed_DmStillTerminates()
        {
            // RFC 854 excludes EC/EL from the interesting set: swallowed, and the
            // scan continues to DM.
            var (output, stream) = await ReadDiscardingAsync(255, 247, 255, 248, 255, 242, 65);
            output.Should().Be("A");
            stream.ByteWrites.Should().BeEmpty();
        }

        [Fact]
        public async Task AytSilent_DmTerminates()
        {
            // Interesting signals still dispatch, but AYT is consumed without
            // reply (no proof-alive bytes, even mid-scan).
            var (output, stream) = await ReadDiscardingAsync(255, 246, 255, 242, 65);
            output.Should().Be("A");
            stream.ByteWrites.Should().BeEmpty();
        }

        [Fact]
        public async Task NegotiationPassesDuringDiscard()
        {
            // Non-edit commands dispatch normally mid-scan (here DO TTYPE earns
            // WILL); only data is dropped.
            var (output, stream) = await ReadDiscardingAsync(255, 253, 24, 255, 242, 65);
            output.Should().Be("A");
            stream.ByteWrites.Should().ContainSingle().Which.Should().Equal(new byte[] { 255, 251, 24 });
        }

        [Fact]
        public async Task IpCancelsRead()
        {
            // IP keeps its normal meaning mid-scan: pending reads cancel.
            var (output, _) = await ReadDiscardingAsync(255, 244, 65, 255, 242, 66);
            output.Should().BeEmpty();
        }

        [Fact]
        public async Task ReenterAfterDm_DiscardsAgain()
        {
            // Post-DM data surfaces; a fresh Synch starts a fresh scan.
            using var stream = new ScriptedStream();
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1);
            sut.EnterSynchDiscard();
            stream.Enqueue(65, 255, 242, 66);
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().Be("B");
            sut.EnterSynchDiscard();
            stream.Enqueue(67, 255, 242, 68);
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().Be("D");
            stream.ByteWrites.Should().BeEmpty();
        }
    }
}
