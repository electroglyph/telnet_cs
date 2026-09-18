namespace telnet_cs.IO;

using telnet_cs.Protocol;
using telnet_cs.Transport;
/// <summary>
/// Outbound negotiation replies: WILL/WONT/DO/DONT answers, environment replies and window-size reporting. Split from <see cref="ByteStreamHandler"/>; wire behavior is unchanged.
/// </summary>
public partial class ByteStreamHandler
{

    /// <summary>
    /// Answer a SEND subnegotiation for an option with the SEND/IS shape:
    /// terminal type, terminal speed, environment, X display, status, or
    /// character set.
    /// </summary>
    /// <param name="inputOption">The option under negotiation.</param>
    /// <param name="payload">The full received subnegotiation payload, SEND first.</param>
    private async Task ReplySendAsync(int inputOption, List<byte> payload)
    {
        if (ShouldSuppressStormSbReply(inputOption))
        {
            WriteLog("storm-guard: suppressed SB reply for " +
                (Enum.GetName(typeof(Options), inputOption) ?? inputOption.ToString()) +
                " (negotiation storm).");
            return;
        }

        switch (inputOption)
        {
            case (int)Options.TerminalType:
                await SendNegotiation(inputOption, TerminalTypeProvider?.Invoke() ?? TerminalType).ConfigureAwait(false);
                break;
            case (int)Options.TerminalSpeed:
                string? speed = TerminalSpeedProtocol.Validate(TerminalSpeed);
                if (speed is null)
                {
                    WriteLog("Skipping TERMINAL-SPEED reply: malformed speed (want \"<tx>,<rx>\" decimal).");
                    break;
                }

                await SendNegotiation(inputOption, speed).ConfigureAwait(false);
                break;
            case (int)Options.OldEnvironment:
            case (int)Options.NewEnvironment:
                await ReplyEnvironmentAsync(inputOption, payload).ConfigureAwait(false);
                break;
            case (int)Options.XDisplay:
                // RFC 1096 §4: IS answers SEND, even when unconfigured, with an
                // empty display string.
                await SendNegotiation(inputOption, XDisplayLocation ?? string.Empty).ConfigureAwait(false);
                break;
            case (int)Options.Status:
                if (!Negotiation.IsEnabledByUs((int)Options.Status))
                {
                    WriteLog("Ignoring STATUS SEND without agreement.");
                    break;
                }

                await ReplyStatusAsync().ConfigureAwait(false);
                break;
            case (int)Options.CharacterSet:
                // RFC 2066: REQUEST shares the SEND byte value (1), so it
                // arrives here; ACCEPTED/REJECTED bypass the gate above.
                await ReplyCharsetRequestAsync(payload).ConfigureAwait(false);
                break;
            default:
                // We don't handle other sub negotiation options yet.
                WriteLog("Request to negotiate: " + Enum.GetName(typeof(Options), inputOption));
                break;
        }
    }

    private Task SendWont(int inputOption)
    {
        var outBuffer = new byte[3];
        outBuffer[0] = (byte)Commands.InterpretAsCommand;
        outBuffer[1] = (byte)Commands.Wont;
        outBuffer[2] = (byte)inputOption;
        return WriteWireAsync(outBuffer, 0, outBuffer.Length, internalCancellation.Token);
    }

    private Task SendDont(int inputOption)
    {
        var outBuffer = new byte[3];
        outBuffer[0] = (byte)Commands.InterpretAsCommand;
        outBuffer[1] = (byte)Commands.Dont;
        outBuffer[2] = (byte)inputOption;
        return WriteWireAsync(outBuffer, 0, outBuffer.Length, internalCancellation.Token);
    }

    /// <summary>
    /// Send the sub negotiation response to the server.
    /// </summary>
    /// <param name="inputOption">The option we are negotiating.</param>
    /// <param name="optionMessage">The setting for <paramref name="inputOption"/>.</param>
    private Task SendNegotiation(int inputOption, string optionMessage)
    {
        WriteLog("Sending: " + Enum.GetName(typeof(Options), inputOption) + " Setting: " + optionMessage);
        return SendNegotiation(inputOption, [EnvironmentProtocol.Is, .. ToNegotiationBytes(optionMessage)]);
    }

