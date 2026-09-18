namespace telnet_cs.IO;

using System.Text;
using telnet_cs.Protocol;
using telnet_cs.Transport;
/// <summary>
/// Inbound response parsing: text accumulation, SLC snooping, command dispatch, subnegotiation frame scanning and pending-command resume. Split from <see cref="ByteStreamHandler"/>; wire behavior is unchanged.
/// </summary>
public partial class ByteStreamHandler
{

    /// <summary>
    /// Separate TELNET commands from text. Handle non-printable characters.
    /// </summary>
    /// <param name="sb">The incoming message.</param>
    /// <param name="rawBytes">The raw data bytes backing <paramref name="sb"/> (used when <see cref="TextEncoding"/> is set).</param>
    /// <param name="opByteCounts">Parallel to <paramref name="sb"/>: bytes of <paramref name="rawBytes"/> per appended char.</param>
    /// <returns>True if response is pending.</returns>
    private async Task<bool> RetrieveAndParseResponse(StringBuilder sb, List<byte> rawBytes, List<int> opByteCounts)
    {
        // A corrupt MCCP stream arms its refusal from the sync byte path;
        // flush it at the async points around this pass (extension: the
        // reference only clears state and logs on corrupt data).
        await FlushMccpShutdownAsync().ConfigureAwait(false);

        // RFC 854 Synch trigger: a pending urgent byte enters discard mode.
        // Poll-gated and TCP-only, so fakes and pipes never see it; the urgent
        // byte itself is consumed by the probe, and in-band IAC DM (below)
        // ends the mode.
        var synch = await RetrieveSynchDiscardAsync(sb, rawBytes, opByteCounts).ConfigureAwait(false);
        if (synch.HasValue)
        {
            return synch.Value;
        }

        if (sawCrAwaitingNul && (pushbackByte.HasValue || MccpHasOutput || byteStream.Available > 0))
        {
            var followingCr = ReadNextByte();
            if (followingCr == -1)
            {
                return false;
            }

            sawCrAwaitingNul = false;
            if (followingCr == 0)
            {
                AppendData(sb, rawBytes, opByteCounts, "\0");
                await FlushMccpShutdownAsync().ConfigureAwait(false);
                return true;
            }

            pushbackByte = followingCr;
            await FlushMccpShutdownAsync().ConfigureAwait(false);
            return true;
        }

        if (!pushbackByte.HasValue && (pendingIac || pendingVerb.HasValue) && (MccpHasOutput || byteStream.Available > 0))
        {
            // RFC 854 command split across reads completes here.
            if (await ResumePendingCommandAsync(sb, rawBytes, opByteCounts).ConfigureAwait(false))
            {
                return true;
            }

            return false;
        }

        if (!pushbackByte.HasValue && sbHeaderIacPending && (MccpHasOutput || byteStream.Available > 0))
        {
            // A bare IAC SB IAC stalled on an earlier read resumes here:
            // the newly arrived byte completes the empty-frame probe.
            await PerformNegotiation(sb, rawBytes, opByteCounts).ConfigureAwait(false);
            return true;
        }

        if (!pushbackByte.HasValue && sbResumeOption.HasValue && (MccpHasOutput || byteStream.Available > 0))
        {
            // A subnegotiation stalled on an earlier read resumes here:
            // the newly arrived bytes continue its frame (telnetlib3
            // _sb_buffer parity) instead of being parsed as fresh input.
            await PerformNegotiation(sb, rawBytes, opByteCounts).ConfigureAwait(false);
            return true;
        }

        if (IsResponsePending)
        {
            var input = ReadNextByte();
            switch (input)
            {
                case -1:
                    break;
                case IacByte:
                    var inputVerb = TryReadByte();
                    if (inputVerb == -1)
                    {
                        // RFC 854 framing split: the verb arrives with the continuation.
                        pendingIac = true;
                    }
                    else if (inputVerb == IacByte)
                    {
                        // Escaped literal data byte 255: one char + one raw byte,
                        // not the decimal string "255".
                        AppendRecorded(sb, rawBytes, opByteCounts, (char)IacByte);
                    }
                    else
                    {
                        await InterpretNextAsCommand(sb, rawBytes, opByteCounts, inputVerb).ConfigureAwait(false);
                    }

                    break;
                case 1 or 2 or 3 or 4 or 5 or 6 or 7 or 8 or 11 or 12 or 21 or 31:
                    // NVT control bytes are data, not commands: forward them
                    // verbatim so the byte stream round-trips exactly. No
                    // expansions ("^C", "NAK: ..." text), no drops (BEL,
                    // ACK), no wire side effects (ENQ must not emit an
                    // unsolicited ACK), and no destructive editing (BS must
                    // not delete already-delivered bytes) — any terminal
                    // presentation belongs in a layer above this parser.
                    // CR keeps its RFC 854 handling in the next case.
                    AppendData(sb, rawBytes, opByteCounts, (char)input);
                    break;
                case 13: // Carriage Return: CR is delivered now; a following
                    // NUL is preserved as data by the raw path (the line
                    // layer collapses CR NUL to CR). CR LF stays CR LF.
                    AppendData(sb, rawBytes, opByteCounts, "\r");
                    sawCrAwaitingNul = true;
                    break;
                default:
                    AppendData(sb, rawBytes, opByteCounts, (char)input);
                    break;
            }

            await FlushMccpShutdownAsync().ConfigureAwait(false);
            return true;
        }

        return false;
    }

