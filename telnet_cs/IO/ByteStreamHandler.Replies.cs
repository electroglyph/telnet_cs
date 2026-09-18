namespace telnet_cs.IO;

using System.Text;
using telnet_cs.Protocol;
/// <summary>
/// Subnegotiation answers and extension sends: STATUS, LOCATION, LINEFLOW, CHARSET, EOR, GMCP, MSDP, MSSP and ZMP. Split from <see cref="ByteStreamHandler"/>; wire behavior is unchanged.
/// </summary>
public partial class ByteStreamHandler
{

    /// <summary>
    /// Answer an RFC 859 STATUS SEND with an IS snapshot rendered from the
    /// persistent negotiation state (all options, defaults omitted). Called
    /// only when we are the agreed WILL-sender (see <see cref="WeAgree"/> and
    /// the SEND gate in <see cref="ReplySendAsync"/>): unsolicited SENDs earn
    /// WONT instead.
    /// </summary>
    private Task ReplyStatusAsync()
    {
        var items = StatusProtocol.BuildIsPayload(Negotiation);
        WriteLog($"Sending: {nameof(Options.Status)}");
        var frame = StatusProtocol.FrameStatusIs(items);
        return WriteWireAsync(frame, 0, frame.Length, internalCancellation.Token);
    }

    /// <summary>
    /// Consumes an SNDLOC subnegotiation (RFC 779): the payload is the raw
    /// ASCII location with no SEND/IS verbs. Records it as
    /// <see cref="LastLocation"/> and fires <see cref="LocationReceived"/>.
    /// </summary>
    /// <param name="payload">The received payload (location bytes).</param>
    private void ReplySendLocation(List<byte> payload)
    {
        var location = Encoding.Latin1.GetString([.. payload]);
        WriteLog("Received SNDLOC location.");
        LastLocation = location;
        LocationReceived?.Invoke(location);
    }

