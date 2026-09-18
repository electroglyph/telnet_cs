namespace telnet_cs.IO;

using telnet_cs.Protocol;
/// <summary>
/// LINEMODE subnegotiation replies: MODE, FORWARD-MASK and SLC triplet handling. Split from <see cref="ByteStreamHandler"/>; wire behavior is unchanged.
/// </summary>
public partial class ByteStreamHandler
{

    /// <summary>
    /// Answer an RFC 1184 LINEMODE subnegotiation. Dispatches on the
    /// LINEMODE subcommand byte: MODE mask confirmation, FORWARDMASK
    /// refusal, or SLC table update.
    /// </summary>
    /// <param name="payload">The full received subnegotiation payload, subcommand first.</param>
    private Task ReplyLinemodeAsync(List<byte> payload)
    {
        if (ShouldSuppressStormSbReply((int)Options.LineMode))
        {
            WriteLog("storm-guard: suppressed SB reply for LINEMODE (negotiation storm).");
            return Task.CompletedTask;
        }

        switch (payload[0])
        {
            case LinemodeProtocol.Mode:
                return ReplyModeAsync(payload);
            case LinemodeProtocol.SetLocalCharacters:
                return ReplySlcAsync(payload);
            case (byte)Commands.Do:
            case (byte)Commands.Dont:
            case (byte)Commands.Will:
            case (byte)Commands.Wont:
                return ReplyForwardMaskAsync(payload);
            default:
                WriteLog($"Ignoring unknown LINEMODE subcommand: {payload[0]}");
                return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Confirm a MODE mask (RFC 1184 §2.2): client rules by default, server
    /// rules when <see cref="ApplyLinemodeAsServer"/> is set. A server also
    /// publishes its SLC table on the first MODE (reference: SLC goes out
    /// on the first ACKed MODE via <c>_slc_sent</c>).
    /// </summary>
    /// <param name="payload">The MODE payload ([MODE, mask]).</param>
    private async Task ReplyModeAsync(List<byte> payload)
    {
        if (payload.Count != 2)
        {
            WriteLog("Ignoring malformed LINEMODE MODE (want [MODE, mask]).");
            return;
        }

        if (!Negotiation.IsEnabledByUs((int)Options.LineMode) && !Negotiation.IsEnabledByPeer((int)Options.LineMode))
        {
            WriteLog("Ignoring LINEMODE MODE without LINEMODE agreement.");
            return;
        }

        if (ApplyLinemodeAsServer
            && (payload[1] & LinemodeProtocol.ModeAck) != 0
            && Linemode.MarkSlcPublished())
        {
            // The SLC table goes out only on the first ACKed MODE
            // (reference: the ACK branch alone publishes via _slc_sent);
            // a non-ACK MODE still earns its MODE+ACK reply below.
            byte[]? triplets = Linemode.ExportTriplets();
            if (triplets is not null)
            {
                WriteLog($"Sending: {nameof(Options.LineMode)} SLC table.");
                await SendNegotiation((int)Options.LineMode,
                  [LinemodeProtocol.SetLocalCharacters, .. triplets]).ConfigureAwait(false);
            }
        }

        byte? reply = ApplyLinemodeAsServer ? Linemode.ApplyModeAsServer(payload[1]) : Linemode.ApplyMode(payload[1]);
        if (reply is null)
        {
            return;
        }

        WriteLog($"Sending: {nameof(Options.LineMode)} MODE {reply.Value}");
        await SendNegotiation((int)Options.LineMode, [LinemodeProtocol.Mode, reply.Value]).ConfigureAwait(false);
    }

    /// <summary>
    /// Handle a FORWARDMASK exchange (RFC 1184 §2.3). A well-formed DO with
    /// a 1–32 byte mask is stored silently and marks the local sub-state;
    /// WILL/WONT mark the remote sub-state; DONT clears the local one. An
    /// empty DO and a DONT with payload bytes are warned on and ignored.
    /// No reply is emitted in any case.
    /// </summary>
    /// <param name="payload">The FORWARDMASK payload ([verb, FORWARDMASK, mask…]).</param>
    private Task ReplyForwardMaskAsync(List<byte> payload)
    {
        if (payload.Count < 2 || payload[1] != LinemodeProtocol.ForwardMask)
        {
            WriteLog("Ignoring malformed LINEMODE FORWARDMASK.");
            return Task.CompletedTask;
        }

        if (IsServerRole && (payload[0] == (byte)Commands.Do || payload[0] == (byte)Commands.Dont))
        {
            WriteLog("Ignoring LINEMODE FORWARDMASK proposal on server role.");
            return Task.CompletedTask;
        }

        if (!IsServerRole && (payload[0] == (byte)Commands.Will || payload[0] == (byte)Commands.Wont))
        {
            WriteLog("Ignoring LINEMODE FORWARDMASK answer on client role.");
            return Task.CompletedTask;
        }

        if (payload[0] == (byte)Commands.Will || payload[0] == (byte)Commands.Wont)
        {
            Linemode.ApplyForwardMaskAnswer(payload[0] == (byte)Commands.Will);
            return Task.CompletedTask;
        }

        if (payload[0] == (byte)Commands.Dont)
        {
            if (payload.Count > 2)
            {
                WriteLog("Ignoring LINEMODE FORWARDMASK DONT with payload bytes.");
                return Task.CompletedTask;
            }

            Linemode.ApplyForwardMaskRefusal();
            return Task.CompletedTask;
        }

        if (payload[0] != (byte)Commands.Do)
        {
            return Task.CompletedTask;
        }

        if (payload.Count < 3)
        {
            WriteLog("Ignoring empty LINEMODE FORWARDMASK DO.");
            return Task.CompletedTask;
        }

        byte[] mask = [.. payload.Skip(2)];
        if (!Linemode.ApplyForwardMaskOffer(mask))
        {
            WriteLog($"Ignoring LINEMODE FORWARDMASK with invalid length: {mask.Length}.");
            return Task.CompletedTask;
        }

        WriteLog("Storing LINEMODE FORWARDMASK.");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Apply an inbound SLC triplet list (RFC 1184 §2.4/§5.5) and reply
    /// with the resulting ACKs/disagreements, if any. A payload whose
    /// triplet tail is not a multiple of 3 is logged and ignored as a
    /// whole, including any valid triplets before the bad tail (telnetlib3
    /// <c>_handle_sb_linemode_slc</c> raises <c>ValueError</c>, but its
    /// feed loop contains per-byte <c>ValueError</c> into a debug log, so
    /// end-to-end the frame is a no-op there too). A server additionally
    /// requests a forwardmask after every SLC block
    /// (reference <c>request_forwardmask</c>) once LINEMODE is agreed on
    /// either side; without agreement the SLC reply still goes out but
    /// the forwardmask is skipped (reference suppresses it without
    /// receipt of WILL LINEMODE).
    /// </summary>
    /// <param name="payload">The SLC payload ([SLC, func, mod, value, …]).</param>
    private async Task ReplySlcAsync(List<byte> payload)
    {
        if ((payload.Count - 1) % 3 != 0)
        {
            WriteLog($"Ignoring LINEMODE SLC with misaligned triplet tail: {payload.Count - 1}.");
            return;
        }

        List<byte>? replies = null;
        foreach (var (function, modifier, value) in LinemodeProtocol.EnumerateSlcTriplets(payload))
        {
            (byte Modifier, byte Value)? reply = ApplyLinemodeAsServer
              ? Linemode.ApplySlcAsServer(function, modifier, value)
              : Linemode.ApplySlc(function, modifier, value);
            if (reply is not null)
            {
                replies ??= [];
                LinemodeProtocol.AppendSlcTriplet(replies, function, reply.Value.Modifier, reply.Value.Value);
            }
        }

        if (replies is not null)
        {
            WriteLog($"Sending: {nameof(Options.LineMode)} SLC reply.");
            await SendNegotiation((int)Options.LineMode, [LinemodeProtocol.SetLocalCharacters, .. replies]).ConfigureAwait(false);
        }

        if (ApplyLinemodeAsServer
            && (Negotiation.IsEnabledByUs((int)Options.LineMode) || Negotiation.IsEnabledByPeer((int)Options.LineMode)))
        {
            byte[] mask = LinemodeProtocol.BuildForwardMask(Negotiation.IsEnabledByUs((int)Options.TransmitBinary));
            WriteLog($"Sending: {nameof(Options.LineMode)} DO FORWARDMASK.");
            await SendNegotiation((int)Options.LineMode,
              [(byte)Commands.Do, LinemodeProtocol.ForwardMask, .. mask]).ConfigureAwait(false);
        }
        else if (ApplyLinemodeAsServer)
        {
            WriteLog("Skipping LINEMODE DO FORWARDMASK without LINEMODE agreement.");
        }
    }
}