    private static void AppendRecorded(StringBuilder sb, List<byte> rawBytes, List<int> opByteCounts, string text)
    {
        sb.Append(text);
        rawBytes.AddRange(Encoding.ASCII.GetBytes(text));
        // Char-aligned: ASCII yields exactly one byte per char, so each sb
        // char maps to exactly one count entry (the documented invariant).
        for (var i = 0; i < text.Length; i++)
        {
            opByteCounts.Add(1);
        }
    }

    private static void AppendRecorded(StringBuilder sb, List<byte> rawBytes, List<int> opByteCounts, char c)
    {
        sb.Append(c);
        // Data bytes are Latin-1 by definition here: the default (null
        // encoding) path returns sb.ToString() verbatim, so the recorded byte
        // only matters for explicit TextEncoding decoding.
        rawBytes.Add((byte)c);
        opByteCounts.Add(1);
    }

    /// <summary>
    /// Appends a delivered data byte and runs the SLC snoop over it
    /// (telnetlib3's data-byte branch: snoop, then forward in-band).
    /// Framed bytes that merely ride along as data — the IAC IAC escape,
    /// a stray SE, an illegal IAC verb — use the plain recorded append
    /// directly: the reference never snoops those paths either.
    /// </summary>
    private void AppendData(StringBuilder sb, List<byte> rawBytes, List<int> opByteCounts, char c)
    {
        AppendRecorded(sb, rawBytes, opByteCounts, c);
        SnoopDataByte((byte)c);
    }

    /// <summary>
    /// Appends delivered data text (CR NUL collapse products) and snoops
    /// each byte, like the single-byte append below.
    /// </summary>
    private void AppendData(StringBuilder sb, List<byte> rawBytes, List<int> opByteCounts, string text)
    {
        AppendRecorded(sb, rawBytes, opByteCounts, text);
        foreach (var ch in text)
        {
            SnoopDataByte((byte)ch);
        }
    }

    /// <summary>
    /// Tests one delivered data byte against the SLC table while snoop is
    /// active: on a match, records the function in <see cref="SlcReceived"/>
    /// and fires <see cref="SlcFunctionReceived"/>; on a miss, records
    /// null. The byte itself is unaffected (already appended).
    /// </summary>
    private void SnoopDataByte(byte value)
    {
        if (!IsSlcSnoopActive())
        {
            return;
        }

        var function = Linemode.Snoop(value);
        SlcReceived = function;
        if (function.HasValue)
        {
            SlcFunctionReceived?.Invoke(function.Value);
        }
    }

