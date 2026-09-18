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
            // Negative replies are never answered on the wire (reference:
            // handle_wont is state-only): the WONT only moves him to No.
            var state = new NegotiationState();
            state.ReceivedWill(3, agree: true).Should().Be(Commands.Do);
            state.ReceivedWont(3).Should().BeNull();
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
            // Negative replies are never answered on the wire (reference:
            // handle_dont is state-only): the DONT only moves us to No.
            var state = new NegotiationState();
            state.ReceivedDo(3, agree: true).Should().Be(Commands.Will);
            state.ReceivedDont(3).Should().BeNull();
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
            // Him-side disables go out immediately even with a DO outstanding
            // (reference iac() never gates DONT): no queue is left behind, so
            // the later WILL lands on WantNo and settles silently to No.
            var state = new NegotiationState();
            state.RequestEnable(3).Should().Be(Commands.Do);
            state.RequestDisable(3).Should().Be(Commands.Dont);
            state.ReceivedWill(3, agree: true).Should().BeNull();
            state.IsEnabledByPeer(3).Should().BeFalse();
            state.ReceivedWont(3).Should().BeNull();
        }

        [Fact]
        public void QueuedDisable_DroppedWhenRefusedInto()
        {
            // The disable goes out at once (no queue to drop); the WONT then
            // completes the outstanding disable to No. A WONT answering our
            // own DONT is completion, not a refusal, so nothing is remembered.
            var state = new NegotiationState();
            state.RequestEnable(3).Should().Be(Commands.Do);
            state.RequestDisable(3).Should().Be(Commands.Dont);
            state.ReceivedWont(3).Should().BeNull();
            state.IsEnabledByPeer(3).Should().BeFalse();
            state.WasRefusedByPeer(3).Should().BeFalse();
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
        public void ToggleTwice_EnableClearsQueuedDisable()
        {
            var state = new NegotiationState();
            state.RequestEnable(3).Should().Be(Commands.Do);
            // The disable goes out immediately (WantNo outstanding); the
            // re-enable then queues behind it, and the WILL drains that queue.
            state.RequestDisable(3).Should().Be(Commands.Dont);
            state.RequestEnable(3).Should().BeNull();
            // Proved queued: the WILL drains into the queued enable and the
            // option ends enabled instead of settling to No.
            state.ReceivedWill(3, agree: true).Should().BeNull();
            state.IsEnabledByPeer(3).Should().BeTrue();
        }

        [Fact]
        public void ToggleTwice_DisableClearsQueuedEnable()
        {
            var state = new NegotiationState();
            state.ReceivedDo(3, agree: true).Should().Be(Commands.Will);
            state.OfferDisable(3).Should().Be(Commands.Wont);
            state.OfferEnable(3).Should().BeNull();
            state.OfferDisable(3).Should().BeNull();
            state.ReceivedDo(3, agree: true).Should().BeNull();
            state.IsEnabledByUs(3).Should().BeFalse();
        }

        [Fact]
        public void ReceivedWill_WhileUsOutstanding_TouchesHimOnly()
        {
            var state = new NegotiationState();
            state.OfferEnable(5).Should().Be(Commands.Will);
            state.ReceivedWill(5, agree: true).Should().Be(Commands.Do);
            state[5].Should().Be((NegotiationState.SideState.WantYes, NegotiationState.SideState.Yes));
            state.IsEnabledByUs(5).Should().BeFalse();
            state.IsEnabledByPeer(5).Should().BeTrue();
        }

        [Fact]
        public void ReceivedDo_WhileHimOutstanding_TouchesUsOnly()
        {
            var state = new NegotiationState();
            state.RequestEnable(5).Should().Be(Commands.Do);
            state.ReceivedDo(5, agree: true).Should().Be(Commands.Will);
            state[5].Should().Be((NegotiationState.SideState.Yes, NegotiationState.SideState.WantYes));
            state.IsEnabledByUs(5).Should().BeTrue();
            state.IsEnabledByPeer(5).Should().BeFalse();
        }

        [Fact]
        public void SimultaneousOpen_AckOfAck_Silent()
        {
            // Our outstanding WILL and the peer's DO ack each other: enabling
            // with no reply bytes, so nothing oscillates.
            var state = new NegotiationState();
            state.OfferEnable(3).Should().Be(Commands.Will);
            state.ReceivedDo(3, agree: true).Should().BeNull();
            state.IsEnabledByUs(3).Should().BeTrue();
        }

        [Fact]
        public void ReceivedWill_InWantNoEmpty_ClearsToNo()
        {
            var state = new NegotiationState();
            state.ReceivedWill(3, agree: true).Should().Be(Commands.Do);
            state.RequestDisable(3).Should().Be(Commands.Dont);
            // Answered DONT met by WILL: error case, back to No, no reply.
            state.ReceivedWill(3, agree: true).Should().BeNull();
            state.IsEnabledByPeer(3).Should().BeFalse();
        }

        [Fact]
        public void ReceivedWill_InWantNoOpposite_CompletesEnable()
        {
            var state = new NegotiationState();
            state.ReceivedWill(3, agree: true).Should().Be(Commands.Do);
            state.RequestDisable(3).Should().Be(Commands.Dont);
            state.RequestEnable(3).Should().BeNull();
            state.ReceivedWill(3, agree: true).Should().BeNull();
            state.IsEnabledByPeer(3).Should().BeTrue();
        }

        [Fact]
        public void ReceivedWont_InWantNoEmpty_ClearsToNo()
        {
            var state = new NegotiationState();
            state.ReceivedWill(3, agree: true).Should().Be(Commands.Do);
            state.RequestDisable(3).Should().Be(Commands.Dont);
            state.ReceivedWont(3).Should().BeNull();
            state.IsEnabledByPeer(3).Should().BeFalse();
        }

        [Fact]
        public void RequestEnable_WhenEnabled_Silent()
        {
            var state = new NegotiationState();
            state.ReceivedWill(3, agree: true).Should().Be(Commands.Do);
            state.RequestEnable(3).Should().BeNull();
            state.IsEnabledByPeer(3).Should().BeTrue();
        }

        [Fact]
        public void RequestEnable_InWantNoOpposite_KeepsQueue()
        {
            var state = new NegotiationState();
            state.ReceivedWill(3, agree: true).Should().Be(Commands.Do);
            state.RequestDisable(3).Should().Be(Commands.Dont);
            state.RequestEnable(3).Should().BeNull();
            // Redundant while queued: no-op, the queue survives.
            state.RequestEnable(3).Should().BeNull();
            state.ReceivedWont(3).Should().Be(Commands.Do);
            state.IsEnabledByPeer(3).Should().BeFalse();
        }

        [Fact]
        public void RequestDisable_WhenDisabled_Silent()
        {
            var state = new NegotiationState();
            state.RequestDisable(3).Should().BeNull();
            state.IsEnabledByPeer(3).Should().BeFalse();
        }

        [Fact]
        public void RequestDisable_InWantNoEmpty_Silent()
        {
            var state = new NegotiationState();
            state.ReceivedWill(3, agree: true).Should().Be(Commands.Do);
            state.RequestDisable(3).Should().Be(Commands.Dont);
            state.RequestDisable(3).Should().BeNull();
            state.ReceivedWont(3).Should().BeNull();
            state.IsEnabledByPeer(3).Should().BeFalse();
        }

        [Fact]
        public void RequestDisable_InWantYesOpposite_KeepsQueue()
        {
            var state = new NegotiationState();
            state.RequestEnable(3).Should().Be(Commands.Do);
            // Immediate DONT out of the outstanding enable; the disable is now
            // the outstanding request, so the redundant second call is silent.
            state.RequestDisable(3).Should().Be(Commands.Dont);
            state.RequestDisable(3).Should().BeNull();
            // The WILL lands on the outstanding disable and settles to No.
            state.ReceivedWill(3, agree: true).Should().BeNull();
            state.IsEnabledByPeer(3).Should().BeFalse();
        }

        [Fact]
        public void ReceivedDo_InWantNoEmpty_ClearsToNo()
        {
            var state = new NegotiationState();
            state.ReceivedDo(3, agree: true).Should().Be(Commands.Will);
            state.OfferDisable(3).Should().Be(Commands.Wont);
            // Answered WONT met by DO: error case, back to No, no reply.
            state.ReceivedDo(3, agree: true).Should().BeNull();
            state.IsEnabledByUs(3).Should().BeFalse();
        }

        [Fact]
        public void ReceivedDo_InWantNoOpposite_CompletesEnable()
        {
            var state = new NegotiationState();
            state.ReceivedDo(3, agree: true).Should().Be(Commands.Will);
            state.OfferDisable(3).Should().Be(Commands.Wont);
            state.OfferEnable(3).Should().BeNull();
            state.ReceivedDo(3, agree: true).Should().BeNull();
            state.IsEnabledByUs(3).Should().BeTrue();
        }

        [Fact]
        public void ReceivedDo_InWantYesEmpty_CompletesSilently()
        {
            var state = new NegotiationState();
            state.OfferEnable(3).Should().Be(Commands.Will);
            state.ReceivedDo(3, agree: true).Should().BeNull();
            state.IsEnabledByUs(3).Should().BeTrue();
        }

        [Fact]
        public void ReceivedDont_InWantNoEmpty_ClearsToNo()
        {
            var state = new NegotiationState();
            state.ReceivedDo(3, agree: true).Should().Be(Commands.Will);
            state.OfferDisable(3).Should().Be(Commands.Wont);
            state.ReceivedDont(3).Should().BeNull();
            state.IsEnabledByUs(3).Should().BeFalse();
        }

        [Fact]
        public void ReceivedDont_InWantNoOpposite_DrainsQueuedEnable()
        {
            var state = new NegotiationState();
            state.ReceivedDo(3, agree: true).Should().Be(Commands.Will);
            state.OfferDisable(3).Should().Be(Commands.Wont);
            state.OfferEnable(3).Should().BeNull();
            state.ReceivedDont(3).Should().Be(Commands.Will);
            state.IsEnabledByUs(3).Should().BeFalse();
        }

        [Fact]
        public void ReceivedDont_InWantYesOpposite_DropsQueueAsRefused()
        {
            var state = new NegotiationState();
            state.OfferEnable(3).Should().Be(Commands.Will);
            state.OfferDisable(3).Should().BeNull();
            // Refused into where the queued disable wanted us: drop the queue.
            state.ReceivedDont(3).Should().BeNull();
            state.IsEnabledByUs(3).Should().BeFalse();
            state.WasRefusedByUs(3).Should().BeTrue();
        }

        [Fact]
        public void OfferEnable_UncoveredRows_MatchTable()
        {
            // Options are independent, so one state covers all four rows.
            var state = new NegotiationState();
            // Us-YES: redundant enable is silent.
            state.ReceivedDo(10, agree: true).Should().Be(Commands.Will);
            state.OfferEnable(10).Should().BeNull();
            // Us-WANTNO-EMPTY: enable queues behind the outstanding disable.
            state.ReceivedDo(11, agree: true).Should().Be(Commands.Will);
            state.OfferDisable(11).Should().Be(Commands.Wont);
            state.OfferEnable(11).Should().BeNull();
            state.ReceivedDont(11).Should().Be(Commands.Will);
            // Us-WANTNO-OPPOSITE: redundant enable keeps the queue.
            state.ReceivedDo(12, agree: true).Should().Be(Commands.Will);
            state.OfferDisable(12).Should().Be(Commands.Wont);
            state.OfferEnable(12).Should().BeNull();
            state.OfferEnable(12).Should().BeNull();
            state.ReceivedDont(12).Should().Be(Commands.Will);
            // Us-WANTYES-EMPTY: repeat while outstanding is silent.
            state.OfferEnable(13).Should().Be(Commands.Will);
            state.OfferEnable(13).Should().BeNull();
            state.ReceivedDo(13, agree: true).Should().BeNull();
            state.IsEnabledByUs(13).Should().BeTrue();
        }

        [Fact]
        public void OfferDisable_UncoveredRows_MatchTable()
        {
            var state = new NegotiationState();
            // Us-NO: redundant disable is silent.
            state.OfferDisable(20).Should().BeNull();
            // Us-YES: disable sends WONT; repeat while outstanding is silent.
            state.ReceivedDo(21, agree: true).Should().Be(Commands.Will);
            state.OfferDisable(21).Should().Be(Commands.Wont);
            state.OfferDisable(21).Should().BeNull();
            state.ReceivedDont(21).Should().BeNull();
            state.IsEnabledByUs(21).Should().BeFalse();
            // Us-WANTYES-OPPOSITE: redundant disable keeps the queue.
            state.OfferEnable(22).Should().Be(Commands.Will);
            state.OfferDisable(22).Should().BeNull();
            state.OfferDisable(22).Should().BeNull();
            state.ReceivedDo(22, agree: true).Should().Be(Commands.Wont);
            state.IsEnabledByUs(22).Should().BeFalse();
        }

        [Fact]
        public void RequestTimingMark_Fresh_SendsDoThenSuppressesWhileOutstanding()
        {
            var state = new NegotiationState();
            state.RequestTimingMark().Should().Be(Commands.Do);
            state[6].Him.Should().Be(NegotiationState.SideState.WantYes);
            state.RequestTimingMark().Should().BeNull();
        }

        [Fact]
        public void RequestTimingMark_WhenAgreed_ResendsDo()
        {
            // Unlike RequestEnable, an agreed timing mark still re-pings:
            // every DO TM is answered, so suppression would break the
            // round-trip measurement.
            var state = new NegotiationState();
            state.RequestTimingMark().Should().Be(Commands.Do);
            state.ReceivedWill(6, agree: true).Should().BeNull();
            state.IsEnabledByPeer(6).Should().BeTrue();
            state.RequestTimingMark().Should().Be(Commands.Do);
            state[6].Him.Should().Be(NegotiationState.SideState.WantYes);
        }

        [Fact]
        public void RequestTimingMark_AfterRefusal_ClearsRefusalAndSendsDo()
        {
            var state = new NegotiationState();
            state.RequestTimingMark().Should().Be(Commands.Do);
            state.ReceivedWont(6).Should().BeNull();
            state.WasRefusedByPeer(6).Should().BeTrue();
            state.RequestTimingMark().Should().Be(Commands.Do);
            state.WasRefusedByPeer(6).Should().BeFalse();
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