    private static byte[] ToNegotiationBytes(string optionMessage)
    {
        // Latin-1 one-to-one mapping (manual truncating loop, kept to pin the
        // historical byte mapping). IAC escaping happens in the byte[]
        // overload below, not here.
        var bytes = new byte[optionMessage.Length];
        for (var i = 0; i < optionMessage.Length; i++)
        {
            bytes[i] = (byte)optionMessage[i];
        }

        return bytes;
    }

    /// <summary>
    /// Send the sub negotiation response to the server, escaping literal IAC
    /// bytes in the payload by doubling them (RFC 854).
    /// </summary>
    /// <param name="inputOption">The option we are negotiating.</param>
    /// <param name="verbFirstPayload">The payload bytes starting with the subnegotiation
    /// verb (<c>IS</c>, <c>INFO</c>, ...), without IAC SB/SE framing.</param>
    private Task SendNegotiation(int inputOption, byte[] verbFirstPayload)
    {
        var frame = EnvironmentProtocol.FrameSubnegotiation(inputOption, verbFirstPayload);
        return WriteWireAsync(frame, 0, frame.Length, internalCancellation.Token);
    }

    /// <summary>
    /// Answer an RFC 1408/1572 ENVIRON SEND with an IS built from the
    /// configured environment values. Named requests are answered
    /// requested-only in order, with a bare type+name (no
    /// <c>VALUE</c>) for undefined variables. A bare <c>VAR</c> or
    /// <c>USERVAR</c> marker volunteers that type's defaults, and an
    /// empty request volunteers both. Old (36) and new (39) forms share
    /// framing and verbs; the answer goes out on whichever option
    /// asked. Volunteered well-known variables include the session
    /// parameters <c>TERM</c>, <c>LANG</c>, <c>COLUMNS</c> and
    /// <c>LINES</c>, matching telnetlib3's auto-sent <c>send_env</c>
    /// set; <c>LANG</c> is <c>C</c> without an explicit
    /// <see cref="TextEncoding"/>, else
    /// <c>en_US.&lt;encoding&gt;</c>. <c>DISPLAY</c> is never
    /// volunteered on this path.
    /// </summary>
    /// <param name="inputOption">The option under negotiation (old or new).</param>
    /// <param name="payload">The full received subnegotiation payload, verb first.</param>
    private Task ReplyEnvironmentAsync(int inputOption, List<byte> payload)
    {
        var (width, height) = NawsProtocol.GetEffectiveSize(WindowWidth, WindowHeight);
        var lang = TextEncoding is null ? "C" : "en_US." + TextEncoding.WebName.Replace("-", string.Empty, StringComparison.Ordinal);
        var colorTerm = System.Environment.GetEnvironmentVariable("COLORTERM") ?? string.Empty;
        // Reference send_env parity: DISPLAY is never volunteered on the
        // SB answer path ("intentionally not available (security)") —
        // not even when EnvironmentDisplay is configured. The configured
        // value still rides spontaneous INFO updates (see
        // MaybeSendEnvironmentInfoAsync), which is the C# superset the
        // INFO tests pin.
        var response = EnvironmentProtocol.BuildResponse(
          EnvironmentProtocol.Is,
          payload.Skip(1),
          EnvironmentUser,
          null,
          EnvironmentUserVars,
          string.IsNullOrEmpty(TerminalType) ? null : TerminalType,
          lang,
          width.ToString(System.Globalization.CultureInfo.InvariantCulture),
          height.ToString(System.Globalization.CultureInfo.InvariantCulture),
          colorTerm);
        WriteLog("Sending: " + Enum.GetName(typeof(Options), inputOption));
        return SendNegotiation(inputOption, response);
    }

    private async Task ReplyToCommand(int inputVerb)
    {
        var inputOption = TryReadByte();
        if (inputOption == -1)
        {
            // RFC 854 framing split: the option byte arrives with the continuation.
            pendingVerb = inputVerb;
            return;
        }

        while (inputOption == IacByte)
        {
            // IAC toggle in option position (reference feed_byte: the IAC
            // pair is framing and the command persists, so the byte after
            // the pair is the real option — e.g. DO IAC IAC 0xFB answers
            // the 0xFB option instead of going silent).
            inputOption = TryReadByte();
            if (inputOption == -1)
            {
                pendingVerb = inputVerb;
                return;
            }
        }

        await ReplyToCommandWithOption(inputVerb, inputOption).ConfigureAwait(false);
    }

