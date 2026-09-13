namespace telnet_cs.Protocol
{
    /// <summary>
    /// RFC 859 STATUS (option 5) snapshot construction: renders the RFC 1143
    /// negotiation state as a WILL/WONT/DO/DONT item list.
    /// </summary>
    internal static class StatusProtocol
    {
        internal const byte Is = 0;
        internal const byte Send = 1;

        // Option 255 (Extended-Options-List) is never rendered: a raw 255 byte
        // inside STATUS IS has no defined escaping (RFC 859 only doubles SE),
        // and we never agree to option 255 anyway.
        private const int LastReportableOption = 254;


        /// <summary>
        /// Builds the STATUS SEND probe frame (<c>IAC SB STATUS SEND IAC SE</c>).
        /// </summary>
        internal static byte[] FrameStatusSend() =>
          EnvironmentProtocol.FrameSubnegotiation((int)Options.Status, [Send]);

        /// <summary>
        /// Builds the STATUS IS item list from the negotiation state, RFC 859
        /// style: one verb plus option byte per non-default side (our side as
        /// WILL/WONT, the peer side as DO/DONT). Sides at their default
        /// (<c>No</c>) are omitted, so one reply describes every option.
        /// Outstanding states render by intent: <c>WantYes</c> as WILL/DO,
        /// <c>WantNo</c> as WONT/DONT. STATUS itself is never listed (the
        /// reference skips it in both halves).
        /// </summary>
        /// <param name="negotiation">The persistent negotiation state.</param>
        internal static byte[] BuildIsPayload(NegotiationState negotiation)
        {
            ArgumentNullException.ThrowIfNull(negotiation);
            var items = new List<byte>();
            for (var option = 0; option <= LastReportableOption; option++)
            {
                if (option == (int)Options.Status)
                {
                    continue;
                }
                var (us, him) = negotiation.GetStates(option);
                if (us != NegotiationState.SideState.No)
                {
                    items.Add(us is NegotiationState.SideState.Yes or NegotiationState.SideState.WantYes
                      ? (byte)Commands.Will
                      : (byte)Commands.Wont);
                    items.Add((byte)option);
                }

                if (him != NegotiationState.SideState.No)
                {
                    items.Add(him is NegotiationState.SideState.Yes or NegotiationState.SideState.WantYes
                      ? (byte)Commands.Do
                      : (byte)Commands.Dont);
                    items.Add((byte)option);
                }
            }

            return [.. items];
        }

        /// <summary>
        /// Frames STATUS IS items as <c>IAC SB STATUS IS</c>, the items, then
        /// <c>IAC SE</c>. RFC 859 §5 specifies a bare <c>SE</c> terminator
        /// (with <c>SE SE</c> doubling), but its own worked example ends the
        /// frame with <c>IAC SE</c>, and every known implementation parses
        /// subnegotiations strictly up to <c>IAC SE</c> — a bare <c>SE</c> is
        /// treated as payload, so the peer swallows whatever follows the frame
        /// into its SB buffer. Interoperability therefore requires the
        /// <c>IAC SE</c> form on send. Inbound parsing still accepts both forms.
        /// Item bytes never reach 255 (see <c>LastReportableOption</c>), so no
        /// IAC doubling occurs; a raw 240 data byte needs no escaping under
        /// <c>IAC SE</c> framing (only <c>IAC</c> itself is special).
        /// </summary>
        /// <param name="items">The item bytes from <see cref="BuildIsPayload"/>.</param>
        internal static byte[] FrameStatusIs(byte[] items)
        {
            ArgumentNullException.ThrowIfNull(items);
            var payload = new byte[items.Length + 1];
            payload[0] = Is;
            items.CopyTo(payload, 1);
            return EnvironmentProtocol.FrameSubnegotiation((int)Options.Status, payload);
        }
    }
}