    /// <summary>
    /// Consumes an LFLOW subnegotiation (RFC 1372): a single mode byte
    /// (0-3). ON/OFF flips <see cref="LineflowEnabled"/>, RESTART_ANY/XON
    /// flips <see cref="LineflowXonAny"/>; the other flag is untouched.
    /// Only the side that agreed to perform LFLOW (local agreement)
    /// adopts the mode: like the reference, a well-formed but unsolicited
    /// body is consumed silently — never answered with WONT — and a
    /// malformed body is logged and ignored.
    /// </summary>
    /// <param name="payload">The received payload (mode byte).</param>
    private Task ReplyLineflowAsync(List<byte> payload)
    {
        if (ShouldSuppressStormSbReply((int)Options.RemoteFlowControl))
        {
            WriteLog("storm-guard: suppressed SB reply for LFLOW (negotiation storm).");
            return Task.CompletedTask;
        }

        if (payload.Count != 1 || !LineflowProtocol.IsDefined(payload[0]))
        {
            WriteLog("Ignoring malformed LFLOW subnegotiation (want one mode byte 0-3).");
            return Task.CompletedTask;
        }

        if (!Negotiation.IsEnabledByUs((int)Options.RemoteFlowControl))
        {
            WriteLog("Ignoring unsolicited LFLOW subnegotiation (LFLOW not agreed).");
            return Task.CompletedTask;
        }

        var mode = payload[0];
        if (mode is LineflowProtocol.Off or LineflowProtocol.On)
        {
            LineflowEnabled = mode == LineflowProtocol.On;
        }
        else
        {
            LineflowXonAny = mode == LineflowProtocol.RestartXon;
        }

        WriteLog($"Received LFLOW mode: {mode}");
        LineflowReceived?.Invoke(mode);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Answers a CHARSET REQUEST (RFC 2066) with ACCEPTED for the selected
    /// offer or REJECTED when nothing matches. A simultaneous REQUEST
    /// (one arriving while our own is outstanding) is REJECTED on a
    /// server role (RFC 2066 §5). ACCEPTED latches
    /// <see cref="ForceBinaryDecoding"/> and switches
    /// <see cref="TextEncoding"/> to the agreed encoding.
    /// </summary>
    /// <param name="payload">The received payload, REQUEST first.</param>
    private Task ReplyCharsetRequestAsync(List<byte> payload)
    {
        if (CharsetRequestPending && IsServerRole)
        {
            WriteLog("Rejecting simultaneous CHARSET request: our own REQUEST is outstanding.");
            var rejected = EnvironmentProtocol.FrameSubnegotiation((int)Options.CharacterSet, [CharsetProtocol.Rejected]);
            return WriteWireAsync(rejected, 0, rejected.Length, internalCancellation.Token);
        }

        var offers = CharsetProtocol.ParseRequest(payload);
        // Reference selection policy (send_charset): every offered name
        // is scanned and an exact canonical match for the local encoding
        // preference wins before any narrowing — the offer list is never
        // intersected with our own outbound CharsetOffers (those only
        // shape REQUESTs we send). With no local preference, or a weak
        // Latin-1 default, the first viable peer offer is accepted.
        var selected = CharsetSelector is not null
          ? CharsetSelector(offers)
          : CharsetProtocol.SelectSupported(offers, TextEncoding?.WebName);
        if (selected is null)
        {
            WriteLog("Rejecting CHARSET request: no supported offer.");
            CharsetRejected?.Invoke();
            var frame = EnvironmentProtocol.FrameSubnegotiation((int)Options.CharacterSet, [CharsetProtocol.Rejected]);
            return WriteWireAsync(frame, 0, frame.Length, internalCancellation.Token);
        }

        WriteLog($"Sending: {nameof(Options.CharacterSet)} ACCEPTED {selected}");
        AdoptCharset(selected);
        CharsetAccepted?.Invoke(selected);
        var accepted = EnvironmentProtocol.FrameSubnegotiation(
          (int)Options.CharacterSet, CharsetProtocol.BuildAccepted(selected));
        return WriteWireAsync(accepted, 0, accepted.Length, internalCancellation.Token);
    }

    /// <summary>
    /// Applies an agreed character set: records <see cref="NegotiatedCharset"/>
    /// (the raw wire spelling), latches <see cref="ForceBinaryDecoding"/> (the
    /// peer presumes BINARY capability), and switches
    /// <see cref="TextEncoding"/> to the agreed encoding, resolved through
    /// its canonical name so a spelling .NET does not know (e.g.
    /// <c>latin-1</c>) still switches when a normalized variant resolves
    /// (keeping the current encoding only when nothing resolves).
    /// </summary>
    /// <param name="charset">The agreed character-set name.</param>
    private void AdoptCharset(string charset)
    {
        NegotiatedCharset = charset;
        ForceBinaryDecoding = true;
        var canonical = CharsetProtocol.CanonicalName(charset);
        if (canonical is null)
        {
            return;
        }

        try
        {
            TextEncoding = Encoding.GetEncoding(canonical);
        }
        catch (ArgumentException)
        {
        }
    }

    /// <summary>
    /// Consumes a CHARSET answer (RFC 2066) to our own REQUEST. ACCEPTED
    /// records <see cref="NegotiatedCharset"/>, latches
    /// <see cref="ForceBinaryDecoding"/>, switches <see cref="TextEncoding"/>,
    /// and fires <see cref="CharsetAccepted"/>; REJECTED leaves the charset
    /// null (TextEncoding stays unset, so bytes keep passing through as
    /// (char)byte; no bytes are dropped)
    /// and fires <see cref="CharsetRejected"/>. An ACCEPTED with an empty
    /// name — or a non-ASCII name, which matches no ASCII offer (RFC 2066
    /// §2) — takes the rejection path (it matches no requested name).
    /// and fires <see cref="CharsetRejected"/>. Inbound table-transfer
    /// verbs (<c>TTABLE-IS/ACK/NAK</c>) are logged and ignored: table
    /// transfer is not implemented, and answering would only invite a
    /// transfer the stack cannot consume; <c>TTABLE-REJECTED</c> only
    /// clears the outstanding request. All are never thrown: the read
    /// loop must survive them.
    /// </summary>
    /// <param name="payload">The received payload, verb first.</param>
    private Task ReplyCharsetAnswerAsync(List<byte> payload)
    {
        if (ShouldSuppressStormSbReply((int)Options.CharacterSet))
        {
            WriteLog("storm-guard: suppressed SB reply for CHARSET (negotiation storm).");
            return Task.CompletedTask;
        }

        if (payload[0] == CharsetProtocol.Accepted)
        {
            var charset = CharsetProtocol.ParseAccepted(payload);
            if (charset.Length == 0)
            {
                // RFC 2066 section 2: ACCEPTED carries a charset identical
                // to one of the requested names, so an empty name matches
                // nothing and takes the rejection path (no binary latch,
                // no encoding switch).
                WriteLog("CHARSET ACCEPTED an empty name; treating as rejected.");
                CharsetRequestPending = false;
                CharsetRejected?.Invoke();
                return Task.CompletedTask;
            }

            WriteLog($"CHARSET accepted: {charset}");
            AdoptCharset(charset);
            CharsetRequestPending = false;
            CharsetAccepted?.Invoke(charset);
            return Task.CompletedTask;
        }

        if (payload[0] == CharsetProtocol.Rejected)
        {
            WriteLog("CHARSET rejected by peer.");
            CharsetRequestPending = false;
            CharsetRejected?.Invoke();
            return Task.CompletedTask;
        }

        if (payload[0] == CharsetProtocol.TTableIs)
        {
            WriteLog("Ignoring CHARSET table transfer (not implemented).");
            return Task.CompletedTask;
        }

        if (payload[0] == CharsetProtocol.TTableRejected)
        {
            WriteLog("CHARSET table transfer rejected; charset unchanged.");
            CharsetRequestPending = false;
            return Task.CompletedTask;
        }

        WriteLog($"Ignoring CHARSET table-transfer verb: {payload[0]}");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Sends <c>IAC EOR</c> (RFC 885). Fails (returns <c>false</c>) unless
    /// EOR is locally enabled (the peer sent <c>DO EOR</c>), mirroring the
    /// reference <c>send_eor</c> guard.
    /// </summary>
    internal async Task<bool> SendEorAsync()
    {
        if (!Negotiation.IsEnabledByUs((int)Options.EndOfRecord))
        {
            WriteLog("Cannot send EOR without receipt of DO EOR.");
            return false;
        }

        byte[] frame = [(byte)Commands.InterpretAsCommand, (byte)Commands.EndOfRecord];
        await WriteWireAsync(frame, 0, frame.Length, internalCancellation.Token).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Sends our location as <c>IAC SB SNDLOC &lt;location&gt; IAC SE</c>
    /// (RFC 779, ASCII, no verbs).
    /// </summary>
    /// <param name="location">The location string.</param>
    internal Task SendLocationPayloadAsync(string location)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(location);
        WriteLog($"Sending: {nameof(Options.SendLocation)}");
        var frame = EnvironmentProtocol.FrameSubnegotiation(
          (int)Options.SendLocation, Encoding.ASCII.GetBytes(location));
        return WriteWireAsync(frame, 0, frame.Length, internalCancellation.Token);
    }

    /// <summary>
    /// Sends the LFLOW mode (RFC 1372) as a server: RESTART_ANY when
    /// <paramref name="restartOnAny"/> is true, else RESTART_XON. Fails
    /// (returns <c>false</c>) unless the peer enabled LFLOW (sent
    /// <c>WILL LFLOW</c>), mirroring the reference server-only guard.
    /// </summary>
    /// <param name="restartOnAny">Whether any character restarts output.</param>
    internal async Task<bool> SendLineflowModeAsync(bool restartOnAny)
    {
        if (!Negotiation.IsEnabledByPeer((int)Options.RemoteFlowControl))
        {
            WriteLog("Cannot send LFLOW without receipt of WILL LFLOW.");
            return false;
        }

        var mode = restartOnAny ? LineflowProtocol.RestartAny : LineflowProtocol.RestartXon;
        WriteLog($"Sending: {nameof(Options.RemoteFlowControl)} mode {mode}");
        var frame = EnvironmentProtocol.FrameSubnegotiation((int)Options.RemoteFlowControl, [mode]);
        await WriteWireAsync(frame, 0, frame.Length, internalCancellation.Token).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Sends a CHARSET REQUEST (RFC 2066) offering
    /// <see cref="CharsetOffers"/>. Fails (returns <c>false</c>) unless
    /// CHARSET is enabled on either side, offers are configured, or an
    /// identical request is already outstanding (single-active rule).
    /// </summary>
    internal async Task<bool> RequestCharsetAsync()
    {
        if (!Negotiation.IsEnabledByPeer((int)Options.CharacterSet) &&
            !Negotiation.IsEnabledByUs((int)Options.CharacterSet))
        {
            WriteLog("Cannot request CHARSET without negotiating CHARSET first.");
            return false;
        }

        if (CharsetRequestPending)
        {
            WriteLog("Cannot request CHARSET: a REQUEST is already outstanding.");
            return false;
        }

        if (CharsetOffers.Count == 0)
        {
            WriteLog("Cannot request CHARSET with no offers configured.");
            return false;
        }

        WriteLog($"Sending: {nameof(Options.CharacterSet)} REQUEST.");
        var frame = EnvironmentProtocol.FrameSubnegotiation(
          (int)Options.CharacterSet, CharsetProtocol.BuildRequest(CharsetOffers));
        await WriteWireAsync(frame, 0, frame.Length, internalCancellation.Token).ConfigureAwait(false);
        CharsetRequestPending = true;
        return true;
    }

    /// <summary>
    /// Sends a MUD subnegotiation body (MSDP, MSSP, MSP, MXP, ZMP,
    /// Aardwolf, ATCP, GMCP) or a COM port control body (RFC 2217
    /// framing level). Fails (returns <c>false</c>) unless the option is
    /// enabled on either side.
    /// </summary>
    /// <param name="option">The MUD or COM-port option.</param>
    /// <param name="payload">The body bytes (without the option byte).</param>
    internal async Task<bool> SendExtensionPayloadAsync(Options option, byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var number = (int)option;
        if (!IsMudOption(number) && number != (int)Options.COMPortControl)
        {
            throw new ArgumentOutOfRangeException(nameof(option), option, "Only MUD options and COM port control can be sent with SendExtensionPayloadAsync.");
        }

        if (!Negotiation.IsEnabledByPeer(number) && !Negotiation.IsEnabledByUs(number))
        {
            WriteLog($"Cannot send {option} without negotiating it first.");
            return false;
        }

        var frame = EnvironmentProtocol.FrameSubnegotiation(number, payload);
        await WriteWireAsync(frame, 0, frame.Length, internalCancellation.Token).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Sends a GMCP message. Fails (returns <c>false</c>) unless GMCP is
    /// enabled on either side; the frame doubles embedded IAC bytes.
    /// </summary>
    /// <param name="package">The dotted package name.</param>
    /// <param name="data">Optional data, encoded as compact JSON.</param>
    internal Task<bool> SendGmcpAsync(string package, object? data = null)
    {
        return SendExtensionPayloadAsync(Options.Gmcp, MudProtocol.GmcpEncodeData(package, data));
    }

    /// <summary>
    /// Sends MSDP variables. Fails (returns <c>false</c>) unless MSDP is
    /// enabled on either side.
    /// </summary>
    /// <param name="variables">Variable names to values.</param>
    internal Task<bool> SendMsdpAsync(IReadOnlyDictionary<string, object?> variables)
    {
        return SendExtensionPayloadAsync(Options.Msdp, MudProtocol.MsdpEncode(variables));
    }

    /// <summary>
    /// Sends MSSP variables. Fails (returns <c>false</c>) unless MSSP is
    /// enabled on either side.
    /// </summary>
    /// <param name="variables">Variable names to single or repeated values.</param>
    internal Task<bool> SendMsspAsync(IReadOnlyDictionary<string, object> variables)
    {
        return SendExtensionPayloadAsync(Options.Mssp, MudProtocol.MsspEncode(variables));
    }

    /// <summary>
    /// Sends a ZMP message. Fails (returns <c>false</c>) unless ZMP is
    /// enabled on either side.
    /// </summary>
    /// <param name="parts">The command followed by its arguments.</param>
    internal Task<bool> SendZmpAsync(params string[] parts)
    {
        return SendExtensionPayloadAsync(Options.Zmp, MudProtocol.ZmpEncode(parts));
    }
}
