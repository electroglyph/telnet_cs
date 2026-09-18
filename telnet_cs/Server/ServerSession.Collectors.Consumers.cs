namespace telnet_cs.Server;

using System.Runtime.InteropServices;
using telnet_cs.IO;
using telnet_cs.Protocol;
using telnet_cs.Transport;

/// <summary>
/// Server-role inbound subnegotiation consumers: routes IS/INFO (and inbound NAWS) payloads into the collectors, plus the shared response poller and the framed send helpers. Split from <see cref="ServerSession"/> collectors; wire behavior is unchanged.
/// </summary>
public partial class ServerSession
{
    private bool OnSubnegotiationResponse(int inputOption, List<byte> payload)
    {
        if (payload.Count == 0)
        {
            return false;
        }

        if (inputOption == (int)Options.WindowSize)
        {
            return TryConsumeNaws(payload);
        }

        if (inputOption == (int)Options.LineMode)
        {
            return TryConsumeLinemodeImport(payload);
        }

        if (inputOption == (int)Options.SendLocation)
        {
            // RFC 779: the SB is the raw ASCII location (no verbs), so it
            // precedes the IS/INFO gate below.
            return TryConsumeSendLocation(payload);
        }

        if (inputOption == (int)Options.CharacterSet)
        {
            // RFC 2066 ACCEPTED shares its byte value (2) with INFO, so it
            // is consumed here, ahead of the gate.
            if (!TryConsumeCharset(payload))
            {
                return false;
            }

            RefreshActiveReadHandlerEncoding();
            return true;
        }

        if (inputOption == (int)Options.Status)
        {
            // RFC 859: STATUS IS reports the peer's option state for
            // display only — parse and record it, never touching the
            // Q-machine (the reference only logs the comparison).
            return TryConsumeStatus(payload);
        }

        if (payload[0] != EnvironmentProtocol.Is && payload[0] != EnvironmentProtocol.Info)
        {
            return false;
        }

        if (inputOption == (int)Options.TerminalType)
        {
            return TryConsumeTerminalType(payload);
        }

        if (inputOption == (int)Options.TerminalSpeed)
        {
            return TryConsumeTerminalSpeed(payload);
        }

        if (inputOption == (int)Options.XDisplay)
        {
            return TryConsumeXDisplay(payload);
        }

        if (inputOption == (int)Options.OldEnvironment || inputOption == (int)Options.NewEnvironment)
        {
            return TryConsumeEnvironment(inputOption, payload);
        }

        return false;
    }

    private bool TryConsumeNaws(List<byte> payload)
    {
        lock (collectorLock)
        {
            if (payload.Count == 4)
            {
                // Strict RFC 1073 shape (no verb).
                clientWindowSize = ((ushort)(payload[0] << 8 | payload[1]), (ushort)(payload[2] << 8 | payload[3]));
            }
            else
            {
                return false;
            }

            // A size report assumes the peer enabled NAWS even when no
            // WILL arrived first: latch remote agreement (no reply bytes
            // ever answer an SB) so STATUS listings and wait gates see it.
            Negotiation.ReceivedWill((int)Options.WindowSize, agree: true);
            return true;
        }
    }

    private bool TryConsumeStatus(List<byte> payload)
    {
        // RFC 859: IS followed by WILL/WONT/DO/DONT <opt> pairs and
        // SB <opt> <data> SE blocks. Unknown single bytes are skipped so
        // following valid pairs still parse; only a trailing lone byte
        // ends the parse. The frame is still consumed either way.
        if (payload.Count == 0 || payload[0] != StatusProtocol.Is)
        {
            return false;
        }

        var items = new List<StatusReportItem>();
        int i = 1;
        while (i < payload.Count)
        {
            if (i + 1 >= payload.Count)
            {
                break;
            }

            if (payload[i] == (byte)Commands.Subnegotiation)
            {
                byte option = payload[i + 1];
                int end = payload.IndexOf((byte)Commands.SubnegotiationEnd, i + 2);
                byte[] data = end < 0
                  ? [.. payload.Skip(i + 2)]
                  : [.. payload.Skip(i + 2).Take(end - (i + 2))];
                items.Add(new StatusReportItem(Commands.Subnegotiation, option, data));
                if (end < 0)
                {
                    break;
                }

                i = end + 1;
                continue;
            }

            if (payload[i] is (byte)Commands.Will or (byte)Commands.Wont or (byte)Commands.Do or (byte)Commands.Dont)
            {
                items.Add(new StatusReportItem((Commands)payload[i], payload[i + 1], null));
                i += 2;
                continue;
            }

            i++;
        }

        lock (collectorLock)
        {
            peerStatusReport = [.. items];
        }

        return true;
    }

