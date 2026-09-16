namespace telnet_cs.Tests
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using FluentAssertions;
    using Xunit;

    /// <summary>
    /// Pins multi-round minimization with synthetic probes: irrelevant rounds
    /// drop out, and a lone surviving round still gets delta-debugged.
    /// </summary>
    public class MinimizerSequenceTests
    {
        private static telnet_cs.Fuzz.FuzzInput Input(params byte[] bytes) => new(bytes, []);

        private static async Task<(long Outbound, long Hash)> ProbeBothMarkers(
            IReadOnlyList<telnet_cs.Fuzz.FuzzInput> seq, CancellationToken token)
        {
            await Task.Yield();
            var all = seq.SelectMany(static s => s.Bytes).ToArray();
            if (all.Contains((byte)0xAA) && all.Contains((byte)0xBB))
            {
                throw new InvalidOperationException("both markers present");
            }

            return (all.Length, 0);
        }

        private static async Task<(long Outbound, long Hash)> ProbeSingleMarker(
            IReadOnlyList<telnet_cs.Fuzz.FuzzInput> seq, CancellationToken token)
        {
            await Task.Yield();
            var all = seq.SelectMany(static s => s.Bytes).ToArray();
            if (all.Contains((byte)0xAA))
            {
                throw new InvalidOperationException("marker present");
            }

            return (all.Length, 0);
        }

        [Fact]
        public async Task MinimizeSequence_DropsIrrelevantRound()
        {
            var seq = new[] { Input(0xAA), Input(0xCC, 0xCC), Input(0xBB) };

            var min = await telnet_cs.Fuzz.Minimizer.MinimizeSequenceAsync(
                seq, ProbeBothMarkers, typeof(InvalidOperationException), CancellationToken.None);

            min.Select(s => s.Bytes).Should().BeEquivalentTo<byte[]>([[0xAA], [0xBB]]);
        }

        [Fact]
        public async Task MinimizeSequence_LoneSurvivor_ShrinksBytes()
        {
            var seq = new[] { Input(0xAA, 0x00, 0x00) };

            var min = await telnet_cs.Fuzz.Minimizer.MinimizeSequenceAsync(
                seq, ProbeSingleMarker, typeof(InvalidOperationException), CancellationToken.None);

            min.Should().ContainSingle().Which.Bytes.Should().Equal((byte)0xAA);
        }

        [Fact]
        public async Task MinimizeSequence_WrongExceptionType_KeepsSequence()
        {
            var seq = new[] { Input(0xAA), Input(0xBB) };

            var min = await telnet_cs.Fuzz.Minimizer.MinimizeSequenceAsync(
                seq, ProbeBothMarkers, typeof(IOException), CancellationToken.None);

            min.Should().HaveCount(2);
        }
    }
}
