namespace telnet_cs.Tests
{
    using System.Linq;
    using FluentAssertions;
    using Xunit;

    /// <summary>
    /// Pins the target-biased generator shapes: storm inputs skew long and
    /// verb-heavy, REPL inputs look like terminal lines, dispatch matches the
    /// dedicated generators, and generation stays deterministic.
    /// </summary>
    [Collection("Serial")]
    public class MutatorShapeTests
    {
        [Fact]
        public void Generate_DeterministicForSameSeedAndIteration()
        {
            telnet_cs.Fuzz.Mutator.LoadExternalCorpus(string.Empty);

            var first = telnet_cs.Fuzz.Mutator.Generate(11, 23, 256);
            var second = telnet_cs.Fuzz.Mutator.Generate(11, 23, 256);

            // Structural comparison: FuzzInput is a record over byte[], and
            // record equality would compare the arrays by reference.
            first.Bytes.Should().Equal(second.Bytes);
            first.Splits.Should().Equal(second.Splits);
        }

        [Fact]
        public void GenerateForTarget_DispatchesToDedicatedGenerators()
        {
            telnet_cs.Fuzz.Mutator.LoadExternalCorpus(string.Empty);

            var storm = telnet_cs.Fuzz.Mutator.GenerateStorm(11, 23, 256);
            var repl = telnet_cs.Fuzz.Mutator.GenerateRepl(11, 23, 256);
            var plain = telnet_cs.Fuzz.Mutator.Generate(11, 23, 256);
            var viaTargetStorm = telnet_cs.Fuzz.Mutator.GenerateForTarget("storm", 11, 23, 256);
            var viaTargetRepl = telnet_cs.Fuzz.Mutator.GenerateForTarget("repl", 11, 23, 256);
            var viaTargetParser = telnet_cs.Fuzz.Mutator.GenerateForTarget("parser", 11, 23, 256);

            viaTargetStorm.Bytes.Should().Equal(storm.Bytes);
            viaTargetStorm.Splits.Should().Equal(storm.Splits);
            viaTargetRepl.Bytes.Should().Equal(repl.Bytes);
            viaTargetRepl.Splits.Should().Equal(repl.Splits);
            viaTargetParser.Bytes.Should().Equal(plain.Bytes);
            viaTargetParser.Splits.Should().Equal(plain.Splits);
        }

        [Fact]
        public void GenerateStorm_LongBiasedInputs_ReachGuardVolume()
        {
            telnet_cs.Fuzz.Mutator.LoadExternalCorpus(string.Empty);
            var lengths = Enumerable.Range(0, 100)
                .Select(i => telnet_cs.Fuzz.Mutator.GenerateStorm(11, i, 2048).Bytes.Length);

            lengths.Count(l => l >= 64).Should().BeGreaterThan(25);
            lengths.Should().OnlyContain(l => l >= 1 && l <= 2048);
        }

        [Fact]
        public void GenerateStorm_VerbHeavy()
        {
            telnet_cs.Fuzz.Mutator.LoadExternalCorpus(string.Empty);
            var bytes = Enumerable.Range(0, 20)
                .SelectMany(i => telnet_cs.Fuzz.Mutator.GenerateStorm(11, i, 2048).Bytes)
                .ToArray();

            var verbs = bytes.Count(b => b is 251 or 252 or 253 or 254 or 250);
            ((double)verbs / bytes.Length).Should().BeGreaterThan(0.05);
        }

        [Fact]
        public void GenerateRepl_MostlyLineOriented()
        {
            telnet_cs.Fuzz.Mutator.LoadExternalCorpus(string.Empty);
            var inputs = Enumerable.Range(0, 50)
                .Select(i => telnet_cs.Fuzz.Mutator.GenerateRepl(11, i, 512).Bytes)
                .ToArray();

            // Command lines dominate, but negotiation bytes are expected: the
            // generator deliberately mixes an IAC prefix and verb bursts into
            // the line stream (the session negotiates under the prompt loop).
            inputs.Count(b => b.Contains((byte)'\n') || b.Contains((byte)'\r'))
                .Should().BeGreaterThan(25);
        }
    }
}