    private bool TryConsumeTerminalType(List<byte> payload)
    {
        var answer = System.Text.Encoding.Latin1.GetString(CollectionsMarshal.AsSpan(payload).Slice(1));
        int ttypeCap = TtypeMaxChars();
        if (ttypeCap > 0 && answer.Length > ttypeCap)
        {
            LogSingleValueCap("TTYPE", answer.Length, ttypeCap);
            return true;
        }

        lock (collectorLock)
        {
            // No expecting gate: the reference stores TTYPE IS even when
            // unsolicited (server check only, no pending check).
            bool resendable = AppendTerminalTypeAnswerLocked(answer);
            if (resendable && TtypeCycleSolicitedLocked())
            {
                // Non-terminal answer to a live solicitation: each one
                // owes a follow-up SEND, paid by the flush after this
                // read (or, for replayed answers, by the request's own
                // drain before polling).
                ttypeResendsOwed++;
            }

            return true;
        }
    }

    /// <summary>
    /// Folds one TTYPE answer into the chain (caller holds
    /// <see cref="collectorLock"/>).
    /// </summary>
    /// <param name="answer">The decoded IS answer (possibly empty).</param>
    /// <returns>Whether the chain grew (a resendable, non-terminal answer).</returns>
    private bool AppendTerminalTypeAnswerLocked(string answer)
    {
        // Past a completed cycle, unsolicited answers are ignored; a live
        // request (expecting) always appends.
        if (!expectingTerminalType && terminalTypesCycleComplete)
        {
            return false;
        }

        int before = terminalTypeChain.Count;
        // Every answer negotiates echo (deduped at flush time): ECHO
        // waits until TTYPE reveals the client because MUD clients
        // render WILL ECHO as password mode. The answer flag releases
        // it on the next flush without waiting for advance.
        negotiateEchoPending = true;
        echoArmedByAnswer = true;
        if (answer.Length == 0)
        {
            negotiateEnvironPending = true;
            environArmedByAnswer = true;
            expectingTerminalType = false;
            terminalTypesCycleComplete = true;
            return false;
        }

        if (terminalTypeChain.Count == 0)
        {
            terminalTypeChain.Add(answer);
            if (answer != "ANSI")
            {
                // Non-Microsoft first answer: enough context to ask for
                // the environment (exact "ANSI" match, like the
                // reference — Microsoft telnet crashes on NEW_ENVIRON).
                negotiateEnvironPending = true;
                environArmedByAnswer = true;
            }

            return terminalTypeChain.Count > before;
        }

        if (terminalTypeChain.Count > TerminalTypeLoopMax)
        {
            // Past the cap: record the final answer once, then stop
            // soliciting and release the deferred environ request
            // (the reference stops at TTYPE_LOOPMAX and moves on to
            // the environment instead of waiting out the timeout).
            terminalTypeChain[^1] = answer;
            expectingTerminalType = false;
            terminalTypesCycleComplete = true;
            negotiateEnvironPending = true;
            environArmedByAnswer = true;
            return false;
        }

        bool isSecond = terminalTypeChain.Count == 1;
        terminalTypeChain.Add(answer);
        if (isSecond && !environRequested)
        {
            // Second answer: an ANSI first answer is resolved now, so
            // the deferred environ request goes out (unless already).
            negotiateEnvironPending = true;
            environArmedByAnswer = true;
        }

        if (string.Equals(answer, terminalTypeChain[0], StringComparison.Ordinal) ||
            string.Equals(answer, terminalTypeChain[^2], StringComparison.Ordinal) ||
            (terminalTypeChain.Count == 3 && answer.StartsWith("MTTS ", StringComparison.OrdinalIgnoreCase)))
        {
            // Cycle looped (first entry repeated, case-sensitive),
            // entry repeated (case-sensitive), or MTTS capability
            // vector in the third slot: done, and the cycle end also
            // releases the deferred environ request.
            expectingTerminalType = false;
            terminalTypesCycleComplete = true;
            negotiateEnvironPending = true;
            environArmedByAnswer = true;
        }

        return terminalTypeChain.Count > before;
    }

    /// <summary>
    /// Whether a follow-up TTYPE SEND is owed (caller holds
    /// <see cref="collectorLock"/>): an explicit collection is always
    /// live, while background answers only continue a cycle we opened
    /// (terminal type requested and the peer WILLed it) — an unsolicited
    /// report with no WILL is stored, never chased.
    /// </summary>
    private bool TtypeCycleSolicitedLocked()
    {
        return expectingTerminalType ||
            (Settings.RequestTerminalType && Negotiation.IsEnabledByPeer((int)Options.TerminalType));
    }

