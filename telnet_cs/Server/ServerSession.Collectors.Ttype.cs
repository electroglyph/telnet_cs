namespace telnet_cs.Server;

using telnet_cs.IO;
using telnet_cs.Protocol;
using telnet_cs.Transport;

/// <summary>
/// Server-role TTYPE collection: the session asks, the peer answers. Covers the wire gate, the TTYPE requester, the deferred-negotiation flush, the MCCP2 starter, the environment pre-request and the charset auto-request. Split from <see cref="ServerSession"/> collectors; wire behavior is unchanged.
/// </summary>
public partial class ServerSession
{
    /// <summary>
    /// Serializes a request start against any in-flight background pump
    /// pass: its per-pass handler was fed before the pass ran, so answers
    /// it is still decoding must land before this request snapshots.
    /// </summary>
    private async Task<SemaphoreReleaser> AcquireWireForRequestAsync(CancellationToken cancellationToken)
    {
        await ReadRateLimit.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new SemaphoreReleaser(ReadRateLimit);
    }

    private readonly record struct SemaphoreReleaser(System.Threading.SemaphoreSlim Semaphore) : IDisposable
    {
        public void Dispose()
        {
            Semaphore.Release();
        }
    }

    /// <summary>
    /// Asks the peer for its terminal-type list (RFC 1091: <c>SEND</c>,
    /// then one <c>IS</c> per entry): an initial <c>SEND</c>, then another
    /// <c>SEND</c> after every non-terminal answer (the reference
    /// <c>request_ttype</c> per answer, sent by the read's flush so the
    /// background pump cycles identically). Answers filed before this
    /// request started are folded into the returned chain without
    /// another <c>SEND</c>: each was already chased once on arrival,
    /// and re-asking would solicit a duplicate past the real chain.
    /// The returned list ends at the
    /// first repeat: a reply equal to the first entry (cycle looped) or to
    /// the previous entry terminates the list — both compared
    /// case-sensitively, like the reference — as does an <c>MTTS</c>
    /// capability vector in the third slot; the terminating duplicate is
    /// excluded. Empty answers advance nothing (the next answer fills the
    /// same slot). The answer arriving past slot <see cref="TerminalTypeLoopMax"/>
    /// is recorded once in the overflow slot and the cycle stops there
    /// (releasing the deferred environ request), so a peer that never
    /// repeats is bounded and the last answer wins. Returns whatever arrived
    /// when the timeout elapses (possibly empty).
    /// </summary>
    /// <param name="timeout">The maximum time to wait for the full chain.</param>
    /// <param name="cancellationToken">A token to cancel the wait.</param>
    public async Task<IReadOnlyList<string>> RequestTerminalTypesAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (!Negotiation.IsEnabledByPeer((int)Options.TerminalType))
        {
            WriteLog("Cannot send SB TTYPE SEND without receipt of WILL TTYPE.");
            return [];
        }

        List<string> snapshot;
        int replayFrom;
        bool preDone;
        using (await AcquireWireForRequestAsync(cancellationToken).ConfigureAwait(false))
        {
            lock (collectorLock)
            {
                // Anything the background pump filed after the previous
                // collection is replayed below to rebuild the chain, but
                // never re-solicited: each filed answer was already chased
                // once at arrival (the reference on_ttype asks per
                // answer), so arming follow-ups again here would put a
                // second SEND per answer on the wire whenever the pump
                // consumed first.
                // preDone: the pump already finished a cycle (e.g. an empty
                // answer) — replay it without polling for more. A merely
                // partial filing (cycle still open) is not done: it is
                // replayed as solicited below and the wait continues for
                // the rest, so a slow peer can never truncate the chain.
                snapshot = [.. terminalTypeChain];
                replayFrom = terminalTypesConsumedUpTo;
                preDone = !expectingTerminalType && terminalTypesCycleComplete && snapshot.Count > replayFrom;
                terminalTypeChain.Clear();
                expectingTerminalType = true;
                // Our initial SEND covers the WILL-triggered probe too.
                ttypeProbeSent = true;
            }
        }

