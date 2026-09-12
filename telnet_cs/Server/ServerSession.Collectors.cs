namespace telnet_cs.Server
{
    using telnet_cs.IO;
    using telnet_cs.Protocol;

    /// <summary>
    /// Server-role subnegotiation requesters (S3): the session asks, the peer
    /// answers. The <see cref="ByteStreamHandler"/> routes inbound IS/INFO (and
    /// inbound NAWS) payloads to <see cref="OnSubnegotiationResponse"/>; each
    /// requester sends its SEND, then polls reads until its collector is
    /// satisfied or the timeout elapses. Stray IS with no outstanding request
    /// keeps the safe default (the handler answers WONT).
    /// </summary>
    public partial class ServerSession
    {
        // Guards the expecting-flags, chains, and collected values below. The
        // collectors assume single-threaded session use: concurrent
        // Request*Async calls would overwrite each other's expecting-flags and
        // mix the chains (no reentrancy protection by design).
        private readonly Lock collectorLock = new();
        private bool expectingTerminalType;
        private bool expectingTerminalSpeed;
        private bool expectingEnvironment;
        private bool expectingNewEnvironment;
        private bool expectingXDisplay;
        private bool expectingCharset;
        private bool expectingLocation;
        private readonly List<string> terminalTypeChain = [];
        // Deferred opening negotiation (mirroring the reference): WILL ECHO
        // and DO NEW_ENVIRON leave with the preset only as intent. The TTYPE
        // answers (or its refusal, or the collection timeout) arm them via
        // the pending flags; FlushDeferredNegotiationAsync sends them after a
        // read. echoNegotiated/environRequested latch so each fires once.
        private bool presetAdvanced;
        private bool echoNegotiated;
        private bool environRequested;
        private bool negotiateEchoPending;
        private bool negotiateEnvironPending;
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
        // MCCP agreement survives across per-read handlers: the arming SB,
        // stream end, and corrupt shutdown report through MccpStateChanged.
        private bool mccp2Agreed;
        private bool mccp3Agreed;
        private MccpDecompressor? mccpStream;
        // Subnegotiation continuation stashed by the last read, fed into the
        // next per-read handler (telnetlib3 _sb_buffer parity).
        private (int Option, byte[] Payload, bool OverCap, bool SePending, bool IacPending)? sbResumeState;        // MUD stores survive across per-read handlers: append collections are
        // injected into each handler, and the replaced MSSP mapping is
        // captured through the MSSP hook.
        private IReadOnlyDictionary<string, object>? mudMsspData;
        private readonly List<byte[]> mudMspData = [];
        private readonly List<byte[]> mudMxpData = [];
        private readonly Dictionary<string, IReadOnlyList<string>> mudZmpData = new(StringComparer.Ordinal);
        private readonly List<AardwolfMessage> mudAardwolfData = [];
        private readonly List<(string Package, string Value)> mudAtcpData = [];
        private (ushort Width, ushort Height)? clientWindowSize;
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
        /// telnetlib3's <c>TTYPE_LOOPMAX</c>: answers past slot 8 keep
        /// overwriting the overflow slot (<c>ttype9</c>), so the last answer
        /// always wins there.
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
        /// null when no solicited IS arrived. Populated by
        /// <see cref="RequestXDisplayAsync"/>; unsolicited reports earn WONT.
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
        /// Asks the peer for its terminal-type list (RFC 1091: <c>SEND</c>,
        /// then one <c>IS</c> per entry). The returned list ends at the first
        /// repeat: a reply equal to the first entry (cycle looped) or to the
        /// previous entry terminates the list, as does an <c>MTTS</c>
        /// capability vector in the third slot; the terminating duplicate is
        /// excluded. Empty answers advance nothing (the next answer fills the
        /// same slot). Answers past slot <see cref="TerminalTypeLoopMax"/>
        /// keep overwriting the overflow slot, so a peer that never repeats
        /// is bounded and the last answer wins there. Returns whatever arrived
        /// when the timeout elapses (possibly empty).
        /// </summary>
        /// <param name="timeout">The maximum time to wait for the full chain.</param>
        /// <param name="cancellationToken">A token to cancel the wait.</param>
        public async Task<IReadOnlyList<string>> RequestTerminalTypesAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            lock (collectorLock)
            {
                terminalTypeChain.Clear();
                expectingTerminalType = true;
            }

            await SendSbAsync(Options.TerminalType, [], cancellationToken).ConfigureAwait(false);
            await PollForResponseAsync(IsTerminalTypeDone, timeout, cancellationToken).ConfigureAwait(false);
            bool timedOut;
            lock (collectorLock)
            {
                timedOut = expectingTerminalType;
                if (timedOut)
                {
                    // Final wait over with the cycle unresolved (stall): the
                    // deferred negotiations release now, like the reference
                    // check_negotiation(final=True).
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
                var result = new List<string>(terminalTypeChain);
                if (result.Count >= 2 &&
                    (string.Equals(result[^1], result[^2], StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(result[^1], result[0], StringComparison.OrdinalIgnoreCase)))
                {
                    // Terminating duplicate excluded: consecutive repeat, or
                    // the looped repeat of the first entry.
                    result.RemoveAt(result.Count - 1);
                }

                return result;
            }
        }

        /// <summary>
        /// Sends whatever deferred opening negotiation is armed: WILL ECHO
        /// (unless the peer looks like a MUD client), DO NEW_ENVIRON, its
        /// default SB SEND once agreed, and the encoding check (DO BINARY /
        /// CHARSET REQUEST). Runs after every read, so TTYPE answers and
        /// refusals observed mid-read take effect on the same round.
        /// </summary>
        /// <param name="cancellationToken">A token to cancel the send.</param>
        private async Task FlushDeferredNegotiationAsync(CancellationToken cancellationToken)
        {
            string? term;
            List<string> chain;
            bool wantEcho;
            bool wantEnviron;
            lock (collectorLock)
            {
                if (presetAdvanced && Negotiation.WasRefusedByPeer((int)Options.TerminalType))
                {
                    // A raw client that WONTs TTYPE still releases the
                    // deferred negotiations (reference check_negotiation).
                    negotiateEchoPending = true;
                    negotiateEnvironPending = true;
                }

                wantEcho = negotiateEchoPending && !echoNegotiated;
                wantEnviron = negotiateEnvironPending && !environRequested;
                if (wantEcho)
                {
                    echoNegotiated = true;
                }

                if (wantEnviron)
                {
                    environRequested = true;
                }

                negotiateEchoPending = false;
                negotiateEnvironPending = false;
                chain = [.. terminalTypeChain];
                term = chain.Count >= 3 && chain[2].StartsWith("MTTS ", StringComparison.OrdinalIgnoreCase)
                    ? chain[1]
                    : chain.Count > 0 ? chain[^1] : null;
            }

            if (wantEcho && Settings.OfferEcho &&
                !MudClientDetector.IsMudClient(term, chain, o => Negotiation.IsEnabledByPeer(o)))
            {
                await OfferEnableAsync(Options.Echo, cancellationToken).ConfigureAwait(false);
            }

            if (wantEnviron && Settings.RequestNewEnvironment)
            {
                await RequestEnableAsync(Options.NewEnvironment, cancellationToken).ConfigureAwait(false);
            }

            await MaybeSendEnvironmentRequestAsync(cancellationToken).ConfigureAwait(false);
            await CheckEncodingAsync(cancellationToken).ConfigureAwait(false);
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
                !Negotiation.IsEnabledByPeer((int)Options.TransmitBinary))
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
            lock (collectorLock)
            {
                if (expectingTerminalSpeed)
                {
                    return null;
                }

                clientTerminalSpeed = null;
                expectingTerminalSpeed = true;
            }

            await SendSbAsync(Options.TerminalSpeed, [], cancellationToken).ConfigureAwait(false);
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
            lock (collectorLock)
            {
                if (expectingXDisplay)
                {
                    return null;
                }

                clientXDisplay = null;
                expectingXDisplay = true;
            }

            await SendSbAsync(Options.XDisplay, [], cancellationToken).ConfigureAwait(false);
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
        /// </summary>
        /// <param name="timeout">The maximum time to wait for the answer.</param>
        /// <param name="types">The requested type bytes (VAR/USERVAR); null or
        /// empty requests the defaults (well-known variables, then user
        /// variables).</param>
        /// <param name="cancellationToken">A token to cancel the wait.</param>
        public async Task<IReadOnlyDictionary<string, string>> RequestEnvironmentAsync(TimeSpan timeout, byte[]? types = null, CancellationToken cancellationToken = default)
        {
            lock (collectorLock)
            {
                expectingEnvironment = true;
            }

            await SendSbAsync(Options.OldEnvironment, types ?? [], cancellationToken).ConfigureAwait(false);
            await PollForResponseAsync(IsEnvironmentDone, timeout, cancellationToken).ConfigureAwait(false);
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
        /// </summary>
        /// <param name="timeout">The maximum time to wait for the answer.</param>
        /// <param name="types">The requested type bytes (VAR/USERVAR); null or
        /// empty requests the defaults (well-known variables, then user
        /// variables).</param>
        /// <param name="cancellationToken">A token to cancel the wait.</param>
        public async Task<IReadOnlyDictionary<string, string>> RequestNewEnvironmentAsync(TimeSpan timeout, byte[]? types = null, CancellationToken cancellationToken = default)
        {
            lock (collectorLock)
            {
                expectingNewEnvironment = true;
            }

            await SendSbAsync(Options.NewEnvironment, types ?? [], cancellationToken).ConfigureAwait(false);
            await PollForResponseAsync(IsNewEnvironmentDone, timeout, cancellationToken).ConfigureAwait(false);
            lock (collectorLock)
            {
                expectingNewEnvironment = false;
                return new Dictionary<string, string>(clientNewEnvironment, StringComparer.Ordinal);
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
            lock (collectorLock)
            {
                clientCharset = null;
                expectingCharset = true;
            }

            await SendFrameAsync((int)Options.CharacterSet, CharsetProtocol.BuildRequest(Settings.CharsetOffers), cancellationToken).ConfigureAwait(false);
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
            lock (collectorLock)
            {
                clientLocation = null;
                expectingLocation = true;
            }

            await RequestEnableAsync(Options.SendLocation, cancellationToken).ConfigureAwait(false);
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
                return TryConsumeCharset(payload);
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

                return true;
            }
        }

        private bool TryConsumeTerminalType(List<byte> payload)
        {
            lock (collectorLock)
            {
                if (!expectingTerminalType)
                {
                    return false;
                }

                var text = new byte[payload.Count - 1];
                payload.CopyTo(1, text, 0, text.Length);
                var answer = System.Text.Encoding.Latin1.GetString(text);
                // Every answer negotiates echo (deduped at flush time): ECHO
                // waits until TTYPE reveals the client because MUD clients
                // render WILL ECHO as password mode.
                negotiateEchoPending = true;
                if (answer.Length == 0)
                {
                    // Empty IS advances nothing: the next answer fills the same
                    // slot (telnetlib3 stores-then-overwrites to the same net
                    // effect), so the chain keeps its order. An empty first
                    // answer still ends the reference cycle, so it arms the
                    // deferred environ request.
                    if (terminalTypeChain.Count == 0)
                    {
                        negotiateEnvironPending = true;
                    }

                    return true;
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
                    }

                    return true;
                }

                if (terminalTypeChain.Count > TerminalTypeLoopMax)
                {
                    // Overflow slot: keep overwriting with the latest answer
                    // (telnetlib3's ttype{LOOPMAX+1}), staying open until a
                    // repeat, an MTTS vector, or the timeout ends the wait.
                    terminalTypeChain[^1] = answer;
                    return true;
                }

                bool isSecond = terminalTypeChain.Count == 1;
                terminalTypeChain.Add(answer);
                if (isSecond && !environRequested)
                {
                    // Second answer: an ANSI first answer is resolved now, so
                    // the deferred environ request goes out (unless already).
                    negotiateEnvironPending = true;
                }

                if (string.Equals(answer, terminalTypeChain[0], StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(answer, terminalTypeChain[^2], StringComparison.OrdinalIgnoreCase) ||
                    (terminalTypeChain.Count == 3 && answer.StartsWith("MTTS ", StringComparison.OrdinalIgnoreCase)))
                {
                    // Cycle looped (first entry repeated), entry repeated, or
                    // MTTS capability vector in the third slot: done, and the
                    // cycle end also releases the deferred environ request.
                    expectingTerminalType = false;
                    negotiateEnvironPending = true;
                }

                return true;
            }
        }

        private bool TryConsumeTerminalSpeed(List<byte> payload)
        {
            lock (collectorLock)
            {
                if (!expectingTerminalSpeed)
                {
                    return false;
                }

                expectingTerminalSpeed = false;
                var text = new byte[payload.Count - 1];
                payload.CopyTo(1, text, 0, text.Length);
                clientTerminalSpeed = TerminalSpeedProtocol.Validate(System.Text.Encoding.Latin1.GetString(text));
                return true;
            }
        }

        private bool TryConsumeXDisplay(List<byte> payload)
        {
            lock (collectorLock)
            {
                if (!expectingXDisplay)
                {
                    return false;
                }

                expectingXDisplay = false;
                var text = new byte[payload.Count - 1];
                payload.CopyTo(1, text, 0, text.Length);
                clientXDisplay = System.Text.Encoding.Latin1.GetString(text);
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
                if (!isInfo && (isNew ? !expectingNewEnvironment : !expectingEnvironment))
                {
                    return false;
                }

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
                foreach (var entry in EnvironmentProtocol.ParseEntries(payload))
                {
                    // Untrusted input: keys are upper-cased so a client cannot
                    // override trusted mixed-case values, and empty values
                    // ("no value", possibly withheld) are dropped. Matches
                    // telnetlib3's on_environ.
                    if (entry.Value is { Length: > 0 })
                    {
                        var key = entry.Name.ToUpperInvariant();
                        store[key] = entry.Value;
                        (batch ??= new Dictionary<string, string>(StringComparer.Ordinal))[key] = entry.Value;
                        if (key == EnvironmentProtocol.DisplayVariableName)
                        {
                            environDisplaySeq = ++displayArrivalSeq;
                        }
                    }
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
        /// Consumes an SNDLOC report (RFC 779, raw ASCII, no verbs) while a
        /// location request is outstanding.
        /// </summary>
        private bool TryConsumeSendLocation(List<byte> payload)
        {
            lock (collectorLock)
            {
                if (!expectingLocation)
                {
                    return false;
                }

                expectingLocation = false;
                clientLocation = System.Text.Encoding.Latin1.GetString([.. payload]);
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

                    clientCharset = name;
                    forceBinaryDecoding = true;
                    try
                    {
                        charsetEncoding = System.Text.Encoding.GetEncoding(clientCharset);
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

        private async Task PollForResponseAsync(Func<bool> isDone, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var end = DateTime.UtcNow.Add(timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, InternalCancellation.Token);
            while (!isDone() && DateTime.UtcNow < end && !linked.Token.IsCancellationRequested)
            {
                await ReadAsync(TimeSpan.FromMilliseconds(MillisecondReadDelay), linked.Token).ConfigureAwait(false);
            }
        }

        private async Task SendFrameAsync(int option, byte[] payload, CancellationToken cancellationToken)
        {
            var frame = EnvironmentProtocol.FrameSubnegotiation(option, payload);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, InternalCancellation.Token);
            if (ByteStream.Connected && !linked.Token.IsCancellationRequested)
            {
                await SendRateLimit.WaitAsync(linked.Token).ConfigureAwait(false);
                try
                {
                    await ByteStream.WriteAsync(frame, 0, frame.Length, linked.Token).ConfigureAwait(false);
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
            if (ByteStream.Connected && !linked.Token.IsCancellationRequested)
            {
                await SendRateLimit.WaitAsync(linked.Token).ConfigureAwait(false);
                try
                {
                    await ByteStream.WriteAsync(frame, 0, frame.Length, linked.Token).ConfigureAwait(false);
                }
                finally
                {
                    SendRateLimit.Release();
                }
            }
        }
    }
}