    private bool TryConsumeTerminalSpeed(List<byte> payload)
    {
        var rawText = System.Text.Encoding.Latin1.GetString(CollectionsMarshal.AsSpan(payload).Slice(1));
        int speedCap = EnvironMaxValueChars();
        if (speedCap > 0 && rawText.Length > speedCap)
        {
            LogSingleValueCap("TSPEED", rawText.Length, speedCap);
            return true;
        }

        lock (collectorLock)
        {
            // No expecting gate: the reference stores TSPEED answers
            // even when unsolicited.
            expectingTerminalSpeed = false;
            clientTerminalSpeed = TerminalSpeedProtocol.Validate(rawText);
            return true;
        }
    }

    private bool TryConsumeXDisplay(List<byte> payload)
    {
        var rawText = System.Text.Encoding.Latin1.GetString(CollectionsMarshal.AsSpan(payload).Slice(1));
        int valueCap = EnvironMaxValueChars();
        if (valueCap > 0 && rawText.Length > valueCap)
        {
            LogSingleValueCap("XDisplay", rawText.Length, valueCap);
            return true;
        }

        lock (collectorLock)
        {
            // No expecting gate: the reference stores XDISPLOC answers
            // even when unsolicited.
            expectingXDisplay = false;
            clientXDisplay = rawText;
            xdisplaySeq = ++displayArrivalSeq;
            return true;
        }
    }

    private bool TryConsumeEnvironment(int inputOption, List<byte> payload)
    {
        var isInfo = payload[0] == EnvironmentProtocol.Info;
        var isNew = inputOption == (int)Options.NewEnvironment;
        lock (collectorLock)
        {
            // No expecting gate for IS: the reference folds environ
            // answers into the store even when unsolicited (a background
            // pump may file an answer before the explicit requester
            // runs; the requesters below pick pre-stored answers up).
            // Only the WILL-ENVIRON side may send INFO. An INFO from a
            // peer that never agreed is left unconsumed so the handler
            // answers WONT, exactly like a stray IS.
            if (isInfo && !Negotiation.IsEnabledByPeer(inputOption))
            {
                return false;
            }

            if (!isInfo)
            {
                if (isNew)
                {
                    expectingNewEnvironment = false;
                }
                else
                {
                    expectingEnvironment = false;
                }
            }

            var store = isNew ? clientNewEnvironment : clientEnvironment;
            Dictionary<string, string>? batch = null;
            int maxVars = EnvironMaxVars();
            int maxValue = EnvironMaxValueChars();
            int maxKey = EnvironMaxKeyChars();
            int parseMax = maxVars > 0 ? maxVars + 16 : int.MaxValue;
            int ignoredVars = 0;
            int ignoredValues = 0;
            int ignoredKeys = 0;
            string firstIgnoredKey = string.Empty;
            foreach (var entry in EnvironmentProtocol.ParseEntries(CollectionsMarshal.AsSpan(payload), parseMax))
            {
                // Untrusted input: keys are upper-cased so a client cannot
                // override trusted mixed-case values, and empty values
                // ("no value", possibly withheld) are dropped. Matches
                // telnetlib3's on_environ.
                if (entry.Value is { Length: > 0 })
                {
                    var key = entry.Name.ToUpperInvariant();
                    if (maxKey > 0 && key.Length > maxKey)
                    {
                        ignoredKeys++;
                        firstIgnoredKey = firstIgnoredKey.Length == 0 ? key : firstIgnoredKey;
                        continue;
                    }

                    if (maxValue > 0 && entry.Value.Length > maxValue)
                    {
                        ignoredValues++;
                        firstIgnoredKey = firstIgnoredKey.Length == 0 ? key : firstIgnoredKey;
                        continue;
                    }

                    if (!store.ContainsKey(key) && maxVars > 0 && store.Count >= maxVars)
                    {
                        ignoredVars++;
                        firstIgnoredKey = firstIgnoredKey.Length == 0 ? key : firstIgnoredKey;
                        continue;
                    }

                    store[key] = entry.Value;
                    (batch ??= new Dictionary<string, string>(StringComparer.Ordinal))[key] = entry.Value;
                    if (key == EnvironmentProtocol.DisplayVariableName)
                    {
                        environDisplaySeq = ++displayArrivalSeq;
                    }
                }
            }

            if (ignoredVars + ignoredValues + ignoredKeys > 0)
            {
                LogEnvironCap($"vars={store.Count} ignoredVars={ignoredVars} ignoredValues={ignoredValues} ignoredKeys={ignoredKeys} key={SanitizeKeyForLog(firstIgnoredKey)}");
            }

            // A CHARSET entry, or a LANG entry carrying an encoding
            // suffix, presumes BINARY capability even without explicit
            // BINARY negotiation (telnetlib3's force-binary rule).
            if (batch is not null && EnvironmentProtocol.ShouldForceBinary(batch))
            {
                forceBinaryDecoding = true;
            }

            return true;
        }
    }