    /// <summary>
    /// Whether delivered data bytes are SLC-snooped (telnetlib3's
    /// <c>mode == "remote" or mode == "kludge" and slc_simulated</c>).
    /// Remote line editing means LINEMODE agreed by the peer with EDIT
    /// clear; otherwise the pre-LINEMODE char-mode heuristic applies —
    /// server side our ECHO + SGA, client side their ECHO plus either
    /// side's SGA. Simulation has no off switch here (the reference
    /// default), so the kludge half needs no extra flag.
    /// </summary>
    private bool IsSlcSnoopActive()
    {
        if (Negotiation.IsEnabledByPeer((int)Options.LineMode))
        {
            return (Linemode.Mode & LinemodeProtocol.Edit) == 0;
        }

        if (ApplyLinemodeAsServer)
        {
            return Negotiation.IsEnabledByUs((int)Options.Echo)
                && Negotiation.IsEnabledByUs((int)Options.SuppressGoAhead);
        }

        return Negotiation.IsEnabledByPeer((int)Options.Echo)
            && (Negotiation.IsEnabledByPeer((int)Options.SuppressGoAhead)
                || Negotiation.IsEnabledByUs((int)Options.SuppressGoAhead));
    }

    /// <summary>
    /// We received a TELNET command. Handle it. Commands are consumed
    /// without touching the accumulation buffer: the decoded text and the
    /// raw bytes backing it are the application's byte record, and only
    /// data bytes may append to them.
    /// </summary>
    /// <param name="sb">The incoming message.</param>
    /// <param name="rawBytes">The raw data bytes backing <paramref name="sb"/> (used when <see cref="TextEncoding"/> is set).</param>
    /// <param name="opByteCounts">Parallel to <paramref name="sb"/>: bytes of <paramref name="rawBytes"/> per appended char.</param>
    /// <param name="inputVerb">The command we received.</param>
    private async Task InterpretNextAsCommand(StringBuilder sb, List<byte> rawBytes, List<int> opByteCounts, int inputVerb)
    {
        WriteLog(Enum.GetName(typeof(Commands), inputVerb) ?? inputVerb.ToString());
        switch (inputVerb)
        {
            case (int)Commands.InterruptProcess:
                // Consumed and logged without reply, state change, or
                // read cancellation (reference handle_ip): the in-flight
                // read keeps delivering the bytes around it.
                WriteLog("Interrupt Process (IP) received.");
                return;
            case (int)Commands.AreYouThere:
                // Consumed without reply: answering with printable bytes
                // would inject peer-visible data that no framing accounts
                // for, corrupting strict request/response exchanges.
                WriteLog("Are You There (AYT) received.");
                return;
            case (int)Commands.AbortOutput:
                // This design has no output queue (writes go straight to the
                // stream), so there is nothing to discard: consume and log.
                WriteLog("Abort Output (AO) received; no queued output to discard.");
                return;
            case (int)Commands.EraseCharacter:
                // Consumed without editing: the delivery buffer is the
                // application's byte record, not a terminal line — erasing
                // from it would destroy already-delivered data and corrupt
                // raw-byte counts and echo accounting.
                WriteLog("Erase Character (EC) received.");
                return;
            case (int)Commands.EraseLine:
                // Same as EC: consumed, buffer untouched.
                WriteLog("Erase Line (EL) received.");
                return;
            case (int)Commands.Break:
                // Out-of-band signal: consumed and logged, never surfaced
                // as text — marker strings would be phantom data to any
                // caller matching terminators or counting bytes.
                WriteLog("Break (BRK) received.");
                return;
            case (int)Commands.EndOfFile:
                // Same as BRK: consumed, never text.
                WriteLog("End of file (EOF) received.");
                return;
            case (int)Commands.Suspend:
                // Same as BRK: consumed, never text.
                WriteLog("Suspend (SUSP) received.");
                return;
            case (int)Commands.Abort:
                // Same as BRK: consumed, never text.
                WriteLog("Abort (ABORT) received.");
                return;
            case (int)Commands.EndOfRecord:
                // RFC 885: IAC EOR marks a prompt boundary with no
                // subnegotiation. Surfaced through the hook like GA, with
                // no agreement gate: peers commonly send the marker
                // without negotiating the option first, and delivering it
                // is harmless — no reply is emitted, no data is produced,
                // and a caller that needs gating can check the
                // negotiation state itself.
                WriteLog("End of record (EOR) received.");
                EorReceived?.Invoke();
                return;
            case (int)Commands.SubnegotiationEnd:
                // A bare IAC SE with no open SB block is delivered as data
                // byte 0xF0 instead of being consumed. The RFCs give SE no
                // standalone meaning (it only terminates an SB block), so
                // either treatment is standards-compliant; delivering keeps
                // the never-drop-bytes invariant (unparseable framed bytes
                // fall through to the reader, exactly like the IAC IAC
                // escape path, which likewise bypasses the 8-bit gate)
                // and matches telnetlib3's parser.
                WriteLog("Stray SE outside subnegotiation; delivering 0xF0 as data.");
                AppendRecorded(sb, rawBytes, opByteCounts, (char)Commands.SubnegotiationEnd);
                return;
            case (int)Commands.NoOperation:
            case (int)Commands.DataMark:
            case (int)Options.TimingMark:
                // Stray NOP, and DM in normal mode (RFC 854: DM is a NOP
                // outside Synch processing), carry no data: consume silently.
                // Byte 6 (TM) rides along: it is not a defined RFC 854
                // command, but telnetlib3 registers a NOP callback for it,
                // so it must not fall into the data default below.
                return;
            case (int)Commands.GoAhead:
                // RFC 858 §5: GA is a NOP only while Suppress-GA is in effect on
                // the peer's transmit path; otherwise it is the NVT turn-taking
                // signal, surfaced through the GoAheadReceived hook.
                if (Negotiation.IsEnabledByPeer((int)Options.SuppressGoAhead))
                {
                    return;
                }

                WriteLog("Go Ahead (GA) received; Suppress-GA not in effect.");
                GoAheadReceived?.Invoke();
                return;
            case (int)Commands.Dont:
            case (int)Commands.Wont:
            case (int)Commands.Do:
            case (int)Commands.Will:
                // All four negotiation verbs flow through ReplyToCommand, which
                // consults the persistent RFC 1143 state: the option byte always
                // belongs to the command (never leaks into data), and refusals or
                // repeats are answered only when the state machine says so.
                await ReplyToCommand(inputVerb).ConfigureAwait(false);
                return;
            case (int)Commands.Subnegotiation:
                await PerformNegotiation(sb, rawBytes, opByteCounts).ConfigureAwait(false);
                return;
            default:
                // IAC followed by a byte with no defined TELNET command
                // meaning and no registered callback is delivered as
                // in-band data instead of being consumed. Every command
                // this parser handles (including TM, which telnetlib3
                // answers with a NOP callback) has an explicit case above,
                // so anything reaching here is one of telnetlib3's "not a
                // legal 2-byte cmd" bytes, which its parser feeds through
                // as data (never-drop-bytes); like the IAC IAC escape this
                // bypasses the 8-bit gate because the peer framed the byte
                // explicitly.
                WriteLog($"Illegal 2-byte IAC {inputVerb}; delivering as data.");
                AppendRecorded(sb, rawBytes, opByteCounts, (char)inputVerb);
                return;
        }
    }