    private async Task ReplyToCommandWithOption(int inputVerb, int inputOption)
    {
        WriteLog(Enum.GetName(typeof(Options), inputOption) ?? inputOption.ToString());
        // Every inbound WILL/WONT/DO/DONT counts toward the storm
        // window, even duplicates that already stay silent below.
        StormGuard?.NoteFrame();
        if (inputOption == (int)Options.TimingMark)
        {
            if (inputVerb == (int)Commands.Do)
            {
                await WriteWireAsync([(byte)Commands.InterpretAsCommand, (byte)Commands.Will, (byte)inputOption], 0, 3, internalCancellation.Token).ConfigureAwait(false);
                return;
            }

            // A timing-mark reply only completes a ping we sent: an
            // outstanding DO TM is cleared and the agreement persisted,
            // with no reply bytes either way. Anything else is ignored.
            if (Negotiation[(int)Options.TimingMark].Him == NegotiationState.SideState.WantYes)
            {
                if (inputVerb == (int)Commands.Will)
                {
                    Negotiation.ReceivedWill((int)Options.TimingMark, agree: true);
                    return;
                }

                if (inputVerb == (int)Commands.Wont)
                {
                    Negotiation.ReceivedWont((int)Options.TimingMark);
                    return;
                }
            }

            WriteLog($"Ignoring {(Commands)inputVerb} TIMING-MARK without outstanding DO.");
            return;
        }

        if (inputOption == (int)Options.Logout && inputVerb == (int)Commands.Will && !IsServerRole)
        {
            // A client never offers LOGOUT itself (reference: the client
            // end raises instead of answering); swallow without reply.
            WriteLog("Ignoring WILL LOGOUT on client role.");
            return;
        }

        if (inputOption == (int)Options.Logout && inputVerb == (int)Commands.Do)
        {
            if (!IsServerRole)
            {
                // Only the server end honors DO LOGOUT by closing
                // (reference: the client end raises instead); a client
                // consumes it without reply or close.
                WriteLog("Ignoring DO LOGOUT on client role.");
                return;
            }

            WriteLog("Peer requested LOGOUT; closing without negotiation bytes.");
            LogoutRequested?.Invoke();
            return;
        }

        if (inputOption == (int)Options.Echo && IsServerRole && inputVerb == (int)Commands.Will)
        {
            WriteLog("Ignoring WILL ECHO on server role.");
            return;
        }

        var (usBefore, himBefore) = Negotiation[inputOption];
        Commands? reply = null;
        if (!SilenceNegotiation)
        {
            reply = inputVerb switch
            {
                (int)Commands.Do => Negotiation.ReceivedDo(inputOption, AgreeEcho(inputOption, peerPerforms: false)),
                (int)Commands.Dont => Negotiation.ReceivedDont(inputOption),
                (int)Commands.Will => Negotiation.ReceivedWill(inputOption, AgreeEcho(inputOption, peerPerforms: true)),
                (int)Commands.Wont => Negotiation.ReceivedWont(inputOption),
                _ => null,
            };
        }
        else
        {
            // Master switch: the verb earns no state, no reply, no log.
            return;
        }
        if (IsServerRole && inputVerb == (int)Commands.Do && inputOption == (int)Options.RemoteFlowControl && reply is Commands.Wont)
        {
            // Directional refusal with a latch: WONT goes out, but the
            // peer sends LFLOW modes regardless, so local agreement is
            // recorded too (reply discarded) and later modes are honored.
            Negotiation.ReceivedDo(inputOption, agree: true);
        }
        // An inbound WONT/DONT that flips an MCCP side Yes-to-No ends
        // inflation; stray repeats change nothing, so they stay silent
        // here too. Outbound compression keeps running (the peer only
        // took back its own direction).
        bool mccpAgreementLost = (inputOption == (int)Options.Mccp2 || inputOption == (int)Options.Mccp3) &&
            ((inputVerb == (int)Commands.Wont && himBefore == NegotiationState.SideState.Yes) ||
             (inputVerb == (int)Commands.Dont && usBefore == NegotiationState.SideState.Yes));
        if (reply is null)
        {
            if (mccpAgreementLost)
            {
                TeardownInboundMccp(inputOption);
            }

            WriteLog($"No reply to {inputVerb} {inputOption}: already in that state (RFC 1143).");
            return;
        }

        if (reply is Commands.Do && inputVerb == (int)Commands.Will &&
            SuppressWillAck(inputOption, IsServerRole))
        {
            // The agreement above is recorded in the negotiation state,
            // but no DO goes out: for these options the reply would only
            // invite a subnegotiation the other side must request first,
            // and the follow-up SEND probes below run regardless.
        }
        else if ((reply is Commands.Wont || reply is Commands.Dont) && ShouldSuppressStormRefusal())
        {
            // Storm shed: the refusal state is still recorded above, so
            // only the repeat reply bytes are dropped. Agreements
            // (WILL/DO) are never suppressed.
            WriteLog($"storm-guard: suppressed {reply} {inputOption} (negotiation storm).");
        }
        else
        {
            byte[] outBuffer =
            [
        (byte)Commands.InterpretAsCommand,
        (byte)reply,
        (byte)inputOption,
            ];
            await WriteWireAsync(outBuffer, 0, outBuffer.Length, internalCancellation.Token).ConfigureAwait(false);
        }

        if (mccpAgreementLost)
        {
            TeardownInboundMccp(inputOption);
        }

        if (inputOption == (int)Options.WindowSize && inputVerb == (int)Commands.Do && reply is Commands.Will)
        {  // NAWS is volunteered only when we agree to send it (reference
           // handle_do): answering a peer WILL with DO merely arms the
           // follow-up subnegotiation without sending our size yet.
            await SendWindowSize().ConfigureAwait(false);
        }

        if (inputOption == (int)Options.SendLocation && reply is Commands.Will && SendLocation is not null)
        {
            // RFC 779: the client volunteers its location right after
            // agreeing (no SEND request exists for this option).
            await SendLocationPayloadAsync(SendLocation).ConfigureAwait(false);
        }

        if (inputOption == (int)Options.RemoteFlowControl && reply is Commands.Do && SendLineflowAsServer)
        {
            await SendLineflowModeAsync(SendLineflowRestartAny).ConfigureAwait(false);
        }

        if (inputOption == (int)Options.Status && inputVerb == (int)Commands.Will && reply is Commands.Do)
        {
            // RFC 859 initiation (reference handle_will): a peer
            // announcing WILL STATUS is immediately put to the test with
            // a SEND probe. Only on the state-changing agreement (a
            // repeat WILL earns no reply above, and no re-probe here).
            var probe = StatusProtocol.FrameStatusSend();
            await WriteWireAsync(probe, 0, probe.Length, internalCancellation.Token).ConfigureAwait(false);
        }

        if (inputOption == (int)Options.Status && inputVerb == (int)Commands.Do && reply is Commands.Will)
        {
            // RFC 859 initiation (reference handle_do): volunteer our
            // snapshot the moment we agree to send it, without waiting
            // for a SEND first.
            await ReplyStatusAsync().ConfigureAwait(false);
        }

        if (inputOption == (int)Options.LineMode && inputVerb == (int)Commands.Do
            && reply is Commands.Will && !ApplyLinemodeAsServer)
        {
            // RFC 1184 §2.4 (reference handle_do): the client initiates
            // the SLC exchange immediately after WILL LINEMODE by
            // requesting the peer's table (func 0, DEFAULT).
            await SendNegotiation((int)Options.LineMode,
              [LinemodeProtocol.SetLocalCharacters, 0, LinemodeProtocol.LevelDefault, 0]).ConfigureAwait(false);
        }

        if (inputOption == (int)Options.LineMode && inputVerb == (int)Commands.Will
            && reply is Commands.Do && IsServerRole)
        {
            // RFC 1184 (reference handle_will): the server answers WILL
            // LINEMODE with DO plus its initial MODE proposal (edit mode
            // off, trapsig off, remoting on: REMOTE|LIT_ECHO).
            await SendNegotiation((int)Options.LineMode,
              [LinemodeProtocol.Mode, LinemodeProtocol.DefaultServerMode]).ConfigureAwait(false);
        }

        if (inputOption == (int)Options.COMPortControl && inputVerb == (int)Commands.Will
            && reply is Commands.Do && !IsServerRole)
        {
            // RFC 2217 (reference request_comport_signature): the client
            // asks for the server's signature right after agreeing
            // (sub-command 0 = SIGNATURE, empty payload = request).
            await SendNegotiation((int)Options.COMPortControl, [0]).ConfigureAwait(false);
        }

        if (inputOption == (int)Options.Mccp3 && inputVerb == (int)Commands.Will
            && reply is Commands.Do && !IsServerRole)
        {
            // The client starts MCCP3 with an empty SB; inbound inflation
            // arms only when the peer's SB actually arrives (the
            // agreement-gated SB path below), never on the WILL itself —
            // arming here would inflate the peer's still-plain bytes.
            // Outbound compression starts here too, via the session hook:
            // the marker above went out raw, so every byte after it is
            // the session's compressor output.
            await SendNegotiation((int)Options.Mccp3, []).ConfigureAwait(false);
            Mccp3StartSent?.Invoke();
        }

        if (inputOption == (int)Options.Gmcp && inputVerb == (int)Commands.Will
            && reply is Commands.Do && !IsServerRole && EnableGmcp && !gmcpHelloSent)
        {
            // Reference on_will_gmcp/send_gmcp_hello: Core.Hello plus
            // Core.Supports.Set go out once GMCP is agreed.
            gmcpHelloSent = true;
            await SendNegotiation((int)Options.Gmcp,
              MudProtocol.GmcpEncodeData("Core.Hello", new Dictionary<string, string>
              {
                  ["client"] = "telnet-cs",
                  ["version"] = "1.0",
              })).ConfigureAwait(false);
            await SendNegotiation((int)Options.Gmcp,
              MudProtocol.GmcpEncodeData("Core.Supports.Set", MudProtocol.DefaultGmcpModules)).ConfigureAwait(false);
        }

        if (inputOption == (int)Options.Zmp && inputVerb == (int)Commands.Will
            && reply is Commands.Do && !IsServerRole && EnableZmp && !ZmpIdentSent)
        {
            // Reference on_will_zmp/send_zmp_ident: zmp.ident plus one
            // zmp.support per supported command go out once ZMP is agreed.
            ZmpIdentSent = true;
            await SendNegotiation((int)Options.Zmp,
              MudProtocol.ZmpEncode("zmp.ident", "telnet-cs", "1.0")).ConfigureAwait(false);
            foreach (var command in ZmpSupportedCommands.OrderBy(static c => c, StringComparer.Ordinal))
            {
                await SendNegotiation((int)Options.Zmp,
                  MudProtocol.ZmpEncode("zmp.support", command)).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Whether a WILL agreement is recorded without emitting DO. A server
    /// never solicits TTYPE/TSPEED/XDISPLOC/NEW_ENVIRON/LFLOW/CHARSET/
    /// STATUS with DO: it sends the SB SEND probe (or the CHARSET
    /// reciprocation) once the peer volunteers WILL, and a bare DO would
    /// only restate an agreement the probe already assumes. A client
    /// likewise records WILL STATUS without DO and still answers the
    /// peer's SEND with its snapshot. Every other agreement still emits
    /// its DO/DONT/WILL/WONT reply above.
    /// </summary>
    /// <param name="inputOption">The negotiated option.</param>
    /// <param name="serverRole">Whether this end acts as server.</param>
    private static bool SuppressWillAck(int inputOption, bool serverRole)
    {
        if (serverRole)
        {
            return inputOption is (int)Options.TerminalType
                or (int)Options.TerminalSpeed
                or (int)Options.XDisplay
                or (int)Options.NewEnvironment
                or (int)Options.RemoteFlowControl
                or (int)Options.CharacterSet
                or (int)Options.Status;
        }

        return inputOption == (int)Options.Status;
    }

    private bool WeAgree(int inputOption, bool peerWill)
    {
        if (inputOption == (int)Options.Mccp2 || inputOption == (int)Options.Mccp3)
        {
            // The reference refuses MCCP unless compression is opted in,
            // and always over TLS (CRIME/BREACH).
            return EnableMccp && !IsTlsActive;
        }

        if (inputOption == (int)Options.COMPortControl)
        {
            return EnableComPort;
        }

        if (IsMudOption(inputOption))
        {
            // Role split, mirroring the reference (client-gated decline):
            // GMCP and ZMP are passively agreed by default (each has its
            // own switch); every other MUD option needs EnableMudOptions
            // (the client stack declines it by default, the server
            // agrees).
            return inputOption switch
            {
                (int)Options.Gmcp => EnableGmcp,
                (int)Options.Zmp => EnableZmp,
                _ => EnableMudOptions,
            };
        }

        if (IsServerRole)
        {
            if (!peerWill)
            {
                if (inputOption == (int)Options.TerminalType ||
                    inputOption == (int)Options.WindowSize ||
                    inputOption == (int)Options.TerminalSpeed ||
                    inputOption == (int)Options.RemoteFlowControl ||
                    inputOption == (int)Options.LineMode ||
                    inputOption == (int)Options.XDisplay ||
                    inputOption == (int)Options.NewEnvironment ||
                    inputOption == (int)Options.SendLocation)
                {
                    return false;
                }
            }
        }
        else
        {
            if (peerWill)
            {
                if (inputOption == (int)Options.WindowSize ||
                    inputOption == (int)Options.LineMode ||
                    inputOption == (int)Options.SendLocation ||
                    inputOption == (int)Options.TerminalType ||
                    inputOption == (int)Options.TerminalSpeed ||
                    inputOption == (int)Options.RemoteFlowControl ||
                    inputOption == (int)Options.XDisplay ||
                    inputOption == (int)Options.NewEnvironment)
                {
                    // One-directional server-side options (reference: the
                    // client end DONTs every WILL except CHARSET): a
                    // client never asks the peer to send TTYPE/TSPEED and
                    // friends — the server requests them.
                    return false;
                }
            }
        }

        return inputOption == (int)Options.SuppressGoAhead ||
          inputOption == (int)Options.TerminalType ||
          inputOption == (int)Options.TerminalSpeed ||
          inputOption == (int)Options.WindowSize ||
          inputOption == (int)Options.TransmitBinary ||
          inputOption == (int)Options.OldEnvironment ||
          inputOption == (int)Options.NewEnvironment ||
          inputOption == (int)Options.XDisplay ||
          inputOption == (int)Options.Status ||
          inputOption == (int)Options.TimingMark ||
          inputOption == (int)Options.LineMode ||
          inputOption == (int)Options.SendLocation ||
          inputOption == (int)Options.EndOfRecord ||
          inputOption == (int)Options.RemoteFlowControl ||
          inputOption == (int)Options.CharacterSet;
    }

    /// <summary>
    /// Agreement for RFC 857 ECHO, which needs per-direction guards on top of
    /// <see cref="WeAgree"/>: the option only controls remote echo, and both
    /// sides echoing at once bounces characters forever.
    /// </summary>
    /// <param name="inputOption">The negotiated option.</param>
    /// <param name="peerPerforms">True for a received WILL (the peer would echo).</param>
    /// <returns>Whether to agree: non-ECHO defers to <see cref="WeAgree"/>.</returns>
    private bool AgreeEcho(int inputOption, bool peerPerforms)
    {
        if (inputOption != (int)Options.Echo)
        {
            return WeAgree(inputOption, peerPerforms);
        }

        if (peerPerforms)
        {
            return !Negotiation.IsEnabledByUs(inputOption);
        }

        if (!IsServerRole)
        {
            return false;
        }

        return AllowRemoteEcho && !Negotiation.IsEnabledByPeer(inputOption);
    }

    /// <summary>
    /// Reports the terminal size per RFC 1073 as Width(16-bit) Height(16-bit),
    /// network byte order. Each <see cref="WindowWidth"/>/
    /// <see cref="WindowHeight"/> dimension is clamped to the 0-65535 wire
    /// range and sent as-is (a 0 dimension is RFC 1073 "unspecified").
    /// </summary>
    private Task SendWindowSize()
    {
        var (width, height) = NawsProtocol.GetEffectiveSize(WindowWidth, WindowHeight);
        NawsSizeSent?.Invoke(width, height);
        byte[] payload =
        [
    (byte)(width >> 8), (byte)width,
    (byte)(height >> 8), (byte)height,
        ];
        return SendNegotiation((int)Options.WindowSize, payload);
    }

    private async Task<bool> IsResponseAnticipated(bool isInitialResponseReceived, DateTime endInitialTimeout, DateTime rollingTimeout)
    {
        // A peer close observed mid-read ends the slice at once
        // instead of sleeping out the timeout: Connected already
        // dropped (a 0-byte read closed the stream, or the FIN probe
        // fired), so no further data can arrive.
        if (!byteStream.Connected)
        {
            return false;
        }

        // Drain immediately while bytes wait. Otherwise yield every idle
        // pass: an earlier form short-circuited on the open initial window
        // and never reached the delay, busy-spinning the calling thread
        // for the whole window and starving same-thread producers of
        // mid-read bytes (#62: a blocked read never observed an Enqueue).
        if (IsResponsePending)
        {
            return true;
        }

        bool continueWaiting = IsWaitForInitialResponse(endInitialTimeout, isInitialResponseReceived) ||
          !IsTimeoutExpired(rollingTimeout);
        await Task.Delay(MillisecondReadDelay, internalCancellation.Token).ConfigureAwait(false);
        return continueWaiting;
    }
}