    /// <summary>
    /// Consumes an SNDLOC report (RFC 779, raw ASCII, no verbs), stored
    /// even when unsolicited (like every other server-role answer).
    /// </summary>
    private bool TryConsumeSendLocation(List<byte> payload)
    {
        var rawText = System.Text.Encoding.Latin1.GetString([.. payload]);
        int valueCap = EnvironMaxValueChars();
        if (valueCap > 0 && rawText.Length > valueCap)
        {
            LogSingleValueCap("Location", rawText.Length, valueCap);
            return true;
        }

        lock (collectorLock)
        {
            expectingLocation = false;
            clientLocation = rawText;
            return true;
        }
    }

    /// <summary>
    /// Consumes a CHARSET ACCEPTED/REJECTED answer (RFC 2066). An ACCEPTED
    /// is latched even when unsolicited (the reference records the peer's
    /// charset unconditionally): it becomes <see cref="ClientCharset"/>,
    /// latches BINARY-capable decoding, and switches the read encoding.
    /// REJECTED completes an outstanding request with a null charset.
    /// </summary>
    private bool TryConsumeCharset(List<byte> payload)
    {
        lock (collectorLock)
        {
            if (payload[0] != CharsetProtocol.Accepted && payload[0] != CharsetProtocol.Rejected)
            {
                return false;
            }

            if (payload[0] == CharsetProtocol.Rejected && !expectingCharset)
            {
                return false;
            }

            expectingCharset = false;
            if (payload[0] == CharsetProtocol.Accepted)
            {
                var name = System.Text.Encoding.ASCII.GetString(CollectionsMarshal.AsSpan(payload).Slice(1));
                if (name.Length == 0)
                {
                    // RFC 2066 section 2: ACCEPTED carries a charset
                    // identical to one of the requested names, so an empty
                    // name matches nothing and takes the rejection path.
                    clientCharset = null;
                    return true;
                }

                int charsetCap = EnvironMaxValueChars();
                if (charsetCap > 0 && name.Length > charsetCap)
                {
                    LogSingleValueCap("Charset", name.Length, charsetCap);
                    return true;
                }

                clientCharset = name;
                forceBinaryDecoding = true;
                try
                {
                    // Resolve through the shared canonicalizer so spellings
                    // the registry does not know directly (e.g. "CP936")
                    // still switch decoding when a numeric code page does.
                    var canonical = CharsetProtocol.CanonicalName(clientCharset);
                    charsetEncoding = canonical is null ? null : System.Text.Encoding.GetEncoding(canonical);
                }
                catch (ArgumentException)
                {
                    charsetEncoding = null;
                }
            }
            else
            {
                clientCharset = null;
            }

            return true;
        }
    }

    /// <summary>
    /// Re-applies the latched charset decoding to the running wire
    /// pass's handler, if any. A pass that consumes a CHARSET ACCEPTED
    /// mid-slice would otherwise decode bytes arriving later in the
    /// same slice with its pre-latch snapshot (the reference switches
    /// its stream reader the moment the agreement lands, so post-agree
    /// bytes in the same chunk already use the new encoding).
    /// Single-flight: at most one pass runs, so this always reaches
    /// the pass whose dispatch latched. Plain property sets, safe to
    /// call with the collector lock released (callers hold none).
    /// </summary>
    private void RefreshActiveReadHandlerEncoding()
    {
        var handler = activeReadHandler;
        if (handler is null)
        {
            return;
        }

        bool forceBinary;
        System.Text.Encoding? agreedEncoding;
        lock (collectorLock)
        {
            forceBinary = forceBinaryDecoding;
            agreedEncoding = charsetEncoding;
        }

        handler.ForceBinaryDecoding = forceBinary;
        if (agreedEncoding is not null)
        {
            handler.TextEncoding = agreedEncoding;
        }
    }

    private async Task PollForResponseAsync(Func<bool> isDone, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var end = DateTime.UtcNow.Add(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, InternalCancellation.Token);
        while (!isDone() && DateTime.UtcNow < end && !linked.Token.IsCancellationRequested)
        {
            var text = await ReadAsync(TimeSpan.FromMilliseconds(MillisecondReadDelay), linked.Token).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(text))
            {
                AppendPendingText(text);
                if (CheckBufferedTextCap())
                {
                    return;
                }
            }
        }
    }

    private Task SendFrameAsync(int option, byte[] payload, CancellationToken cancellationToken)
    {
        var frame = EnvironmentProtocol.FrameSubnegotiation(option, payload);
        return SendFrameLockedAsync(frame, cancellationToken, Context.NoteWritten);
    }

    private Task SendSbAsync(Options option, byte[] types, CancellationToken cancellationToken)
    {
        var frame = EnvironmentProtocol.FrameSubnegotiation((int)option, [EnvironmentProtocol.Send, .. types]);
        return SendFrameLockedAsync(frame, cancellationToken, Context.NoteWritten);
    }
}