    /// <summary>
    /// We received a request to perform sub negotiation on a TELNET option.
    /// The terminal type, speed, and window size are taken from the settable
    /// properties on this handler (fed per read from the client's settings).
    /// </summary>
    private async Task PerformNegotiation(StringBuilder sb, List<byte> rawBytes, List<int> opByteCounts)
    {
        int inputOption;
        List<byte> payload;
        bool overCap;
        bool iacPending;
        if (sbHeaderIacPending)
        {
            // Resuming a bare IAC SB IAC stalled on an earlier read: the
            // next byte completes the empty-frame probe (reference: cmd=SB
            // with iac set persists across feed_byte calls).
            sbHeaderIacPending = false;
            var headerFollowing = TryReadByte();
            if (headerFollowing == -1)
            {
                sbHeaderIacPending = true;
                return;
            }

            if (headerFollowing == SeByte)
            {
                WriteLog("Discarding empty subnegotiation without option byte.");
                return;
            }

            pushbackByte = headerFollowing;
            return;
        }

        if (sbResumeOption.HasValue)
        {
            // Resuming a subnegotiation stalled mid-scan on an earlier
            // read: the continuation bytes belong to this frame.
            inputOption = sbResumeOption.Value;
            payload = sbResumePayload!;
            overCap = sbResumeOverCap;
            iacPending = sbResumeIacPending;
            sbResumeOption = null;
            sbResumePayload = null;
            sbResumeOverCap = false;
            sbResumeSePending = false;
            sbResumeIacPending = false;
        }
        else
        {
            var option = TryReadByte();
            if (option == -1)
            {
                // RFC 854 framing split: the option byte arrives with the continuation.
                pendingVerb = (int)Commands.Subnegotiation;
                return;
            }

            if (option == IacByte)
            {
                var following = TryReadByte();
                if (following == SeByte)
                {
                    WriteLog("Discarding empty subnegotiation without option byte.");
                    return;
                }

                if (following == -1)
                {
                    // RFC 854 framing split: IAC SB IAC arrived with the
                    // post-IAC byte still in flight. Stash the probe; the
                    // continuation completes it above.
                    sbHeaderIacPending = true;
                    return;
                }

                pushbackByte = following;
                return;
            }

            inputOption = option;
            payload = [];
            overCap = false;
            iacPending = false;
        }

        await ScanAndDispatchSbAsync(inputOption, payload, overCap, iacPending, sb, rawBytes, opByteCounts).ConfigureAwait(false);
    }

