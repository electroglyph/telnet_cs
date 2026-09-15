namespace telnet_cs.Server
{
    using telnet_cs.IO;
    using telnet_cs.Protocol;
    using telnet_cs.Transport;

    /// <summary>
    /// Server-role subnegotiation requesters (S3): the session asks, the peer
    /// answers. The <see cref="ByteStreamHandler"/> routes inbound IS/INFO (and
    /// inbound NAWS) payloads to <see cref="OnSubnegotiationResponse"/>; each
    /// requester sends its SEND, then polls reads until its collector is
    /// satisfied or the timeout elapses. TTYPE IS is stored even when
    /// unsolicited (the reference records it with no pending check).
    /// </summary>
    public partial class ServerSession
    {
        // Guards the expecting-flags, chains, and collected values below. The
        // collectors assume single-threaded session use: concurrent
        // Request*Async calls would overwrite each other's expecting-flags and
        // mix the chains (no reentrancy protection by design).
        private readonly Lock collectorLock = new();
        // Session-owned negotiation storm guard (fixed 100 inbound neg
        // frames/s window): one instance shared by every per-read handler
        // so the count survives across reads.
        private readonly NegotiationStormGuard stormGuard = new();
        private bool expectingTerminalType;
        private bool expectingTerminalSpeed;
        private bool expectingEnvironment;
        private bool expectingNewEnvironment;
        private bool expectingXDisplay;
        private bool expectingCharset;
        private bool expectingLocation;
        private readonly List<string> terminalTypeChain = [];
        // Answers before this index were consumed by an earlier collection;
        // a new request only replays what arrived after it.
        private int terminalTypesConsumedUpTo;
        // A completed TTYPE cycle freezes unsolicited appends (answers past
        // the end of a cycle are ignored, like a live request that already
        // stopped polling). Request end releases it, so answers arriving in
        // the gap between requests are still kept for the next collection.
        private bool terminalTypesCycleComplete;
        // Deferred opening negotiation (mirroring the reference): WILL ECHO
        // and DO NEW_ENVIRON leave with the preset only as intent. The TTYPE
        // answers arm them via the pending flags and release them on the
        // next flush even before anything is agreed (the reference on_ttype
        // path, which negotiates echo/environ per answer); the refusal and
        // collection-timeout arming below only releases once negotiation
        // advances (the reference check_negotiation gate). echoNegotiated /
        // environRequested latch so each fires once. advancedNegotiationSent
        // latches the WILL SGA / WILL BINARY / DO NAWS / DO CHARSET advanced
        // preset; ttypeProbeSent latches the SB TTYPE SEND probe on WILL
        // TTYPE (an explicit collection covers its own probe and latches
        // this too).
        private bool advancedNegotiationSent;
        private bool ttypeProbeSent;
        // Like the TTYPE probe above, but for the options the session asks
        // about without a public requester: SB TSPEED SEND on WILL TSPEED and
        // SB XDISPLOC SEND on WILL XDISPLOC, each latched to fire once.
        private bool tspeedProbeSent;
        private bool xdisplayProbeSent;
        // A non-terminal TTYPE answer owed a follow-up SEND (the reference
        // request_ttype per answer): counted when solicited answers arrive
        // and drained by the flush after the read, so background and
        // explicit collection cycle identically with one SEND per answer no
        // matter who consumed first or how answers batch across passes.
        private int ttypeResendsOwed;
        private bool echoNegotiated;
        private bool environRequested;
        private bool negotiateEchoPending;
        private bool negotiateEnvironPending;
        // Answer-driven release for the flags above: set alongside them when
        // a TTYPE answer arrives, so the flush sends the offer without
        // waiting for the advanced preset. Cleared once consumed.
        private bool echoArmedByAnswer;
        private bool environArmedByAnswer;
        // Set once the deferred DO NEW_ENVIRON is agreed and its default SB
        // SEND went out; the charset auto-REQUEST likewise fires once.
        private bool environSent;
        private bool charsetAutoRequested;
        private string? clientTerminalSpeed;
        private string? clientXDisplay;
        private string? clientCharset;
        private string? clientLocation;
        private readonly Dictionary<string, string> clientEnvironment = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> clientNewEnvironment = new(StringComparer.Ordinal);
        private bool forceBinaryDecoding;
        private System.Text.Encoding? charsetEncoding;
        // The handler of the currently running wire pass, if any (set and
        // cleared by ReadWireOnceAsync). At most one pass runs at a time,
        // so a subnegotiation callback always belongs to this handler and
        // a mid-pass latch can refresh its decoding immediately.
        private ByteStreamHandler? activeReadHandler;
        // MCCP agreement survives across per-read handlers: the arming SB,
        // stream end, and corrupt shutdown report through MccpStateChanged.
        private bool mccp2Agreed;
        private bool mccp3Agreed;
        private MccpDecompressor? mccpStream;
        // Subnegotiation continuation stashed by the last read, fed into the
        // next per-read handler (telnetlib3 _sb_buffer parity).
        private (int Option, byte[] Payload, bool OverCap, bool SePending, bool IacPending, bool HeaderIacPending)? sbResumeState;
        private (bool PendingIac, int? PendingVerb, bool SawCr, int? Pushback) framingState;
        // MUD stores survive across per-read handlers: append collections are
        // injected into each handler, and the replaced MSSP mapping is
        // captured through the MSSP hook.
        private IReadOnlyDictionary<string, object>? mudMsspData;
        private readonly List<byte[]> mudMspData = [];
        private readonly List<byte[]> mudMxpData = [];
        private readonly Dictionary<string, IReadOnlyList<string>> mudZmpData = new(StringComparer.Ordinal);
        private readonly List<AardwolfMessage> mudAardwolfData = [];
        private readonly List<(string Package, string Value)> mudAtcpData = [];
        private (ushort Width, ushort Height)? clientWindowSize;
        // Last parsed STATUS IS report (RFC 859): verb/option pairs and SB
        // blocks, recorded for display only — never fed back into the
        // Q-machine.
        private IReadOnlyList<StatusReportItem>? peerStatusReport;
        // Arrival order shared by option-35 IS and ENVIRON DISPLAY writes, so
        // the effective display resolves last-arrived-wins (RFC 1408 §5). Zero
        // means "never arrived" on both sides.
        private long displayArrivalSeq;
        private long xdisplaySeq;
        private long environDisplaySeq;

        /// <summary>
        /// Gets the terminal types reported by the peer (RFC 1091), most
        /// specific first. Populated by <see cref="RequestTerminalTypesAsync"/>;
        /// empty until the first answer arrives.
        /// </summary>
        public IReadOnlyList<string> ClientTerminalTypes
        {
            get
            {
                lock (collectorLock)
                {
                    return [.. terminalTypeChain];
                }
            }
        }

        /// <summary>
        /// Gets the normalized terminal speed reported by the peer
        /// (<c>"&lt;tx&gt;,&lt;rx&gt;"</c>, RFC 1079), or null when nothing
        /// usable arrived (no answer yet, or a malformed IS).
        /// </summary>
        public string? ClientTerminalSpeed
        {
            get
            {
                lock (collectorLock)
                {
                    return clientTerminalSpeed;
                }
            }
        }

        /// <summary>
        /// Gets the environment variables reported by the peer (RFC 1408),
        /// from requested IS answers and spontaneous INFO updates. A variable
        /// sent without VALUE is undefined and omitted; on a VAR/USERVAR name
        /// collision the later entry wins.
        /// </summary>
        public IReadOnlyDictionary<string, string> ClientEnvironment
        {
            get
            {
                lock (collectorLock)
                {
                    return new Dictionary<string, string>(clientEnvironment, StringComparer.Ordinal);
                }
            }
        }

        /// <summary>
        /// Gets the environment variables reported by the peer in new form
        /// (RFC 1572), from requested IS answers and spontaneous INFO updates.
        /// Same shape as <see cref="ClientEnvironment"/>, kept separate so a
        /// peer reporting both forms never mixes them.
        /// </summary>
        public IReadOnlyDictionary<string, string> ClientNewEnvironment
        {
            get
            {
                lock (collectorLock)
                {
                    return new Dictionary<string, string>(clientNewEnvironment, StringComparer.Ordinal);
                }
            }
        }

        /// <summary>
        /// Gets the character set agreed via CHARSET ACCEPTED (RFC 2066), or
        /// null when none was negotiated yet. Populated by
        /// <see cref="RequestCharsetAsync"/>.
        /// </summary>
        public string? ClientCharset
        {
            get
            {
                lock (collectorLock)
                {
                    return clientCharset;
                }
            }
        }

        /// <summary>
        /// Reports whether our own CHARSET REQUEST is outstanding (sent, not
        /// yet answered). Fed into each per-read handler so a simultaneous
        /// inbound REQUEST is answered REJECTED (RFC 2066 §5).
        /// </summary>
        internal bool IsCharsetOutstanding
        {
            get
            {
                lock (collectorLock)
                {
                    return expectingCharset;
                }
            }
        }

        /// <summary>
        /// Gets the location reported by the peer via SNDLOC (RFC 779), or
        /// null when none arrived. Populated by
        /// <see cref="RequestSendLocationAsync"/>.
        /// </summary>
        public string? ClientLocation
        {
            get
            {
                lock (collectorLock)
                {
                    return clientLocation;
                }
            }
        }

        /// <summary>
        /// Gets the last MSSP variables reported by the peer, or null when
        /// none arrived yet. Each MSSP subnegotiation replaces the mapping.
        /// </summary>
        public IReadOnlyDictionary<string, object>? MsspData
        {
            get
            {
                lock (collectorLock)
                {
                    return mudMsspData;
                }
            }
        }

        /// <summary>
        /// Gets the raw MSP payloads received so far, in arrival order.
        /// </summary>
        public IReadOnlyList<byte[]> MspData
        {
            get
            {
                lock (collectorLock)
                {
                    return [.. mudMspData];
                }
            }
        }

        /// <summary>
        /// Gets the raw MXP payloads received so far, in arrival order.
        /// </summary>
        public IReadOnlyList<byte[]> MxpData
        {
            get
            {
                lock (collectorLock)
                {
                    return [.. mudMxpData];
                }
            }
        }

        /// <summary>
        /// Gets the latest ZMP arguments by command; each command slot holds
        /// its most recent message.
        /// </summary>
        public IReadOnlyDictionary<string, IReadOnlyList<string>> ZmpData
        {
            get
            {
                lock (collectorLock)
                {
                    return new Dictionary<string, IReadOnlyList<string>>(mudZmpData, StringComparer.Ordinal);
                }
            }
        }

        /// <summary>
        /// Gets the decoded Aardwolf messages received so far, in arrival
        /// order.
        /// </summary>
        public IReadOnlyList<AardwolfMessage> AardwolfData
        {
            get
            {
                lock (collectorLock)
                {
                    return [.. mudAardwolfData];
                }
            }
        }

        /// <summary>
        /// Gets the decoded ATCP <c>(package, value)</c> pairs received so
        /// far, in arrival order.
        /// </summary>
        public IReadOnlyList<(string Package, string Value)> AtcpData
        {
            get
            {
                lock (collectorLock)
                {
                    return [.. mudAtcpData];
                }
            }
        }

        /// <summary>
        /// Maximum terminal-type answers stored in distinct slots, mirroring
        /// telnetlib3's <c>TTYPE_LOOPMAX</c>: the answer arriving past slot 8
        /// is recorded once in the overflow slot (<c>ttype9</c>) then the
        /// cycle stops and the deferred environ request is released, so the
        /// last answer wins there without waiting out the timeout.
        /// </summary>
        internal const int TerminalTypeLoopMax = 8;

        /// <summary>
        /// Gets the effective terminal type: when the reported chain reaches
        /// a third entry starting with <c>"MTTS "</c> (MUD Terminal Type
        /// Standard), the second entry names the real terminal and the MTTS
        /// bitmask only lists client capabilities — so the second entry wins.
        /// Otherwise the last entry wins (the most recently reported type is
        /// assumed current), or null when nothing arrived.
        /// </summary>
        public string? ClientEffectiveTerminalType
        {
            get
            {
                lock (collectorLock)
                {
                    if (terminalTypeChain.Count >= 3 &&
                        terminalTypeChain[2].StartsWith("MTTS ", StringComparison.OrdinalIgnoreCase))
                    {
                        return terminalTypeChain[1];
                    }

                    return terminalTypeChain.Count > 0 ? terminalTypeChain[^1] : null;
                }
            }
        }

        /// <summary>
        /// Gets the X display reported by the peer via option 35 (RFC 1096), or
        /// null when none arrived yet. Stored even when unsolicited, like
        /// every other server-role answer.
        /// </summary>
        public string? ClientXDisplay
        {
            get
            {
                lock (collectorLock)
                {
                    return clientXDisplay;
                }
            }
        }

        /// <summary>
        /// Gets the effective display: the last-arrived of the option-35 X
        /// display and the ENVIRON DISPLAY variable (RFC 1408 §5 recency rule),
        /// or null when neither arrived. Kept out of
        /// <see cref="ClientEnvironment"/>, which holds ENVIRON vars only.
        /// </summary>
        public string? ClientEffectiveDisplay
        {
            get
            {
                lock (collectorLock)
                {
                    if (xdisplaySeq > 0 && xdisplaySeq >= environDisplaySeq && clientXDisplay is not null)
                    {
                        return clientXDisplay;
                    }

                    return clientEnvironment.TryGetValue(EnvironmentProtocol.DisplayVariableName, out string? display) ? display : null;
                }
            }
        }

        /// <summary>
        /// Gets the last terminal size reported by the peer (RFC 1073), or null
        /// when the peer never sent NAWS. Updated whenever a NAWS report
        /// arrives; the peer volunteers these, so there is no request method.
        /// </summary>
        public (ushort Width, ushort Height)? ClientWindowSize
        {
            get
            {
                lock (collectorLock)
                {
                    return clientWindowSize;
                }
            }
        }

        /// <summary>
        /// One parsed STATUS IS item (RFC 859): a <c>WILL</c>/<c>WONT</c>/
        /// <c>DO</c>/<c>DONT</c> option pair (<c>Data</c> null), or an
        /// <c>SB &lt;opt&gt; &lt;data&gt; SE</c> block (<c>Verb</c> is
        /// <c>Subnegotiation</c>).
        /// </summary>
        /// <param name="Verb">The item verb.</param>
        /// <param name="Option">The option byte.</param>
        /// <param name="Data">The SB block bytes, or null for verb pairs.</param>
        public readonly record struct StatusReportItem(Commands Verb, byte Option, byte[]? Data);

        /// <summary>
        /// Gets the last STATUS IS report from the peer (RFC 859), or null
        /// when none arrived. Recorded for display only: arriving reports
        /// never affect negotiation state.
        /// </summary>
        public IReadOnlyList<StatusReportItem>? PeerStatusReport
        {
            get
            {
                lock (collectorLock)
                {
                    return peerStatusReport is null ? null : [.. peerStatusReport];
                }
            }
        }

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
        /// background pump cycles identically). The returned list ends at the
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
            List<string> snapshot;
            int replayFrom;
            bool preDone;
            using (await AcquireWireForRequestAsync(cancellationToken).ConfigureAwait(false))
            {
                lock (collectorLock)
                {
                    // Anything the background pump filed after the previous
                    // collection is replayed below as solicited (re-asked):
                    // each replayed answer owes its follow-up SEND exactly
                    // like a live one, so the wire count is deterministic no
                    // matter who consumed first.
                    // preDone: the pump already finished a cycle (e.g. an empty
                    // answer) — replay it without polling for more.
                    snapshot = [.. terminalTypeChain];
                    replayFrom = terminalTypesConsumedUpTo;
                    preDone = !expectingTerminalType && snapshot.Count > replayFrom;
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
                    // Replayed answers re-ask exactly like live ones
                    // (telnetlib3 sends one SEND per answer no matter who
                    // consumed it): each grown answer owes its follow-up,
                    // drained below before polling starts.
                    bool grown = AppendTerminalTypeAnswerLocked(snapshot[i]);
                    if (grown && TtypeCycleSolicitedLocked())
                    {
                        ttypeResendsOwed++;
                    }
                }
            }

            // Answers the pump filed ahead of this request still owe their
            // per-answer follow-ups: emit them here (not in some later
            // flush) so the count is fixed before polling starts.
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
                // replay above re-asked it, nothing more to wait for.
                lock (collectorLock)
                {
                    expectingTerminalType = false;
                }
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
                    PendingText += text;
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
                await FlushDeferredNegotiationAsync(cancellationToken).ConfigureAwait(false);
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
        private async Task FlushDeferredNegotiationAsync(CancellationToken cancellationToken)
        {
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

                var (charsetUs, _) = Negotiation.GetStates((int)Options.CharacterSet);
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

                wantEcho = (advancedNegotiationSent || echoArmedByAnswer) && negotiateEchoPending && !echoNegotiated
                    // A linemode server stays in NVT line mode (reference
                    // _negotiate_echo returns early when line_mode is set),
                    // so ECHO is never offered.
                    && !Settings.RequestLinemode;
                wantEnviron = (advancedNegotiationSent || environArmedByAnswer) && negotiateEnvironPending && !environRequested;
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
                WriteLog("Charset auto-request failed: " + ex.Message);
            }
            finally
            {
                lock (collectorLock)
                {
                    expectingCharset = false;
                }
            }
        }

        /// <summary>
        /// Asks the peer for its terminal speed (RFC 1079: <c>SEND</c>, one
        /// <c>IS</c>). Returns the verbatim validated <c>"&lt;tx&gt;,&lt;rx&gt;"</c>
        /// shape, or null on timeout, on a malformed answer, or when a previous
        /// request is still outstanding (single-active rule, like the reference
        /// <c>request_tspeed</c> returning false while pending).
        /// </summary>
        /// <param name="timeout">The maximum time to wait for the answer.</param>
        /// <param name="cancellationToken">A token to cancel the wait.</param>
        public async Task<string?> RequestTerminalSpeedAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            bool preStored;
            using (await AcquireWireForRequestAsync(cancellationToken).ConfigureAwait(false))
            {
                lock (collectorLock)
                {
                    if (expectingTerminalSpeed)
                    {
                        return null;
                    }

                    // An answer the pump filed before this request started
                    // satisfies it: still SEND (the peer answers again, filed
                    // unsolicited), but return the known value without waiting.
                    preStored = clientTerminalSpeed is not null;
                    if (!preStored)
                    {
                        clientTerminalSpeed = null;
                        expectingTerminalSpeed = true;
                    }
                }
            }

            await SendSbAsync(Options.TerminalSpeed, [], cancellationToken).ConfigureAwait(false);
            if (preStored)
            {
                lock (collectorLock)
                {
                    return clientTerminalSpeed;
                }
            }

            await PollForResponseAsync(IsTerminalSpeedDone, timeout, cancellationToken).ConfigureAwait(false);
            lock (collectorLock)
            {
                expectingTerminalSpeed = false;
                return clientTerminalSpeed;
            }
        }

        /// <summary>
        /// Asks the peer for its X display location (RFC 1096: <c>SEND</c>, one
        /// <c>IS</c>). Returns the display string, or null on timeout, on a
        /// malformed answer, or when a previous request is still outstanding
        /// (single-active rule, like <see cref="RequestTerminalSpeedAsync"/>:
        /// overlapping calls share the in-flight SEND). Also feeds the
        /// effective-display recency rule (see
        /// <see cref="ClientEffectiveDisplay"/>).
        /// </summary>
        /// <param name="timeout">The maximum time to wait for the answer.</param>
        /// <param name="cancellationToken">A token to cancel the wait.</param>
        public async Task<string?> RequestXDisplayAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            bool preStored;
            using (await AcquireWireForRequestAsync(cancellationToken).ConfigureAwait(false))
            {
                lock (collectorLock)
                {
                    if (expectingXDisplay)
                    {
                        return null;
                    }

                    // See RequestTerminalSpeedAsync: a pre-stored answer is
                    // returned after the SEND without waiting.
                    preStored = clientXDisplay is not null;
                    if (!preStored)
                    {
                        clientXDisplay = null;
                        expectingXDisplay = true;
                    }
                }
            }

            await SendSbAsync(Options.XDisplay, [], cancellationToken).ConfigureAwait(false);
            if (preStored)
            {
                lock (collectorLock)
                {
                    return clientXDisplay;
                }
            }

            await PollForResponseAsync(IsXDisplayDone, timeout, cancellationToken).ConfigureAwait(false);
            lock (collectorLock)
            {
                expectingXDisplay = false;
                return clientXDisplay;
            }
        }

        /// <summary>
        /// Asks the peer for environment variables (RFC 1408: <c>SEND</c>, one
        /// <c>IS</c>). Later spontaneous INFO updates also land in
        /// <see cref="ClientEnvironment"/>. Returns the variables known when
        /// the answer arrives or the timeout elapses.
        /// Long type lists are split into size-limited SB frames (at most
        /// <see cref="MaxEnvironBatchBytes"/> type bytes each) so peers with
        /// small subnegotiation buffers are not overflowed.
        /// </summary>
        /// <param name="timeout">The maximum time to wait for the answer.</param>
        /// <param name="types">The requested type bytes (VAR/USERVAR); null or
        /// empty requests the defaults (well-known variables, then user
        /// variables).</param>
        /// <param name="cancellationToken">A token to cancel the wait.</param>
        public async Task<IReadOnlyDictionary<string, string>> RequestEnvironmentAsync(TimeSpan timeout, byte[]? types = null, CancellationToken cancellationToken = default)
        {
            bool preSatisfied;
            using (await AcquireWireForRequestAsync(cancellationToken).ConfigureAwait(false))
            {
                lock (collectorLock)
                {
                    // Answers the pump filed before this request started satisfy
                    // it (like every other requester): SEND anyway, skip the wait.
                    preSatisfied = clientEnvironment.Count > 0;
                    expectingEnvironment = true;
                }
            }

            await SendEnvironmentBatchesAsync(Options.OldEnvironment, types ?? [], cancellationToken).ConfigureAwait(false);
            if (!preSatisfied)
            {
                await PollForResponseAsync(IsEnvironmentDone, timeout, cancellationToken).ConfigureAwait(false);
            }

            lock (collectorLock)
            {
                expectingEnvironment = false;
                return new Dictionary<string, string>(clientEnvironment, StringComparer.Ordinal);
            }
        }

        /// <summary>
        /// Asks the peer for environment variables in new form (RFC 1572:
        /// <c>SEND</c>, one <c>IS</c>). Later spontaneous INFO updates also
        /// land in <see cref="ClientNewEnvironment"/>. Returns the variables
        /// known when the answer arrives or the timeout elapses.
        /// Long type lists are split like <see cref="RequestEnvironmentAsync"/>.
        /// </summary>
        /// <param name="timeout">The maximum time to wait for the answer.</param>
        /// <param name="types">The requested type bytes (VAR/USERVAR); null or
        /// empty requests the defaults (well-known variables, then user
        /// variables).</param>
        /// <param name="cancellationToken">A token to cancel the wait.</param>
        public async Task<IReadOnlyDictionary<string, string>> RequestNewEnvironmentAsync(TimeSpan timeout, byte[]? types = null, CancellationToken cancellationToken = default)
        {
            bool preSatisfied;
            using (await AcquireWireForRequestAsync(cancellationToken).ConfigureAwait(false))
            {
                lock (collectorLock)
                {
                    preSatisfied = clientNewEnvironment.Count > 0;
                    expectingNewEnvironment = true;
                }
            }

            await SendEnvironmentBatchesAsync(Options.NewEnvironment, types ?? [], cancellationToken).ConfigureAwait(false);
            if (!preSatisfied)
            {
                await PollForResponseAsync(IsNewEnvironmentDone, timeout, cancellationToken).ConfigureAwait(false);
            }

            lock (collectorLock)
            {
                expectingNewEnvironment = false;
                return new Dictionary<string, string>(clientNewEnvironment, StringComparer.Ordinal);
            }
        }

        /// <summary>
        /// Maximum type bytes per ENVIRON SEND frame: with the option, SEND,
        /// and IAC SB/SE framing the wire frame stays within the 240-byte SB
        /// payload budget small-buffer peers (e.g. GNU inetutils telnet)
        /// tolerate.
        /// </summary>
        private const int MaxEnvironBatchBytes = 238;

        private async Task SendEnvironmentBatchesAsync(Options option, byte[] types, CancellationToken cancellationToken)
        {
            if (types.Length == 0)
            {
                await SendSbAsync(option, [], cancellationToken).ConfigureAwait(false);
                return;
            }

            for (var offset = 0; offset < types.Length; offset += MaxEnvironBatchBytes)
            {
                var length = Math.Min(MaxEnvironBatchBytes, types.Length - offset);
                await SendSbAsync(option, types[offset..(offset + length)], cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Asks the peer for a character set (RFC 2066: <c>REQUEST</c>, one
        /// <c>ACCEPTED</c>/<c>REJECTED</c>). Returns the accepted name, or null
        /// on timeout or rejection.
        /// </summary>
        /// <param name="timeout">The maximum time to wait for the answer.</param>
        /// <param name="cancellationToken">A token to cancel the wait.</param>
        public async Task<string?> RequestCharsetAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            bool preStored;
            using (await AcquireWireForRequestAsync(cancellationToken).ConfigureAwait(false))
            {
                lock (collectorLock)
                {
                    // An ACCEPTED the pump latched before this request started
                    // satisfies it (still REQUEST, the peer answers again).
                    preStored = clientCharset is not null;
                    if (!preStored)
                    {
                        clientCharset = null;
                        expectingCharset = true;
                    }
                }
            }

            await SendFrameAsync((int)Options.CharacterSet, CharsetProtocol.BuildRequest(Settings.CharsetOffers), cancellationToken).ConfigureAwait(false);
            if (preStored)
            {
                lock (collectorLock)
                {
                    return clientCharset;
                }
            }

            await PollForResponseAsync(IsCharsetDone, timeout, cancellationToken).ConfigureAwait(false);
            lock (collectorLock)
            {
                expectingCharset = false;
                return clientCharset;
            }
        }

        /// <summary>
        /// Asks the peer for its location (RFC 779: <c>DO SNDLOC</c>, one
        /// spontaneous SB). Returns the location string, or null on timeout.
        /// The peer volunteers the SB after WILL, so no SEND goes out here.
        /// </summary>
        /// <param name="timeout">The maximum time to wait for the answer.</param>
        /// <param name="cancellationToken">A token to cancel the wait.</param>
        public async Task<string?> RequestSendLocationAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            bool preStored;
            using (await AcquireWireForRequestAsync(cancellationToken).ConfigureAwait(false))
            {
                lock (collectorLock)
                {
                    // A volunteered location the pump filed before this request
                    // started satisfies it (still DO, the peer volunteers again).
                    preStored = clientLocation is not null;
                    if (!preStored)
                    {
                        clientLocation = null;
                        expectingLocation = true;
                    }
                }
            }

            await RequestEnableAsync(Options.SendLocation, cancellationToken).ConfigureAwait(false);
            if (preStored)
            {
                lock (collectorLock)
                {
                    return clientLocation;
                }
            }

            await PollForResponseAsync(IsLocationDone, timeout, cancellationToken).ConfigureAwait(false);
            lock (collectorLock)
            {
                expectingLocation = false;
                return clientLocation;
            }
        }

        /// <summary>
        /// Sends the LFLOW restart mode (RFC 1372) as a server: RESTART_ANY
        /// when <paramref name="restartOnAny"/> is true, else RESTART_XON.
        /// Returns <c>false</c> (sending nothing) unless the peer enabled
        /// LFLOW (WILL) first, mirroring the reference server-only guard.
        /// </summary>
        /// <param name="restartOnAny">Whether any character restarts output.</param>
        /// <param name="cancellationToken">A token to cancel the send.</param>
        public async Task<bool> SendLineflowModeAsync(bool restartOnAny, CancellationToken cancellationToken = default)
        {
            if (!Negotiation.IsEnabledByPeer((int)Options.RemoteFlowControl))
            {
                WriteLog("Cannot send LFLOW without receipt of WILL LFLOW.");
                return false;
            }

            var mode = restartOnAny ? LineflowProtocol.RestartAny : LineflowProtocol.RestartXon;
            await SendFrameAsync((int)Options.RemoteFlowControl, [mode], cancellationToken).ConfigureAwait(false);
            return true;
        }

        private bool IsTerminalTypeDone()
        {
            lock (collectorLock)
            {
                return !expectingTerminalType;
            }
        }

        private bool IsTerminalSpeedDone()
        {
            lock (collectorLock)
            {
                return !expectingTerminalSpeed;
            }
        }

        private bool IsEnvironmentDone()
        {
            lock (collectorLock)
            {
                return !expectingEnvironment;
            }
        }

        private bool IsNewEnvironmentDone()
        {
            lock (collectorLock)
            {
                return !expectingNewEnvironment;
            }
        }

        private bool IsCharsetDone()
        {
            lock (collectorLock)
            {
                return !expectingCharset;
            }
        }

        private bool IsLocationDone()
        {
            lock (collectorLock)
            {
                return !expectingLocation;
            }
        }

        private bool IsXDisplayDone()
        {
            lock (collectorLock)
            {
                return !expectingXDisplay;
            }
        }

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
                if (payload.Count == 5 && payload[0] == EnvironmentProtocol.Is)
                {
                    // Our own stack's shape (verb first).
                    clientWindowSize = ((ushort)(payload[1] << 8 | payload[2]), (ushort)(payload[3] << 8 | payload[4]));
                }
                else if (payload.Count == 4)
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
            // SB <opt> <data> SE blocks. Anything else (including a trailing
            // lone byte, which the reference logs and stops at) ends the
            // parse; the frame is still consumed.
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

                break;
            }

            lock (collectorLock)
            {
                peerStatusReport = [.. items];
            }

            return true;
        }

        private bool TryConsumeTerminalType(List<byte> payload)
        {
            var text = new byte[payload.Count - 1];
            payload.CopyTo(1, text, 0, text.Length);
            var answer = System.Text.Encoding.Latin1.GetString(text);
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
            var rawText = System.Text.Encoding.Latin1.GetString(payload.Skip(1).ToArray());
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
            var rawText = System.Text.Encoding.Latin1.GetString(payload.Skip(1).ToArray());
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
                foreach (var entry in EnvironmentProtocol.ParseEntries(payload, parseMax))
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
                    var name = System.Text.Encoding.ASCII.GetString([.. payload.Skip(1)]);
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
                    PendingText += text;
                    if (CheckBufferedTextCap())
                    {
                        return;
                    }
                }
            }
        }

        private async Task SendFrameAsync(int option, byte[] payload, CancellationToken cancellationToken)
        {
            var frame = EnvironmentProtocol.FrameSubnegotiation(option, payload);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, InternalCancellation.Token);
            if (WriteStream.Connected && !linked.Token.IsCancellationRequested)
            {
                await SendRateLimit.WaitAsync(linked.Token).ConfigureAwait(false);
                try
                {
                    await WriteStream.WriteAsync(frame, 0, frame.Length, linked.Token).ConfigureAwait(false);
                    Context.NoteWritten(frame.Length);
                }
                finally
                {
                    SendRateLimit.Release();
                }
            }
        }

        private async Task SendSbAsync(Options option, byte[] types, CancellationToken cancellationToken)
        {
            var frame = EnvironmentProtocol.FrameSubnegotiation((int)option, [EnvironmentProtocol.Send, .. types]);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, InternalCancellation.Token);
            if (WriteStream.Connected && !linked.Token.IsCancellationRequested)
            {
                await SendRateLimit.WaitAsync(linked.Token).ConfigureAwait(false);
                try
                {
                    await WriteStream.WriteAsync(frame, 0, frame.Length, linked.Token).ConfigureAwait(false);
                    Context.NoteWritten(frame.Length);
                }
                finally
                {
                    SendRateLimit.Release();
                }
            }
        }
    }
}
