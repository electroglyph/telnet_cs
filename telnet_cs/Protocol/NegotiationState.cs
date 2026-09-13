namespace telnet_cs.Protocol
{
    using System;
    using System.Threading;
    using telnet_cs.Client;
    using telnet_cs.IO;

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
                var reply = ReceivedPositiveLocked(option, agree, him, himQueued, refusedByPeer, Commands.Do, Commands.Dont);
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
                var reply = ReceivedNegativeLocked(option, him, himQueued, refusedByPeer, Commands.Do, Commands.Dont);
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
                var reply = ReceivedPositiveLocked(option, agree, us, usQueued, refusedByUs, Commands.Will, Commands.Wont);
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
                var reply = ReceivedNegativeLocked(option, us, usQueued, refusedByUs, Commands.Will, Commands.Wont);
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
                var reply = InitiateEnableLocked(option, him, himQueued, Commands.Do);
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
                // Him-side (DONT): the reference iac() never gates DONT on an
                // outstanding DO — it goes out immediately.
                return InitiateDisableLocked(option, him, himQueued, Commands.Dont, gateOnOutstanding: false);
            }
        }

        /// <summary>
        /// Pings the peer with <c>IAC DO TIMING-MARK</c> (RFC 860). Unlike
        /// <see cref="RequestEnable"/>, an already-agreed mark still goes out:
        /// each ping is new stimulus and the peer answers every <c>DO TM</c>
        /// with <c>WILL TM</c>, so suppressing re-pings would break the
        /// round-trip measurement. Only a ping with a reply still in flight
        /// sends nothing. An explicit ping also clears a remembered refusal.
        /// </summary>
        /// <returns><see cref="Commands.Do"/> when bytes must be sent, else
        /// <c>null</c>.</returns>
        public Commands? RequestTimingMark()
        {
            const int timingMark = (int)Options.TimingMark;
            lock (sync)
            {
                if (him[timingMark] == SideState.WantYes)
                {
                    return null;
                }

                him[timingMark] = SideState.WantYes;
                himQueued[timingMark] = false;
                refusedByPeer[timingMark] = false;
                return Commands.Do;
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
                var reply = InitiateEnableLocked(option, us, usQueued, Commands.Will);
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
                var reply = InitiateDisableLocked(option, us, usQueued, Commands.Wont, gateOnOutstanding: true);
                if (us[option] != SideState.No)
                {
                    refusedByUs[option] = false;
                }

                return reply;
            }
        }

        /// <summary>
        /// Shared RFC 1143 "upon receipt of WILL/DO" table over one side:
        /// <c>him</c> with DO/DONT verbs answers WILL, <c>us</c> with WILL/WONT
        /// verbs answers DO. A refusal is remembered: repeating the same
        /// positive request still answers nothing (reference: the reply goes
        /// out once; later repeats find the option already refused).
        /// </summary>
        private Commands? ReceivedPositiveLocked(
          int option, bool agree, SideState[] side, bool[] queued, bool[] refused, Commands agreeVerb, Commands refuseVerb)
        {
            switch (side[option])
            {
                case SideState.No:
                    if (agree)
                    {
                        side[option] = SideState.Yes;
                        return agreeVerb;
                    }

                    if (refused[option])
                    {
                        return null;
                    }

                    refused[option] = true;
                    return refuseVerb;
                case SideState.Yes:
                    return null;
                case SideState.WantNo when queued[option]:
                    side[option] = SideState.Yes;
                    queued[option] = false;
                    return null;
                case SideState.WantNo:
                    side[option] = SideState.No;
                    return null;
                case SideState.WantYes when queued[option]:
                    side[option] = SideState.WantNo;
                    queued[option] = false;
                    return refuseVerb;
                default:
                    side[option] = SideState.Yes;
                    return null;
            }
        }

        /// <summary>
        /// Shared "upon receipt of WONT/DONT" table over one side:
        /// <c>him</c> answers WONT, <c>us</c> answers DONT. Negative replies
        /// are never answered on the wire (reference: no reply bytes for
        /// DONT/WONT, avoiding refusal loops); only the state moves — except
        /// a queued opposite request, which drains now that the line is free
        /// (the drain is queued new stimulus going out, not a reply).
        /// </summary>
        private Commands? ReceivedNegativeLocked(
          int option, SideState[] side, bool[] queued, bool[] refused, Commands agreeVerb, Commands disagreeVerb)
        {
            switch (side[option])
            {
                case SideState.No:
                    return null;
                case SideState.Yes:
                    side[option] = SideState.No;
                    return null;
                case SideState.WantNo when queued[option]:
                    // Disable completes with an opposite queued: drain it as
                    // a fresh enable (outstanding, so not enabled yet).
                    side[option] = SideState.WantYes;
                    queued[option] = false;
                    return agreeVerb;
                case SideState.WantNo:
                    side[option] = SideState.No;
                    return null;
                case SideState.WantYes when queued[option]:
                    // Refused into the queued state: already where the queued
                    // disable wanted us, so drop the queue without further traffic.
                    side[option] = SideState.No;
                    queued[option] = false;
                    refused[option] = true;
                    return null;
                default:
                    side[option] = SideState.No;
                    refused[option] = true;
                    return null;
            }
        }

        /// <summary>
        /// Shared RFC 1143 "if we decide to ask him to enable" table over one
        /// side: <c>him</c> with DO sends the request, <c>us</c> with WILL sends
        /// the offer.
        /// </summary>
        private Commands? InitiateEnableLocked(int option, SideState[] side, bool[] queued, Commands enableVerb)
        {
            switch (side[option])
            {
                case SideState.No:
                    side[option] = SideState.WantYes;
                    return enableVerb;
                case SideState.WantNo when !queued[option]:
                    queued[option] = true;
                    return null;
                case SideState.WantYes when queued[option]:
                    queued[option] = false;
                    return null;
                default:
                    return null;
            }
        }

        /// <summary>
        /// Shared disable table over one side: the mirror of
        /// <see cref="InitiateEnableLocked"/>. Him-side disables (DONT) are
        /// never gated on an outstanding enable (reference iac(): DONT goes
        /// out immediately and the enable is dropped); us-side disables
        /// (WONT) keep the legacy single-entry opposite queue (no reference
        /// pin either way, and queue tests pin the gated shape).
        /// </summary>
        private Commands? InitiateDisableLocked(int option, SideState[] side, bool[] queued, Commands disableVerb, bool gateOnOutstanding)
        {
            switch (side[option])
            {
                case SideState.Yes:
                    side[option] = SideState.WantNo;
                    return disableVerb;
                case SideState.WantYes when gateOnOutstanding:
                    // Queued (first) or kept (redundant) behind the
                    // outstanding enable; the receipt path drains it.
                    queued[option] = true;
                    return null;
                case SideState.WantYes:
                    queued[option] = false;
                    side[option] = SideState.No;
                    if (gateOnOutstanding)
                    {
                        return null;
                    }

                    // Immediate disable out of an outstanding enable: the
                    // disable is now the outstanding request.
                    side[option] = SideState.WantNo;
                    return disableVerb;
                case SideState.WantNo when queued[option]:
                    queued[option] = false;
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
