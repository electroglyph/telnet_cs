namespace telnet_cs.Tests
{
    using FluentAssertions;
    using Xunit;

    /// <summary>
    /// Pins the <c>--faults</c> arming schedule: every 8th iteration faults,
    /// alternating read and write, and arming is one-shot and opt-in.
    /// </summary>
    [Collection("Serial")]
    public class FuzzFaultsTests
    {
        [Fact]
        public void ArmForIteration_EighthIteration_ArmsReadFault()
        {
            telnet_cs.Fuzz.FuzzFaults.ArmForIteration(true, 7);

            telnet_cs.Fuzz.FuzzFaults.TakeRead().Should().BeTrue();
            telnet_cs.Fuzz.FuzzFaults.TakeRead().Should().BeFalse("arming is one-shot");
            telnet_cs.Fuzz.FuzzFaults.TakeWrite().Should().BeFalse();
        }

        [Fact]
        public void ArmForIteration_SixteenthIteration_ArmsWriteFault()
        {
            telnet_cs.Fuzz.FuzzFaults.ArmForIteration(true, 15);

            telnet_cs.Fuzz.FuzzFaults.TakeWrite().Should().BeTrue();
            telnet_cs.Fuzz.FuzzFaults.TakeRead().Should().BeFalse();
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(6)]
        [InlineData(8)]
        public void ArmForIteration_OffScheduleIterations_ArmsNothing(int iteration)
        {
            telnet_cs.Fuzz.FuzzFaults.ArmForIteration(true, iteration);

            telnet_cs.Fuzz.FuzzFaults.TakeRead().Should().BeFalse();
            telnet_cs.Fuzz.FuzzFaults.TakeWrite().Should().BeFalse();
        }

        [Fact]
        public void ArmForIteration_Disabled_ArmsNothing()
        {
            telnet_cs.Fuzz.FuzzFaults.ArmForIteration(false, 7);

            telnet_cs.Fuzz.FuzzFaults.TakeRead().Should().BeFalse();
            telnet_cs.Fuzz.FuzzFaults.TakeWrite().Should().BeFalse();
        }
    }
}
