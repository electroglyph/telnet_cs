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
        private bool expectingXDisplay;
        private readonly List<string> terminalTypeChain = [];
        private string? clientTerminalSpeed;
        private string? clientXDisplay;
        private readonly Dictionary<string, string> clientEnvironment = new(StringComparer.Ordinal);
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
        /// repeat (same-string-twice terminates the list); the terminating
        /// duplicate is excluded. A peer that never repeats is cut off after
        /// 32 entries. Returns whatever arrived when the timeout
        /// elapses (possibly empty).
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
            lock (collectorLock)
            {
                expectingTerminalType = false;
                var result = new List<string>(terminalTypeChain);
                if (result.Count >= 2 && string.Equals(result[^1], result[^2], StringComparison.OrdinalIgnoreCase))
                {
                    result.RemoveAt(result.Count - 1);
                }

                return result;
            }
        }

        /// <summary>
        /// Asks the peer for its terminal speed (RFC 1079: <c>SEND</c>, one
        /// <c>IS</c>). Returns the normalized <c>"&lt;tx&gt;,&lt;rx&gt;"</c>
        /// shape, or null on timeout or a malformed answer.
        /// </summary>
        /// <param name="timeout">The maximum time to wait for the answer.</param>
        /// <param name="cancellationToken">A token to cancel the wait.</param>
        public async Task<string?> RequestTerminalSpeedAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            lock (collectorLock)
            {
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
        /// <c>IS</c>). Returns the display string, or null on timeout or a
        /// malformed answer. Also feeds the effective-display recency rule (see
        /// <see cref="ClientEffectiveDisplay"/>).
        /// </summary>
        /// <param name="timeout">The maximum time to wait for the answer.</param>
        /// <param name="cancellationToken">A token to cancel the wait.</param>
        public async Task<string?> RequestXDisplayAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            lock (collectorLock)
            {
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

            if (inputOption == (int)Options.OldEnvironment)
            {
                return TryConsumeEnvironment(payload);
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
                terminalTypeChain.Add(System.Text.Encoding.Latin1.GetString(text));
                if (terminalTypeChain.Count >= 2 && string.Equals(terminalTypeChain[^1], terminalTypeChain[^2], StringComparison.OrdinalIgnoreCase))
                {
                    // Same-string-twice ends the list (RFC 1091 §6).
                    expectingTerminalType = false;
                }
                else if (terminalTypeChain.Count >= 32)
                {
                    // No end in sight: stop growing, keep consuming.
                    WriteLog("TERMINAL-TYPE chain exceeded 32 entries without repeating; ignoring the rest.");
                    expectingTerminalType = false;
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
                clientTerminalSpeed = TerminalSpeedProtocol.Normalize(System.Text.Encoding.Latin1.GetString(text));
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

        private bool TryConsumeEnvironment(List<byte> payload)
        {
            var isInfo = payload[0] == EnvironmentProtocol.Info;
            lock (collectorLock)
            {
                if (!isInfo && !expectingEnvironment)
                {
                    return false;
                }

                // RFC 1408: only the WILL-ENVIRON side may send INFO. An INFO
                // from a peer that never agreed is left unconsumed so the
                // handler answers WONT, exactly like a stray IS.
                if (isInfo && !Negotiation.IsEnabledByPeer((int)Options.OldEnvironment))
                {
                    return false;
                }

                if (!isInfo)
                {
                    expectingEnvironment = false;
                }

                foreach (var entry in EnvironmentProtocol.ParseEntries(payload))
                {
                    if (entry.Value is not null)
                    {
                        clientEnvironment[entry.Name] = entry.Value;
                        if (string.Equals(entry.Name, EnvironmentProtocol.DisplayVariableName, StringComparison.Ordinal))
                        {
                            environDisplaySeq = ++displayArrivalSeq;
                        }
                    }
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
