namespace telnet_cs.Tests
{
  using FluentAssertions;
  using Xunit;
  using telnet_cs.Protocol;

  public class NegotiationStateTests
  {
    [Fact]
    public void ReceivedWill_Disagree_RepliesDontAndStaysDisabled()
    {
      var state = new NegotiationState();
      state.ReceivedWill(1, agree: false).Should().Be(Commands.Dont);
      state.IsEnabledByPeer(1).Should().BeFalse();
    }

    [Fact]
    public void ReceivedWill_Agree_RepliesDoAndEnables()
    {
      var state = new NegotiationState();
      state.ReceivedWill(3, agree: true).Should().Be(Commands.Do);
      state.IsEnabledByPeer(3).Should().BeTrue();
    }

    [Fact]
    public void ReceivedWill_WhenEnabled_Silent()
    {
      var state = new NegotiationState();
      state.ReceivedWill(3, agree: true).Should().Be(Commands.Do);
      state.ReceivedWill(3, agree: true).Should().BeNull();
      state.IsEnabledByPeer(3).Should().BeTrue();
    }

    [Fact]
    public void ReceivedWont_WhenNo_Silent()
    {
      var state = new NegotiationState();
      state.ReceivedWont(3).Should().BeNull();
      state.IsEnabledByPeer(3).Should().BeFalse();
    }

    [Fact]
    public void ReceivedWont_WhenEnabled_RepliesDontAndDisables()
    {
      var state = new NegotiationState();
      state.ReceivedWill(3, agree: true).Should().Be(Commands.Do);
      state.ReceivedWont(3).Should().Be(Commands.Dont);
      state.IsEnabledByPeer(3).Should().BeFalse();
    }

    [Fact]
    public void ReceivedDo_Agree_RepliesWillAndEnablesUs()
    {
      var state = new NegotiationState();
      state.ReceivedDo(3, agree: true).Should().Be(Commands.Will);
      state.IsEnabledByUs(3).Should().BeTrue();
    }

    [Fact]
    public void ReceivedDo_Disagree_RepliesWont()
    {
      var state = new NegotiationState();
      state.ReceivedDo(1, agree: false).Should().Be(Commands.Wont);
      state.IsEnabledByUs(1).Should().BeFalse();
    }

    [Fact]
    public void ReceivedDo_WhenUsEnabled_Silent()
    {
      var state = new NegotiationState();
      state.ReceivedDo(3, agree: true).Should().Be(Commands.Will);
      state.ReceivedDo(3, agree: true).Should().BeNull();
    }

    [Fact]
    public void ReceivedDont_WhenUsEnabled_RepliesWontAndDisables()
    {
      var state = new NegotiationState();
      state.ReceivedDo(3, agree: true).Should().Be(Commands.Will);
      state.ReceivedDont(3).Should().Be(Commands.Wont);
      state.IsEnabledByUs(3).Should().BeFalse();
    }

    [Fact]
    public void ReceivedDont_WhenUsNo_Silent()
    {
      var state = new NegotiationState();
      state.ReceivedDont(3).Should().BeNull();
    }

    [Fact]
    public void RequestEnable_Fresh_SendsDoThenSuppressesWhileOutstanding()
    {
      var state = new NegotiationState();
      state.RequestEnable(3).Should().Be(Commands.Do);
      state.RequestEnable(3).Should().BeNull();
      state.ReceivedWill(3, agree: true).Should().BeNull();
      state.IsEnabledByPeer(3).Should().BeTrue();
    }

    [Fact]
    public void Refusal_RememberedUntilExplicitStimulus()
    {
      var state = new NegotiationState();
      state.RequestEnable(1).Should().Be(Commands.Do);
      state.ReceivedWont(1).Should().BeNull();
      state.WasRefusedByPeer(1).Should().BeTrue();
      state.IsEnabledByPeer(1).Should().BeFalse();
      // Explicit re-request is new stimulus: goes out, clears the flag.
      state.RequestEnable(1).Should().Be(Commands.Do);
      state.WasRefusedByPeer(1).Should().BeFalse();
    }