    /// <summary>
    /// Dispatches a command interrupting an open SB frame: the outer
    /// payload is dropped (reference: warns "interrupted by IAC", clears
    /// its SB buffer) and the inner verb is handled exactly as if it
    /// arrived outside the frame, so no payload or option bytes leak into
    /// the data stream.
    /// </summary>
    private async Task DispatchInterruptedSbAsync(int following,
        StringBuilder sb, List<byte> rawBytes, List<int> opByteCounts)
    {
        if (following is (int)Commands.Do or (int)Commands.Dont or (int)Commands.Will or (int)Commands.Wont)
        {
            var innerOption = TryReadByte();
            if (innerOption == -1)
            {
                // RFC 854 framing split: the inner option byte arrives
                // with the continuation.
                pendingVerb = following;
                return;
            }

            while (innerOption == IacByte)
            {
                // IAC toggle in option position: the paired IAC is framing,
                // the next byte is the real option.
                innerOption = TryReadByte();
                if (innerOption == -1)
                {
                    pendingVerb = following;
                    return;
                }
            }

            await ReplyToCommandWithOption(following, innerOption).ConfigureAwait(false);
            return;
        }

        if (following == (int)Commands.Subnegotiation)
        {
            await PerformNegotiation(sb, rawBytes, opByteCounts).ConfigureAwait(false);
            return;
        }

        // Anything else (a data byte or a non-negotiation command):
        // framing is lost. The reference drops the outer frame and
        // swallows the interrupting byte as a pseudo-command (never
        // data, never dispatched), so consume it silently here too.
        WriteLog($"Subnegotiation interrupted by IAC {following}; dropping the frame.");
        return;
    }