        await SendSbAsync(Options.TerminalType, [], cancellationToken).ConfigureAwait(false);
        bool replayedAny = false;
        for (int i = replayFrom; i < snapshot.Count; i++)
        {
            if (IsTerminalTypeDone())
            {
                break;
            }

            replayedAny = true;
            lock (collectorLock)
            {
                // Re-append only: restores the chain (and the
                // answer-driven ECHO/ENVIRON arms) without re-chasing.
                // A live peer answers each SEND once, so a replayed
                // SEND would solicit a duplicate past the real chain.
                AppendTerminalTypeAnswerLocked(snapshot[i]);
            }
        }

        // Follow-ups still owed for answers that arrived after the
        // snapshot (filed live by this request's reads or a concurrent
        // pump pass) are paid here, before polling starts.
        while (true)
        {
            bool owed;
            lock (collectorLock)
            {
                owed = ttypeResendsOwed > 0;
                if (owed)
                {
                    ttypeResendsOwed--;
                }
            }

            if (!owed)
            {
                break;
            }

            await SendSbAsync(Options.TerminalType, [], cancellationToken).ConfigureAwait(false);
        }

        if (preDone && replayedAny)
        {
            // The cycle already ended before this request started: the
            // replay above rebuilt it, nothing more to wait for. A
            // background pass that filed these answers withheld the
            // answer-triggered WILL ECHO / DO NEW_ENVIRON (unsolicited
            // cycle), so flush here — after the SENDs above — instead
            // of some later pass: the wire order stays
            // SENDs-then-ECHO/ENVIRON no matter who consumed first.
            lock (collectorLock)
            {
                expectingTerminalType = false;
            }

            await FlushDeferredNegotiationAsync(cancellationToken, backgroundPass: false).ConfigureAwait(false);
        }

