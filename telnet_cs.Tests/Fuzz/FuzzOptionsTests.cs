namespace telnet_cs.Tests
{
    using FluentAssertions;
    using Xunit;

    /// <summary>
    /// Pins the Track 4 CLI surface: new flags parse into <see cref="telnet_cs.Fuzz.FuzzOptions"/>,
    /// and out-of-range values fail with an actionable message.
    /// </summary>
    public class FuzzOptionsTests
    {
        [Fact]
        public void TryParse_NewFlags_PopulatesOptions()
        {
            var ok = telnet_cs.Fuzz.FuzzOptions.TryParse(
                ["--input", "crash.bin", "--jobs", "4", "--seconds", "30",
                 "--no-minimize", "--faults", "--list-modes"],
                out var options, out var error);

            ok.Should().BeTrue(error);
            options.Should().NotBeNull();
            options!.InputFile.Should().Be("crash.bin");
            options.Jobs.Should().Be(4);
            options.Seconds.Should().Be(30);
            options.NoMinimize.Should().BeTrue();
            options.Faults.Should().BeTrue();
            options.ListModes.Should().BeTrue();
        }

        [Fact]
        public void TryParse_Defaults_KeepOldBehavior()
        {
            var ok = telnet_cs.Fuzz.FuzzOptions.TryParse([], out var options, out var error);

            ok.Should().BeTrue(error);
            options.Should().NotBeNull();
            options!.InputFile.Should().BeEmpty();
            options.Jobs.Should().Be(1);
            options.Seconds.Should().Be(0);
            options.NoMinimize.Should().BeFalse();
            options.Faults.Should().BeFalse();
            options.ListModes.Should().BeFalse();
        }

        [Theory]
        [InlineData("--jobs", "0")]
        [InlineData("--jobs", "65")]
        [InlineData("--jobs", "many")]
        [InlineData("--seconds", "-1")]
        [InlineData("--seconds", "86401")]
        [InlineData("--seconds", "soon")]
        public void TryParse_OutOfRangeValues_Fails(string flag, string value)
        {
            var ok = telnet_cs.Fuzz.FuzzOptions.TryParse([flag, value], out var options, out var error);

            ok.Should().BeFalse();
            options.Should().BeNull();
            error.Should().Contain(flag);
        }

        [Fact]
        public void TryParse_InputWithoutValue_Fails()
        {
            var ok = telnet_cs.Fuzz.FuzzOptions.TryParse(["--input"], out var options, out var error);

            ok.Should().BeFalse();
            options.Should().BeNull();
            error.Should().NotBeEmpty();
        }
    }
}