    private async Task ScanAndDispatchSbAsync(int inputOption, List<byte> payload, bool overCap, bool iacPending,
        StringBuilder sb, List<byte> rawBytes, List<int> opByteCounts)
    {
        // Scan to IAC SE. The payload is capped: over-long input keeps being
        // consumed (so the stream resynchronises) but is then ignored.
        // Framing is uniform for every option (reference parity): only
        // IAC SE terminates; a bare SE byte is ordinary payload data.
        // A stall mid-scan stashes the frame; the next read resumes it.
        bool scanDone = false;

        if (!scanDone && iacPending)
        {
            // RFC 854 IAC SE split: the IAC was consumed, its following byte arrives now.
            var followingSplit = TryReadByte();
            if (followingSplit == -1)
            {
                StashSbResume(inputOption, payload, overCap, seAwait: false, iacAwait: true);
                return;
            }

            if (followingSplit == SeByte)
            {
                scanDone = true;
            }
            else if (followingSplit == IacByte)
            {
                AddPayloadByte(IacByte);
            }
            else if (followingSplit == (int)Commands.Subnegotiation)
            {
                // RFC 854 defines no nesting, so a second IAC SB inside an
                // open frame cannot be a nested frame. Recovery: drop the
                // outer frame and scan the inner one fresh, which is what
                // the reference implementation does (it warns, clears its
                // SB buffer, and re-buffers starting from the inner SB, so
                // the inner frame is still terminated at its own IAC SE
                // and dispatched normally — an unknown inner option is then
                // ignored without reply, a known one handled as usual).
                // PerformNegotiation below does exactly that: it reads a
                // fresh option byte and starts a fresh scan.
                // Do NOT "fix" this by returning early instead: the inner
                // frame's remaining bytes (option, payload, IAC SE) would
                // stay in the stream and be delivered as application data
                // on the next read, which neither this stack nor the
                // reference does — both consume through the inner IAC SE.
                // Any reply-vs-silence difference for the inner frame comes
                // from the payload dispatch rules (SEND vs stray payload),
                // not from this recovery path.
                await PerformNegotiation(sb, rawBytes, opByteCounts).ConfigureAwait(false);
                return;
            }
            else
            {
                // The outer frame is interrupted by an inner command: drop
                // the buffered payload (reference: warns, clears its SB
                // buffer) and dispatch the inner command normally.
                await DispatchInterruptedSbAsync(followingSplit, sb, rawBytes, opByteCounts).ConfigureAwait(false);
                return;
            }
        }

        if (!scanDone)
        {
            while (true)
            {
                var b = TryReadByte();
                if (b == -1)
                {
                    StashSbResume(inputOption, payload, overCap, seAwait: false, iacAwait: false);
                    return;
                }

                if (b == IacByte)
                {
                    var following = TryReadByte();
                    if (following == -1)
                    {
                        StashSbResume(inputOption, payload, overCap, seAwait: false, iacAwait: true);
                        return;
                    }

                    if (following == SeByte)
                    {
                        break;
                    }

                    if (following == IacByte)
                    {
                        // Escaped literal IAC inside the payload.
                        AddPayloadByte(IacByte);
                        continue;
                    }

                    if (following == (int)Commands.Subnegotiation)
                    {
                        // Same recovery as the split path above: the outer
                        // frame is dropped and the inner one is scanned
                        // fresh (see the detailed note there). Returning
                        // early here would leak the inner frame's tail
                        // into the data stream — never do that.
                        await PerformNegotiation(sb, rawBytes, opByteCounts).ConfigureAwait(false);
                        return;
                    }

                    // The outer frame is interrupted by an inner command:
                    // drop the buffered payload and dispatch it normally.
                    await DispatchInterruptedSbAsync(following, sb, rawBytes, opByteCounts).ConfigureAwait(false);
                    return;
                }

                AddPayloadByte((byte)b);
            }
        }

        void AddPayloadByte(byte value)
        {
            if (overCap)
            {
                return;
            }

            if (payload.Count < MaxSubnegotiationBytes)
            {
                payload.Add(value);
            }
            else
            {
                overCap = true;
            }
        }

        void StashSbResume(int option, List<byte> body, bool capped, bool seAwait, bool iacAwait)
        {
            sbResumeOption = option;
            sbResumePayload = body;
            sbResumeOverCap = capped;
            sbResumeSePending = seAwait;
            sbResumeIacPending = iacAwait;
        }

        if (overCap)
        {
            StormGuard?.NoteFrame();
            return;
        }

        // A full subnegotiation frame arrived: count it toward the
        // storm window before any dispatch below.
        StormGuard?.NoteFrame();

        if (payload.Count == 0)
        {
            // MCCP2/MCCP3 start on an empty SB, and several MUD options
            // allow one; anything else empty is dropped (unchanged).
            if (IsEmptySbAllowed(inputOption))
            {
                ReplyEmptySb(inputOption);
            }

            return;
        }

        if (SubnegotiationResponse?.Invoke(inputOption, payload) is true)
        {
            // Server-role consumer (TTYPE/TSPEED/ENVIRON IS or INFO, inbound
            // NAWS, LINEMODE import requests): collected or answered by the
            // session, nothing further to do. Null for clients and
            // directly-constructed handlers, so their path is unchanged.
            return;
        }

        if (inputOption == (int)Options.LineMode)
        {
            // LINEMODE payloads start with their own subcommand (MODE /
            // FORWARDMASK / SLC), not SEND, so they bypass the SEND gate below.
            // In particular a server DO FORWARDMASK ([253, 2, …]) must be
            // refused in-band, never mistaken for a stray SEND.
            await ReplyLinemodeAsync(payload).ConfigureAwait(false);
            return;
        }

        if (inputOption == (int)Options.SendLocation)
        {
            // RFC 779: the SB carries the raw ASCII location with no
            // SEND/IS discrimination.
            ReplySendLocation(payload);
            return;
        }

        if (inputOption == (int)Options.RemoteFlowControl)
        {
            // RFC 1372: the SB carries a single mode byte (0-3), no verbs.
            await ReplyLineflowAsync(payload).ConfigureAwait(false);
            return;
        }

        if (inputOption == (int)Options.COMPortControl)
        {
            // RFC 2217 framing level: surface the raw payload; modem-line
            // semantics stay the caller's.
            ComPortReceived?.Invoke([.. payload]);
            return;
        }

        if (IsMudOption(inputOption))
        {
            byte[] body = [.. payload];
            DispatchMud(inputOption, body);
            if (inputOption == (int)Options.Zmp)
            {
                await AnswerZmpAsync(body).ConfigureAwait(false);
            }

            return;
        }

        if (inputOption == (int)Options.Mccp2 || inputOption == (int)Options.Mccp3)
        {
            // Padding-carrying SBs start compression under the same gates
            // as the empty form (agreement first, never over TLS).
            if (!MccpStartAllowed(inputOption))
            {
                return;
            }

            // Compression has a direction: MCCP2 flows server-to-client
            // (only a client inflates), MCCP3 client-to-server (only a
            // server inflates). A marker for the wrong direction never
            // arms inflation; a client seeing the peer's MCCP3 marker
            // only makes sure its own outbound view is up.
            if (inputOption == (int)Options.Mccp2)
            {
                if (IsServerRole)
                {
                    return;
                }

                WriteLog("Starting MCCP compression; ignoring padding bytes.");
                Mccp2Active = true;
                ArmMccpStream(inputOption);
                Mccp2StartReceived?.Invoke();
                return;
            }

            if (!IsServerRole)
            {
                // Wrong direction to inflate, and outbound already
                // started when we agreed to the peer's WILL: nothing
                // more to do.
                return;
            }

            WriteLog("Starting MCCP compression; ignoring padding bytes.");
            Mccp3Active = true;
            ArmMccpStream(inputOption);
            Mccp3StartReceived?.Invoke();
            return;
        }

        if (inputOption == (int)Options.CharacterSet && payload[0] != CharsetProtocol.Request)
        {
            // ACCEPTED/REJECTED answers (and unimplemented table verbs)
            // never look like SEND, so they bypass the gate below.
            await ReplyCharsetAnswerAsync(payload).ConfigureAwait(false);
            return;
        }

        if (payload[0] != 1) // Sub-negotiation SEND command.
        {
            WriteLog("Ignoring unsolicited subnegotiation answer.");
            return;
        }

        await ReplySendAsync(inputOption, payload).ConfigureAwait(false);
    }

