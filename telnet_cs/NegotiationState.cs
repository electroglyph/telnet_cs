namespace telnet_cs
{
  using System;
  using System.Threading;

  /// <summary>
  /// Persistent RFC 1143 ("Q method") option-negotiation state for one
  /// connection. Tracks, per option byte, what we agreed our side
  /// (<c>us</c>: our WILL/WONT) and their side (<c>him</c>: our DO/DONT) is
  /// doing: <c>NO</c>, <c>WANTNO</c>, <c>WANTYES</c> or <c>YES</c>, plus the
  /// single-entry <c>EMPTY</c>/<c>OPPOSITE</c> queue for requests made while
  /// another is outstanding. An option is enabled if and only if its state
  /// is <c>YES</c>.
  /// <para/>
  /// The <see cref="Client"/> owns one instance for its lifetime and feeds it
  /// to every per-read <see cref="ByteStreamHandler"/>, so replies are never
  /// repeated across reads and refusals are remembered. All members are
  /// thread-safe.
  /// </summary>
  public sealed class NegotiationState
  {
    /// <summary>
    /// The RFC 1143 negotiation state of one side (<c>us</c> or <c>him</c>)
    /// of one option. An option is enabled if and only if its state is
    /// <see cref="Yes"/>.
    /// </summary>
    public enum SideState : byte
    {
      /// <summary>Default state: the option is disabled and none is outstanding.</summary>
      No,
      /// <summary>A disable was sent and is awaiting the peer's reply.</summary>
      WantNo,
      /// <summary>An enable was sent and is awaiting the peer's reply.</summary>
      WantYes,
      /// <summary>The option is enabled.</summary>
      Yes,
    }

    private const int OptionSpace = 256;

    private readonly Lock sync = new();
    private readonly SideState[] us = new SideState[OptionSpace];
    private readonly SideState[] him = new SideState[OptionSpace];
    private readonly bool[] usQueued = new bool[OptionSpace];
    private readonly bool[] himQueued = new bool[OptionSpace];
    private readonly bool[] refusedByPeer = new bool[OptionSpace];
    private readonly bool[] refusedByUs = new bool[OptionSpace];

    /// <summary>
    /// Gets whether the peer currently has <paramref name="option"/> enabled
    /// (we sent DO and it was not refused or disabled since).
    /// </summary>
    /// <param name="option">The option byte (0-255).</param>
    public bool IsEnabledByPeer(int option)
    {
      ValidateOption(option);
      lock (sync)
      {
        return him[option] == SideState.Yes;
      }
    }

    /// <summary>
    /// Gets whether we currently have <paramref name="option"/> enabled
    /// (we sent WILL and it was not refused or disabled since).
    /// </summary>
    /// <param name="option">The option byte (0-255).</param>
    public bool IsEnabledByUs(int option)
    {
      ValidateOption(option);
      lock (sync)
      {
        return us[option] == SideState.Yes;
      }
    }

    /// <summary>
    /// Gets whether the peer refused our last request to enable
    /// <paramref name="option"/> (we sent DO, they answered WONT) and nothing
    /// has changed since. Automatic re-requests must stay silent while this
    /// is set; an explicit caller-initiated request is new stimulus and may
    /// still go out (which clears the flag).
    /// </summary>
    /// <param name="option">The option byte (0-255).</param>
    public bool WasRefusedByPeer(int option)
    {
      ValidateOption(option);
      lock (sync)
      {
        return refusedByPeer[option];
      }
    }

    /// <summary>
    /// Gets whether the peer refused our last offer to enable
    /// <paramref name="option"/> (we sent WILL, they answered DONT) and
    /// nothing has changed since. See <see cref="WasRefusedByPeer"/> for the
    /// re-request rule, mirrored to our side.
    /// </summary>
    /// <param name="option">The option byte (0-255).</param>
    public bool WasRefusedByUs(int option)
    {
      ValidateOption(option);
      lock (sync)
      {
        return refusedByUs[option];
      }
    }

    /// <summary>
    /// Gets the raw RFC 1143 states of both sides of
    /// <paramref name="option"/>, for advisory snapshots such as the
    /// RFC 859 STATUS reply. The single-entry queue is not reported:
    /// a side with a queued opposite request still shows its outstanding
    /// state.
    /// </summary>
    /// <param name="option">The option byte (0-255).</param>
    /// <returns>The <c>us</c> (our WILL/WONT) and <c>him</c> (our DO/DONT)
    /// states.</returns>
    public (SideState Us, SideState Him) GetStates(int option)
    {
      ValidateOption(option);
      lock (sync)
      {
        return (us[option], him[option]);
      }
    }

    /// <summary>
    /// Records a received <c>IAC WILL</c> and decides the reply.
    /// RFC 1143 "upon receipt of WILL" table over <c>him</c>.
    /// </summary>
    /// <param name="option">The option byte (0-255).</param>
    /// <param name="agree">Whether we accept the peer enabling the option.</param>
    /// <returns>The reply verb (<see cref="Commands.Do"/> /
    /// <see cref="Commands.Dont"/>), or <c>null</c> for no reply.</returns>
    public Commands? ReceivedWill(int option, bool agree)
    {
      ValidateOption(option);
      lock (sync)
      {
        var reply = ReceivedWillLocked(option, agree);
        if (him[option] != SideState.No)
        {
          refusedByPeer[option] = false;
        }

        return reply;
      }
    }

    /// <summary>
    /// Records a received <c>IAC WONT</c> and decides the reply.
    /// RFC 1143 "upon receipt of WONT" table over <c>him</c>.
    /// </summary>
    /// <param name="option">The option byte (0-255).</param>
    /// <returns>The reply verb (<see cref="Commands.Dont"/>), or <c>null</c>
    /// for no reply.</returns>
    public Commands? ReceivedWont(int option)
    {
      ValidateOption(option);
      lock (sync)
      {
        var reply = ReceivedWontLocked(option);
        if (him[option] != SideState.No)
        {
          refusedByPeer[option] = false;
        }

        return reply;
      }
    }

    /// <summary>
    /// Records a received <c>IAC DO</c> and decides the reply: the mirror of
    /// <see cref="ReceivedWill"/> over <c>us</c> (DO/WILL, DONT/WONT swapped).
    /// </summary>
    /// <param name="option">The option byte (0-255).</param>
    /// <param name="agree">Whether we accept enabling the option ourselves.</param>
    /// <returns>The reply verb (<see cref="Commands.Will"/> /
    /// <see cref="Commands.Wont"/>), or <c>null</c> for no reply.</returns>
    public Commands? ReceivedDo(int option, bool agree)
    {
      ValidateOption(option);
      lock (sync)
      {
        var reply = ReceivedDoLocked(option, agree);
        if (us[option] != SideState.No)
        {
          refusedByUs[option] = false;
        }

        return reply;
      }
    }

    /// <summary>
    /// Records a received <c>IAC DONT</c> and decides the reply: the mirror of
    /// <see cref="ReceivedWont"/> over <c>us</c>.
    /// </summary>
    /// <param name="option">The option byte (0-255).</param>
    /// <returns>The reply verb (<see cref="Commands.Wont"/>), or <c>null</c>
    /// for no reply.</returns>
    public Commands? ReceivedDont(int option)
    {
      ValidateOption(option);
      lock (sync)
      {
        var reply = ReceivedDontLocked(option);
        if (us[option] != SideState.No)
        {
          refusedByUs[option] = false;
        }

        return reply;
      }
    }

    /// <summary>
    /// Asks the peer to enable <paramref name="option"/> (sends
    /// <c>IAC DO</c>). RFC 1143 "if we decide to ask him to enable" table:
    /// no new request goes out while one is outstanding (it is queued), and
    /// an already-enabled or already-negotiating option sends nothing.
    /// An explicit call is new stimulus: it clears a remembered refusal.
    /// </summary>
    /// <param name="option">The option byte (0-255).</param>
    /// <returns><see cref="Commands.Do"/> when bytes must be sent, else
    /// <c>null</c>.</returns>
    public Commands? RequestEnable(int option)
    {
      ValidateOption(option);
      lock (sync)
      {
        var reply = RequestEnableLocked(option);
        if (him[option] != SideState.No)
        {
          refusedByPeer[option] = false;
        }

        return reply;
      }
    }

    /// <summary>
    /// Asks the peer to disable <paramref name="option"/> (sends
    /// <c>IAC DONT</c>). Mirror of <see cref="RequestEnable"/>.
    /// </summary>
    /// <param name="option">The option byte (0-255).</param>
    /// <returns><see cref="Commands.Dont"/> when bytes must be sent, else
    /// <c>null</c>.</returns>
    public Commands? RequestDisable(int option)
    {
      ValidateOption(option);
      lock (sync)
      {
        var reply = RequestDisableLocked(option);
        if (him[option] != SideState.No)
        {
          refusedByPeer[option] = false;
        }

        return reply;
      }
    }

    /// <summary>
    /// Offers to enable <paramref name="option"/> ourselves (sends
    /// <c>IAC WILL</c>). The <c>us</c>-side mirror of
    /// <see cref="RequestEnable"/>, used for our own offers.
    /// </summary>
    /// <param name="option">The option byte (0-255).</param>
    /// <returns><see cref="Commands.Will"/> when bytes must be sent, else
    /// <c>null</c>.</returns>
    public Commands? OfferEnable(int option)
    {
      ValidateOption(option);
      lock (sync)
      {
        var reply = OfferEnableLocked(option);
        if (us[option] != SideState.No)
        {
          refusedByUs[option] = false;
        }

        return reply;
      }
    }

    /// <summary>
    /// Offers to disable <paramref name="option"/> ourselves (sends
    /// <c>IAC WONT</c>). The <c>us</c>-side mirror of
    /// <see cref="RequestDisable"/>.
    /// </summary>
    /// <param name="option">The option byte (0-255).</param>
    /// <returns><see cref="Commands.Wont"/> when bytes must be sent, else
    /// <c>null</c>.</returns>
    public Commands? OfferDisable(int option)
    {
      ValidateOption(option);
      lock (sync)
      {
        var reply = OfferDisableLocked(option);
        if (us[option] != SideState.No)
        {
          refusedByUs[option] = false;
        }

        return reply;
      }
    }

    private Commands? ReceivedWillLocked(int option, bool agree)
    {
      switch (him[option])
      {
        case SideState.No:
          if (agree)
          {
            him[option] = SideState.Yes;
            return Commands.Do;
          }

          return Commands.Dont;
        case SideState.Yes:
          return null;
        case SideState.WantNo when himQueued[option]:
          him[option] = SideState.Yes;
          himQueued[option] = false;
          return null;
        case SideState.WantNo:
          him[option] = SideState.No;
          return null;
        case SideState.WantYes when himQueued[option]:
          him[option] = SideState.WantNo;
          himQueued[option] = false;
          return Commands.Dont;
        default:
          him[option] = SideState.Yes;
          return null;
      }
    }

    private Commands? ReceivedWontLocked(int option)
    {
      switch (him[option])
      {
        case SideState.No:
          return null;
        case SideState.Yes:
          him[option] = SideState.No;
          return Commands.Dont;
        case SideState.WantNo when himQueued[option]:
          him[option] = SideState.WantYes;
          himQueued[option] = false;
          return Commands.Do;
        case SideState.WantNo:
          him[option] = SideState.No;
          return null;
        case SideState.WantYes when himQueued[option]:
          // Refused into the queued state: already where the queued
          // disable wanted us, so drop the queue without further traffic.
          him[option] = SideState.No;
          himQueued[option] = false;
          refusedByPeer[option] = true;
          return null;
        default:
          him[option] = SideState.No;
          refusedByPeer[option] = true;
          return null;
      }
    }

    private Commands? ReceivedDoLocked(int option, bool agree)
    {
      switch (us[option])
      {
        case SideState.No:
          if (agree)
          {
            us[option] = SideState.Yes;
            return Commands.Will;
          }

          return Commands.Wont;
        case SideState.Yes:
          return null;
        case SideState.WantNo when usQueued[option]:
          us[option] = SideState.Yes;
          usQueued[option] = false;
          return null;
        case SideState.WantNo:
          us[option] = SideState.No;
          return null;
        case SideState.WantYes when usQueued[option]:
          us[option] = SideState.WantNo;
          usQueued[option] = false;
          return Commands.Wont;
        default:
          us[option] = SideState.Yes;
          return null;
      }
    }

    private Commands? ReceivedDontLocked(int option)
    {
      switch (us[option])
      {
        case SideState.No:
          return null;
        case SideState.Yes:
          us[option] = SideState.No;
          return Commands.Wont;
        case SideState.WantNo when usQueued[option]:
          us[option] = SideState.WantYes;
          usQueued[option] = false;
          return Commands.Will;
        case SideState.WantNo:
          us[option] = SideState.No;
          return null;
        case SideState.WantYes when usQueued[option]:
          us[option] = SideState.No;
          usQueued[option] = false;
          refusedByUs[option] = true;
          return null;
        default:
          us[option] = SideState.No;
          refusedByUs[option] = true;
          return null;
      }
    }

    private Commands? RequestEnableLocked(int option)
    {
      switch (him[option])
      {
        case SideState.No:
          him[option] = SideState.WantYes;
          return Commands.Do;
        case SideState.WantNo when !himQueued[option]:
          himQueued[option] = true;
          return null;
        case SideState.WantYes when himQueued[option]:
          himQueued[option] = false;
          return null;
        default:
          return null;
      }
    }

    private Commands? RequestDisableLocked(int option)
    {
      switch (him[option])
      {
        case SideState.Yes:
          him[option] = SideState.WantNo;
          return Commands.Dont;
        case SideState.WantYes when !himQueued[option]:
          himQueued[option] = true;
          return null;
        case SideState.WantNo when himQueued[option]:
          himQueued[option] = false;
          return null;
        default:
          return null;
      }
    }

    private Commands? OfferEnableLocked(int option)
    {
      switch (us[option])
      {
        case SideState.No:
          us[option] = SideState.WantYes;
          return Commands.Will;
        case SideState.WantNo when !usQueued[option]:
          usQueued[option] = true;
          return null;
        case SideState.WantYes when usQueued[option]:
          usQueued[option] = false;
          return null;
        default:
          return null;
      }
    }

    private Commands? OfferDisableLocked(int option)
    {
      switch (us[option])
      {
        case SideState.Yes:
          us[option] = SideState.WantNo;
          return Commands.Wont;
        case SideState.WantYes when !usQueued[option]:
          usQueued[option] = true;
          return null;
        case SideState.WantNo when usQueued[option]:
          usQueued[option] = false;
          return null;
        default:
          return null;
      }
    }

    private static void ValidateOption(int option)
    {
      ArgumentOutOfRangeException.ThrowIfNegative(option);
      ArgumentOutOfRangeException.ThrowIfGreaterThan(option, OptionSpace - 1);
    }
  }
}