        var end = DateTime.UtcNow.Add(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, InternalCancellation.Token);
        while (!IsTerminalTypeDone() && DateTime.UtcNow < end && !linked.Token.IsCancellationRequested)
        {
            // A fresh non-terminal answer owes its follow-up SEND, paid
            // by the read's flush (ttypeResendsOwed): nothing to send here.
            var text = await ReadAsync(TimeSpan.FromMilliseconds(MillisecondReadDelay), linked.Token).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(text))
            {
                AppendPendingText(text);
                if (CheckBufferedTextCap())
                {
                    // Inbound buffer cap tripped (session closing): stop
                    // polling and return what arrived, with the same
                    // cleanup and duplicate trim as the normal epilogue.
                    lock (collectorLock)
                    {
                        expectingTerminalType = false;
                        ttypeResendsOwed = 0;
                        terminalTypesConsumedUpTo = terminalTypeChain.Count;
                        terminalTypesCycleComplete = false;
                        var capped = new List<string>(terminalTypeChain);
                        if (capped.Count >= 2 &&
                            (string.Equals(capped[^1], capped[^2], StringComparison.Ordinal) ||
                              string.Equals(capped[^1], capped[0], StringComparison.Ordinal)))
                        {
                            capped.RemoveAt(capped.Count - 1);
                        }

                        return capped;
                    }
                }
            }
        }

        bool timedOut;
        if (IsTerminalTypeDone())
        {
            // Completed without polling (the pump filed the whole cycle
            // after this request snapshot it): no read of ours ran, so
            // no flush released the answer-triggered WILL ECHO / DO
            // NEW_ENVIRON the pump correctly withheld for a solicited
            // release. Flush explicitly now — after every SEND — or the
            // arms strand and the deferred request never goes out.
            await FlushDeferredNegotiationAsync(cancellationToken, backgroundPass: false).ConfigureAwait(false);
        }

        lock (collectorLock)
        {
            timedOut = expectingTerminalType;
            if (timedOut)
            {
                // Final wait over with the cycle unresolved (stall): the
                // deferred negotiations arm now but still need advance
                // to go out, like the reference check_negotiation(final=True).
                negotiateEchoPending = true;
                negotiateEnvironPending = true;
            }
        }

        if (timedOut)
        {
            await FlushDeferredNegotiationAsync(cancellationToken, backgroundPass: false).ConfigureAwait(false);
            bool warnEnviron;
            bool warnCharset;
            lock (collectorLock)
            {
                warnEnviron = expectingNewEnvironment || expectingEnvironment;
                warnCharset = expectingCharset;
            }

            if (warnEnviron || warnCharset)
            {
                WriteLog($"Waiting for critical subnegotiation: environ={warnEnviron}, charset={warnCharset}.");
            }
        }

        lock (collectorLock)
        {
            expectingTerminalType = false;
            ttypeResendsOwed = 0;
            // Watermark for the next request's replay; release the
            // cycle-complete freeze so gap answers are kept.
            terminalTypesConsumedUpTo = terminalTypeChain.Count;
            terminalTypesCycleComplete = false;
            var result = new List<string>(terminalTypeChain);
            if (result.Count >= 2 &&
                (string.Equals(result[^1], result[^2], StringComparison.Ordinal) ||
                  string.Equals(result[^1], result[0], StringComparison.Ordinal)))
            {
                // Terminating duplicate excluded: consecutive repeat, or
                // the looped repeat of the first entry (both
                // case-sensitive, like the reference).
                result.RemoveAt(result.Count - 1);
            }

            return result;
        }
    }

    /// <summary>
    /// Sends whatever deferred opening negotiation is armed: the TTYPE
    /// SEND probe once the peer WILLs TTYPE (likewise TSPEED and
    /// XDISPLOC), the advanced preset once negotiation advances, WILL
    /// ECHO (unless the peer looks like a MUD client), DO NEW_ENVIRON
    /// (skipped when the peer volunteered it), its default SB SEND once
    /// agreed, the CHARSET WILL reciprocation, the encoding check (DO
    /// BINARY / CHARSET REQUEST), and the MCCP2 start once the peer
    /// accepts outbound compression. WILL ECHO and DO NEW_ENVIRON wait
    /// for the advanced preset: a raw client that agrees to nothing gets
    /// neither. Runs after every read — and on every background-pump
    /// pass — so answers observed without a caller read take effect all
    /// the same.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the send.</param>
    /// <param name="backgroundPass">True when this flush runs on a
    /// background-pump pass rather than a caller-driven read: the
    /// answer-triggered WILL ECHO / DO NEW_ENVIRON for an unsolicited
    /// cycle (no request in flight, peer never WILLed TTYPE) are
    /// withheld — releasing them there would put them ahead of the
    /// SENDs a later request still owes, and the wire order would
    /// depend on who consumed first. The arms stay set, so an explicit
    /// read or request flush releases them after the SENDs. Solicited
    /// cycles flush as usual either way.</param>
    private async Task FlushDeferredNegotiationAsync(CancellationToken cancellationToken, bool backgroundPass)
    {
        if (Settings.DisableAllNegotiation)
        {
            return;
        }

        bool sendTtypeProbe = false;
        int sendTtypeCycleSends = 0;
        bool sendTspeedProbe = false;
        bool sendXDisplayProbe = false;
        bool reciprocateCharset = false;
        bool sendAdvanced = false;
        string? term;
        List<string> chain;
        bool wantEcho;
        bool wantEnviron;
        lock (collectorLock)
        {
            if (Negotiation.WasRefusedByPeer((int)Options.TerminalType))
            {
                // A raw client that WONTs TTYPE arms the deferred
                // negotiations, but they only go out once negotiation
                // advances (see the gate below): a refusal alone
                // earns no ECHO or NEW_ENVIRON.
                negotiateEchoPending = true;
                negotiateEnvironPending = true;
            }

            if (Negotiation.IsEnabledByPeer((int)Options.NewEnvironment))
            {
                // The peer volunteered NEW_ENVIRON: no DO goes out for
                // it (it is already agreed — the WILL got no DO reply),
                // but the default SB SEND below still owes one.
                negotiateEnvironPending = true;
            }

            if (!ttypeProbeSent && Settings.RequestTerminalType &&
                Negotiation.IsEnabledByPeer((int)Options.TerminalType))
            {
                // The peer WILLed TTYPE: ask for the first report (the
                // reference request_ttype on WILL). An explicit
                // RequestTerminalTypesAsync in flight covers its own
                // probe, so it latches this off (see there).
                ttypeProbeSent = true;
                sendTtypeProbe = true;
            }

            if (!tspeedProbeSent && Settings.RequestTerminalSpeed &&
                Negotiation.IsEnabledByPeer((int)Options.TerminalSpeed))
            {
                // Same WILL-triggered probe for the terminal speed.
                tspeedProbeSent = true;
                sendTspeedProbe = true;
            }

            if (!xdisplayProbeSent && Settings.RequestXDisplay &&
                Negotiation.IsEnabledByPeer((int)Options.XDisplay))
            {
                // Same WILL-triggered probe for the X display location.
                xdisplayProbeSent = true;
                sendXDisplayProbe = true;
            }

            var (charsetUs, _) = Negotiation[(int)Options.CharacterSet];
            reciprocateCharset = Settings.RequestCharacterSet &&
                Negotiation.IsEnabledByPeer((int)Options.CharacterSet) &&
                charsetUs == NegotiationState.SideState.No &&
                !Negotiation.WasRefusedByUs((int)Options.CharacterSet);

            if (!advancedNegotiationSent && ShouldBeginAdvancedNegotiation())
            {
                advancedNegotiationSent = true;
                sendAdvanced = true;
            }

            if (ttypeResendsOwed > 0)
            {
                // One SEND per answer (telnetlib3 request_ttype parity):
                // drain every owed follow-up, not just one per flush, so
                // answers batched in a single pass match back-to-back
                // ones on the wire. Owed implies solicited at arm time,
                // so no gate here: a cycle that completed mid-pass still
                // owes the SENDs its answers earned.
                sendTtypeCycleSends = ttypeResendsOwed;
                ttypeResendsOwed = 0;
            }

            // A background pass withholds the answer-triggered release
            // for an unsolicited cycle (see the parameter doc); the
            // arms stay set for a later explicit flush.
            bool answerRelease = !backgroundPass || TtypeCycleSolicitedLocked();
            wantEcho = (advancedNegotiationSent || (echoArmedByAnswer && answerRelease)) && negotiateEchoPending && !echoNegotiated
                // A linemode server stays in NVT line mode (reference
                // _negotiate_echo returns early when line_mode is set),
                // so ECHO is never offered. A game-driven manual call
                // stands the auto-offer down for the rest of the
                // session (see manualEcho): the gate lives here, not
                // at the OfferEcho/MUD send site below, so the one-shot
                // echoNegotiated latch is never consumed while
                // withholding the WILL.
                && !Settings.RequestLinemode
                && manualEcho is null;
            wantEnviron = (advancedNegotiationSent || (environArmedByAnswer && answerRelease)) && negotiateEnvironPending && !environRequested;
            if (wantEcho)
            {
                echoNegotiated = true;
                echoArmedByAnswer = false;
            }

            if (wantEnviron)
            {
                environRequested = true;
                environArmedByAnswer = false;
            }

            if (advancedNegotiationSent)
            {
                // Consumed: a raw client that never advances keeps its
                // pending intent armed until something is affirmatively
                // agreed, exactly like the deferred ECHO/ENVIRON below.
                negotiateEchoPending = false;
                negotiateEnvironPending = false;
                echoArmedByAnswer = false;
                environArmedByAnswer = false;
            }
            chain = [.. terminalTypeChain];
            term = chain.Count >= 3 && chain[2].StartsWith("MTTS ", StringComparison.OrdinalIgnoreCase)
                ? chain[1]
                : chain.Count > 0 ? chain[^1] : null;
        }

        if (sendTtypeProbe)
        {
            await SendSbAsync(Options.TerminalType, [], cancellationToken).ConfigureAwait(false);
            if (sendTtypeCycleSends > 0)
            {
                // The probe doubles as one follow-up.
                sendTtypeCycleSends--;
            }
        }

        while (sendTtypeCycleSends > 0)
        {
            sendTtypeCycleSends--;
            await SendSbAsync(Options.TerminalType, [], cancellationToken).ConfigureAwait(false);
        }

        if (sendTspeedProbe)
        {
            await SendSbAsync(Options.TerminalSpeed, [], cancellationToken).ConfigureAwait(false);
        }

        if (sendXDisplayProbe)
        {
            await SendSbAsync(Options.XDisplay, [], cancellationToken).ConfigureAwait(false);
        }

        if (sendAdvanced)
        {
            await BeginAdvancedNegotiationAsync(cancellationToken).ConfigureAwait(false);
        }

        if (wantEcho && Settings.OfferEcho &&
            !MudClientDetector.IsMudClient(term, chain, o => Negotiation.IsEnabledByPeer(o)))
        {
            await OfferEnableAsync(Options.Echo, cancellationToken).ConfigureAwait(false);
        }

        if (wantEnviron && Settings.RequestNewEnvironment &&
            !Negotiation.IsEnabledByPeer((int)Options.NewEnvironment))
        {
            // A volunteered NEW_ENVIRON needs no DO (see the arming
            // above); only ask when the peer has not offered.
            await RequestEnableAsync(Options.NewEnvironment, cancellationToken).ConfigureAwait(false);
        }

        if (reciprocateCharset)
        {
            // The peer WILLed CHARSET (its DO reply doubles as the
            // state change, so no DO went out for it): answer with our
            // own WILL, which the encoding check below needs on our
            // side before it fires the auto-REQUEST.
            await OfferEnableAsync(Options.CharacterSet, cancellationToken).ConfigureAwait(false);
        }

        await MaybeSendEnvironmentRequestAsync(cancellationToken).ConfigureAwait(false);
        await CheckEncodingAsync(cancellationToken).ConfigureAwait(false);
        await MaybeStartMccp2Async(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Starts outbound MCCP2 compression once the peer accepts our WILL
    /// MCCP2: the empty SB start marker goes out raw, and only then is
    /// the compressing write view published, so every writer is either
    /// fully before the marker (raw) or fully after it (compressed).
    /// Strict: once started the view runs to disconnect — the reference
    /// never stops its compressor, not even on WONT/DONT — so a live
    /// view also covers re-agreement and is never replaced.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the send.</param>
    private async Task MaybeStartMccp2Async(CancellationToken cancellationToken)
    {
        lock (mccpFilterGate)
        {
            if (mccp2Filter is not null || !Negotiation.IsEnabledByUs((int)Options.Mccp2))
            {
                return;
            }
        }

        var frame = EnvironmentProtocol.FrameSubnegotiation((int)Options.Mccp2, []);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, InternalCancellation.Token);
        if (!ByteStream.Connected || linked.Token.IsCancellationRequested)
        {
            return;
        }

        // Stays on the manual throttle pattern instead of the shared
        // frame gate on purpose: the recheck, the raw marker write, and
        // the filter publish must all happen while holding the gate, so a
        // second thread can never slip a duplicate start marker (and a
        // second compressor) onto the wire between the write and the
        // publish. The marker also bypasses WriteStream on purpose: it
        // must go out raw, before the compressing view exists.
        await SendRateLimit.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            lock (mccpFilterGate)
            {
                if (mccp2Filter is not null || !Negotiation.IsEnabledByUs((int)Options.Mccp2))
                {
                    return;
                }
            }

            await ByteStream.WriteAsync(frame, 0, frame.Length, linked.Token).ConfigureAwait(false);
            Context.NoteWritten(frame.Length);
            lock (mccpFilterGate)
            {
                if (mccp2Filter is not null)
                {
                    return;
                }

                mccp2Filter = new MccpWriteFilter(ByteStream, new MccpCompressor());
            }
        }
        finally
        {
            SendRateLimit.Release();
        }
    }

    /// <summary>
    /// Sends the deferred NEW_ENVIRON default SB SEND once the peer WILLs
    /// the option (RFC 855: no subnegotiation before agreement). Fires
    /// once; an explicit <c>RequestNewEnvironmentAsync</c> in flight
    /// suppresses it. Arms the answer latch so the IS lands in
    /// <c>ClientEnvironment</c>.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the send.</param>
    private async Task MaybeSendEnvironmentRequestAsync(CancellationToken cancellationToken)
    {
        string? ttype1;
        string? ttype2;
        bool send;
        lock (collectorLock)
        {
            send = environRequested && !environSent && !expectingNewEnvironment &&
                Negotiation.IsEnabledByPeer((int)Options.NewEnvironment);
            ttype1 = terminalTypeChain.Count > 0 ? terminalTypeChain[0] : null;
            ttype2 = terminalTypeChain.Count > 1 ? terminalTypeChain[1] : null;
            if (send)
            {
                environSent = true;
                expectingNewEnvironment = true;
            }
        }

        if (send)
        {
            await SendSbAsync(Options.NewEnvironment, EnvironmentProtocol.BuildDefaultSendRequest(ttype1, ttype2), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The periodic encoding check (reference <c>_check_encoding</c>):
    /// once we send binary, also ask for the inbound direction; once
    /// CHARSET is agreed both ways, fire one auto REQUEST.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the send.</param>
    private Task CheckEncodingAsync(CancellationToken cancellationToken)
    {
        if (Negotiation.IsEnabledByUs((int)Options.TransmitBinary) &&
            !Negotiation.IsEnabledByPeer((int)Options.TransmitBinary) &&
            !Negotiation.WasRefusedByPeer((int)Options.TransmitBinary))
        {
            return CheckEncodingWithBinaryRequestAsync(cancellationToken);
        }

        MaybeLaunchCharsetAutoRequest();
        return Task.CompletedTask;
    }

    private async Task CheckEncodingWithBinaryRequestAsync(CancellationToken cancellationToken)
    {
        // Our binary offer was accepted outbound: ask for the inbound
        // direction too. The 1143 machine dedupes while negotiating.
        await RequestEnableAsync(Options.TransmitBinary, cancellationToken).ConfigureAwait(false);
        MaybeLaunchCharsetAutoRequest();
    }

    private void MaybeLaunchCharsetAutoRequest()
    {
        bool launch;
        lock (collectorLock)
        {
            launch = Settings.RequestCharacterSet && !charsetAutoRequested && !expectingCharset &&
                Negotiation.IsEnabledByPeer((int)Options.CharacterSet) &&
                Negotiation.IsEnabledByUs((int)Options.CharacterSet);
            if (launch)
            {
                charsetAutoRequested = true;
            }
        }

        if (launch)
        {
            // Background poll: answers latch via TryConsumeCharset, and the
            // wait ends early on an answer or on session teardown. Never
            // throws out of the read path.
            _ = AutoCharsetRequestAsync();
        }
    }

    private async Task AutoCharsetRequestAsync()
    {
        bool proceed;
        lock (collectorLock)
        {
            // Re-check under the lock: an explicit request (or an answer)
            // may have landed since the flush armed this.
            proceed = !expectingCharset && clientCharset is null;
            if (proceed)
            {
                expectingCharset = true;
            }
        }

        if (!proceed)
        {
            return;
        }

        try
        {
            await SendFrameAsync((int)Options.CharacterSet, CharsetProtocol.BuildRequest(Settings.CharsetOffers), InternalCancellation.Token).ConfigureAwait(false);
            await PollForResponseAsync(IsCharsetDone, TimeSpan.FromSeconds(5), InternalCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            WriteLog($"Charset auto-request failed: {ex.Message}");
        }
        finally
        {
            lock (collectorLock)
            {
                expectingCharset = false;
            }
        }
    }
}