    /// <summary>
    /// Completes an RFC 854 command split across reads: a stashed IAC or
    /// IAC-plus-verb consumes its continuation bytes and dispatches exactly
    /// as if the three bytes arrived together.
    /// </summary>
    /// <param name="sb">The incoming message.</param>
    /// <param name="rawBytes">The raw data bytes backing <paramref name="sb"/>.</param>
    /// <param name="opByteCounts">Parallel to <paramref name="sb"/>: bytes of <paramref name="rawBytes"/> per appended char.</param>
    private async Task<bool> ResumePendingCommandAsync(StringBuilder sb, List<byte> rawBytes, List<int> opByteCounts)
    {
        if (pendingIac)
        {
            pendingIac = false;
            var verb = TryReadByte();
            if (verb == -1)
            {
                pendingIac = true;
                return false;
            }

            if (verb == IacByte)
            {
                AppendRecorded(sb, rawBytes, opByteCounts, (char)IacByte);
                return true;
            }

            if (verb is (int)Commands.Dont or (int)Commands.Wont or (int)Commands.Do or (int)Commands.Will)
            {
                var stashedOption = TryReadByte();
                if (stashedOption == -1)
                {
                    pendingVerb = verb;
                    return true;
                }

                while (stashedOption == IacByte)
                {
                    // IAC toggle in option position (reference: the IAC
                    // pair is framing, the byte after it is the real
                    // option): consume the pair and re-read.
                    stashedOption = TryReadByte();
                    if (stashedOption == -1)
                    {
                        pendingVerb = verb;
                        return true;
                    }
                }

                await ReplyToCommandWithOption(verb, stashedOption).ConfigureAwait(false);
                return true;
            }

            if (verb == (int)Commands.Subnegotiation)
            {
                var sbOption = TryReadByte();
                if (sbOption == -1)
                {
                    pendingVerb = verb;
                    return true;
                }

                if (sbOption == IacByte)
                {
                    var sbFollowing = TryReadByte();
                    if (sbFollowing == -1)
                    {
                        sbHeaderIacPending = true;
                        return true;
                    }

                    if (sbFollowing == SeByte)
                    {
                        WriteLog("Discarding empty subnegotiation without option byte.");
                        return true;
                    }

                    pushbackByte = sbFollowing;
                    return true;
                }

                await ScanAndDispatchSbAsync(sbOption, [], false, false, sb, rawBytes, opByteCounts).ConfigureAwait(false);
                return true;
            }

            await InterpretNextAsCommand(sb, rawBytes, opByteCounts, verb).ConfigureAwait(false);
            return true;
        }

        if (pendingVerb.HasValue)
        {
            var stashedVerb = pendingVerb.Value;
            pendingVerb = null;
            var stashedOption = TryReadByte();
            if (stashedOption == -1)
            {
                pendingVerb = stashedVerb;
                return false;
            }

            if (stashedVerb == (int)Commands.Subnegotiation && stashedOption == IacByte)
            {
                var sbFollowing = TryReadByte();
                if (sbFollowing == -1)
                {
                    sbHeaderIacPending = true;
                    return true;
                }

                if (sbFollowing == SeByte)
                {
                    WriteLog("Discarding empty subnegotiation without option byte.");
                    return true;
                }

                pushbackByte = sbFollowing;
                return true;
            }

            while (stashedVerb != (int)Commands.Subnegotiation && stashedOption == IacByte)
            {
                // IAC toggle in option position: consume the pair, the
                // next byte is the real option.
                stashedOption = TryReadByte();
                if (stashedOption == -1)
                {
                    pendingVerb = stashedVerb;
                    return false;
                }
            }

            if (stashedVerb == (int)Commands.Subnegotiation)
            {
                await ScanAndDispatchSbAsync(stashedOption, [], false, false, sb, rawBytes, opByteCounts).ConfigureAwait(false);
                return true;
            }

            await ReplyToCommandWithOption(stashedVerb, stashedOption).ConfigureAwait(false);
            return true;
        }

        return false;
    }
}
