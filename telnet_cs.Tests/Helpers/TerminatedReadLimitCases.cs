// Shared 64 KiB terminated-read limit cases (D2 pins, both roles): an
// overlong line throws InvalidOperationException naming the limit while the
// endpoint survives, 0 disables, and negatives throw ArgumentOutOfRange.
// Parameterized by lambdas because the client (ApplyOptions + instance
// override) and the server session (ctor options + session override) set
// the limit through different members.
namespace telnet_cs.Tests
{
    using System;
    using System.Threading.Tasks;
    using FluentAssertions;
    using Xunit;

    internal static class TerminatedReadLimitCases
    {
        public static async Task OverlongLine_ThrowsNamingLimitAndSurvives(Func<Task<string>> read, Func<bool> isConnected)
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(read);
            ex.Message.Should().Contain("10-character");
            isConnected().Should().BeTrue();
        }

        public static async Task ZeroLimit_Disables(Func<Task<string>> read, string expected)
        {
            (await read()).Should().Be(expected);
        }

        public static void NegativeLimit_ThrowsArgumentOutOfRange(Action set)
        {
            Assert.Throws<ArgumentOutOfRangeException>(set);
        }
    }
}