    [Fact]
    public void Refusal_ClearedByPeerChangingMind()
    {
      var state = new NegotiationState();
      state.RequestEnable(1).Should().Be(Commands.Do);
      state.ReceivedWont(1).Should().BeNull();
      state.WasRefusedByPeer(1).Should().BeTrue();
      state.ReceivedWill(1, agree: true).Should().Be(Commands.Do);
      state.WasRefusedByPeer(1).Should().BeFalse();
      state.IsEnabledByPeer(1).Should().BeTrue();
    }

    [Fact]
    public void QueuedDisable_DrainsWhenEnableCompletes()
    {
      var state = new NegotiationState();
      state.RequestEnable(3).Should().Be(Commands.Do);
      state.RequestDisable(3).Should().BeNull();
      state.ReceivedWill(3, agree: true).Should().Be(Commands.Dont);
      state.IsEnabledByPeer(3).Should().BeFalse();
      state.ReceivedWont(3).Should().BeNull();
    }

    [Fact]
    public void QueuedDisable_DroppedWhenRefusedInto()
    {
      var state = new NegotiationState();
      state.RequestEnable(3).Should().Be(Commands.Do);
      state.RequestDisable(3).Should().BeNull();
      state.ReceivedWont(3).Should().BeNull();
      state.IsEnabledByPeer(3).Should().BeFalse();
      state.WasRefusedByPeer(3).Should().BeTrue();
    }

    [Fact]
    public void QueuedEnable_DrainsWhenDisableCompletes()
    {
      var state = new NegotiationState();
      state.ReceivedWill(3, agree: true).Should().Be(Commands.Do);
      state.RequestDisable(3).Should().Be(Commands.Dont);
      state.RequestEnable(3).Should().BeNull();
      // Disable completes (WONT): the queued enable drains as a fresh DO.
      state.ReceivedWont(3).Should().Be(Commands.Do);
      state.IsEnabledByPeer(3).Should().BeFalse();
      state.ReceivedWill(3, agree: true).Should().BeNull();
      state.IsEnabledByPeer(3).Should().BeTrue();
    }

    [Fact]
    public void UsSide_QueueMirrorsHimSide()
    {
      var state = new NegotiationState();
      state.OfferEnable(3).Should().Be(Commands.Will);
      state.OfferDisable(3).Should().BeNull();
      state.ReceivedDo(3, agree: true).Should().Be(Commands.Wont);
      state.IsEnabledByUs(3).Should().BeFalse();
    }

    [Fact]
    public void UsSide_RefusalRemembered()
    {
      var state = new NegotiationState();
      state.OfferEnable(3).Should().Be(Commands.Will);
      state.ReceivedDont(3).Should().BeNull();
      state.WasRefusedByUs(3).Should().BeTrue();
      state.OfferEnable(3).Should().Be(Commands.Will);
      state.WasRefusedByUs(3).Should().BeFalse();
    }

    [Fact]
    public void InvalidOption_ThrowsOutOfRange()
    {
      var state = new NegotiationState();
      foreach (var bad in new[] { -1, 256 })
      {
        Assert.Throws<ArgumentOutOfRangeException>(() => state.ReceivedWill(bad, true));
        Assert.Throws<ArgumentOutOfRangeException>(() => state.ReceivedWont(bad));
        Assert.Throws<ArgumentOutOfRangeException>(() => state.ReceivedDo(bad, true));
        Assert.Throws<ArgumentOutOfRangeException>(() => state.ReceivedDont(bad));
        Assert.Throws<ArgumentOutOfRangeException>(() => state.RequestEnable(bad));
        Assert.Throws<ArgumentOutOfRangeException>(() => state.RequestDisable(bad));
        Assert.Throws<ArgumentOutOfRangeException>(() => state.OfferEnable(bad));
        Assert.Throws<ArgumentOutOfRangeException>(() => state.OfferDisable(bad));
        Assert.Throws<ArgumentOutOfRangeException>(() => state.IsEnabledByPeer(bad));
        Assert.Throws<ArgumentOutOfRangeException>(() => state.IsEnabledByUs(bad));
        Assert.Throws<ArgumentOutOfRangeException>(() => state.WasRefusedByPeer(bad));
        Assert.Throws<ArgumentOutOfRangeException>(() => state.WasRefusedByUs(bad));
      }
    }
  }
}
