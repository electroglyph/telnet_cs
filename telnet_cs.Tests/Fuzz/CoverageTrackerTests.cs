namespace telnet_cs.Tests
{
    using System;
    using System.IO;
    using FluentAssertions;
    using Xunit;

    /// <summary>
    /// Pins novelty-key semantics: the (outbound, hash) signature plus the
    /// input-length bucket decide novelty, so ragged tails stay distinct from
    /// bulk floods without every length becoming its own class.
    /// </summary>
    public class CoverageTrackerTests : IDisposable
    {
        private readonly string dir = Path.Combine(Path.GetTempPath(), "telnetcs-cov-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }

            GC.SuppressFinalize(this);
        }

        [Fact]
        public void Observe_SameSignatureTwice_SavesOnce()
        {
            var tracker = new telnet_cs.Fuzz.CoverageTracker(dir);
            var first = new byte[16];
            var second = new byte[16];
            second[0] = 1;

            tracker.Observe(first, 5, 9);
            tracker.Observe(second, 5, 9);

            tracker.Saved.Should().Be(1, "content is not part of the novelty key");
        }

        [Fact]
        public void Observe_SameSignatureDifferentLengthBucket_SavesAgain()
        {
            var tracker = new telnet_cs.Fuzz.CoverageTracker(dir);

            tracker.Observe(new byte[15], 5, 9);
            tracker.Observe(new byte[16], 5, 9);

            tracker.Saved.Should().Be(2, "15 and 16 bytes sit in different /16 buckets");
        }

        [Fact]
        public void Observe_DifferentSignature_SavesAgain()
        {
            var tracker = new telnet_cs.Fuzz.CoverageTracker(dir);

            tracker.Observe(new byte[16], 5, 9);
            tracker.Observe(new byte[16], 6, 9);

            tracker.Saved.Should().Be(2);
        }
    }
}
