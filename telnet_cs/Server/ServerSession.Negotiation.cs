namespace telnet_cs.Server
{
    using System;
    using System.Collections.Generic;
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Threading.Tasks;
    using telnet_cs.Protocol;
    using telnet_cs.Transport;

    /// <summary>
    /// Session-level protocol operations: the server opening preset (S2),
    /// explicit negotiation requests, control commands, the terminated-read
    /// family, and password authentication. Split from
    /// <see cref="ServerSession"/> the way <c>Client.*.cs</c> splits
    /// <c>Client</c>: same wire engine and I/O semantics, opposite role.
    /// </summary>
    public partial class ServerSession
    {
        /// <summary>
        /// Gets the persistent RFC 1143 negotiation state backing the shared
        /// <c>BaseClient</c> GA/waiter helpers.
        /// </summary>
        protected override NegotiationState SessionNegotiation => Negotiation;

        /// <summary>
        /// Sends the server opening preset: <c>DO TerminalType</c> only (the
        /// reference <c>begin_negotiation</c>). Everything else — <c>WILL
        /// SGA</c> (unless linemode is requested), <c>WILL BINARY</c>, <c>DO
        /// NAWS</c>, <c>DO CHARSET</c> — follows in the advanced preset once
        /// negotiation advances (see <see cref="BeginAdvancedNegotiationAsync"/>),
        /// and <c>WILL ECHO</c> / <c>DO NewEnvironment</c> stay deferred past
        /// TTYPE (see <see cref="FlushDeferredNegotiationAsync"/>). Called by
        /// <see cref="TelnetServer.AcceptSessionAsync"/>; call it yourself
        /// after accepting outside a <see cref="TelnetServer"/>.
        /// A preset with <c>RequestTerminalType</c> off sends nothing.
        /// </summary>
        /// <param name="cancellationToken">A token to cancel the send.</param>
        /// <returns>An awaitable Task.</returns>
        public async Task SendOpeningPresetAsync(CancellationToken cancellationToken = default)
        {
            if (Settings.RequestTerminalType)
            {
                await RequestEnableAsync(Options.TerminalType, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Sends the server advanced preset once negotiation advances (the
        /// reference <c>begin_advanced_negotiation</c>, gated by
        /// <c>negotiation_should_advance</c>): <c>WILL SGA</c>, <c>WILL
        /// BINARY</c>, <c>DO NAWS</c> and <c>DO CHARSET</c> per the matching
        /// settings flags, plus <c>DO LINEMODE</c> only when explicitly
        /// requested (the reference LINEMODE offer lives in its dedicated
        /// LinemodeServer path; the default char-mode server never sends it).
        /// Fires at most once per session; the RFC 1143 machine dedupes
        /// against anything already sent or agreed.
        /// </summary>
        /// <param name="cancellationToken">A token to cancel the send.</param>
        /// <returns>An awaitable Task.</returns>
        private async Task BeginAdvancedNegotiationAsync(CancellationToken cancellationToken)
        {
            if (Settings.OfferSuppressGoAhead)
            {
                await OfferEnableAsync(Options.SuppressGoAhead, cancellationToken).ConfigureAwait(false);
            }

            if (Settings.OfferBinary)
            {
                await OfferEnableAsync(Options.TransmitBinary, cancellationToken).ConfigureAwait(false);
            }

            if (Settings.RequestWindowSize)
            {
                await RequestEnableAsync(Options.WindowSize, cancellationToken).ConfigureAwait(false);
            }

            if (Settings.RequestCharacterSet)
            {
                await RequestEnableAsync(Options.CharacterSet, cancellationToken).ConfigureAwait(false);
            }

            if (Settings.RequestLinemode)
            {
                await RequestEnableAsync(Options.LineMode, cancellationToken).ConfigureAwait(false);
            }

            // Outbound MCCP2 is explicit opt-in (never over TLS): the
            // WILL goes out here, and the SB start marker plus the
            // compressing write view follow once the peer accepts (see
            // MaybeStartMccp2Async). EnableMccp alone stays the
            // passive-accept gate (agree + inflate when the peer offers),
            // never an offer.
            if (Settings.OfferMccp2 && !IsTls)
            {
                await OfferEnableAsync(Options.Mccp2, cancellationToken).ConfigureAwait(false);
            }

            // Inbound MCCP3 is explicit opt-in too (never over TLS): the
            // WILL goes out here, and the session inflates everything after
            // the peer's own SB start marker once it accepts.
            if (Settings.OfferMccp3 && !IsTls)
            {
                await OfferEnableAsync(Options.Mccp3, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Gets whether negotiation advanced far enough for the advanced
        /// preset: any option enabled on either side. A refusal alone is
        /// not an advance — a raw client that WONTs everything gets
        /// nothing further.
        /// </summary>
        private bool ShouldBeginAdvancedNegotiation()
        {
            for (int option = 0; option <= 255; option++)
            {
                var (us, him) = Negotiation.GetStates(option);
                if (us == NegotiationState.SideState.Yes || him == NegotiationState.SideState.Yes)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Asks the peer to enable <paramref name="telnetOption"/> (sends
        /// <c>IAC DO</c>), unless already enabled, already negotiating, or
        /// refused without new stimulus (see <see cref="Negotiation"/>).
        /// An explicit call is new stimulus and clears a remembered refusal.
        /// Timing-mark pings go through the ping path so repeats re-emit.
        /// </summary>
        /// <param name="telnetOption">The option to request.</param>
        /// <param name="cancellationToken">A token to cancel the send.</param>
        /// <returns>An awaitable Task.</returns>
        public Task RequestEnableAsync(Options telnetOption, CancellationToken cancellationToken = default)
        {
            var verb = telnetOption == Options.TimingMark
              ? Negotiation.RequestTimingMark()
              : Negotiation.RequestEnable((int)telnetOption);
            return SendRequestAsync(verb, telnetOption, cancellationToken);
        }

        /// <summary>
        /// Asks the peer to disable <paramref name="telnetOption"/> (sends
        /// <c>IAC DONT</c>), unless already disabled or already negotiating
        /// (see <see cref="Negotiation"/>).
        /// </summary>
        /// <param name="telnetOption">The option to refuse.</param>
        /// <param name="cancellationToken">A token to cancel the send.</param>
        /// <returns>An awaitable Task.</returns>
        public Task RequestDisableAsync(Options telnetOption, CancellationToken cancellationToken = default)
        {
            return SendRequestAsync(Negotiation.RequestDisable((int)telnetOption), telnetOption, cancellationToken);
        }

        /// <summary>
        /// Sends a standalone TELNET control command as an <c>IAC &lt;cmd&gt;</c>
        /// frame (RFC 854, plus EOF/SUSP/ABORT from RFC 1184 §2.5): BRK, IP,
        /// AO, AYT, EC, EL, GA, NOP, EOF, SUSP or ABORT.
        /// Option-negotiation verbs (DO, DONT, WILL, WONT, SB, SE, IAC) are
        /// rejected with <see cref="ArgumentOutOfRangeException"/>; they go
        /// through the RFC 1143 negotiation API instead.
        /// </summary>
        /// <param name="command">The control command to send.</param>
        /// <param name="cancellationToken">A token to cancel the send.</param>
        /// <returns>An awaitable Task.</returns>
        public async Task SendCommand(Commands command, CancellationToken cancellationToken = default)
        {
            switch (command)
            {
                case Commands.Break:
                case Commands.InterruptProcess:
                case Commands.AbortOutput:
                case Commands.AreYouThere:
                case Commands.EraseCharacter:
                case Commands.EraseLine:
                case Commands.GoAhead:
                case Commands.NoOperation:
                case Commands.EndOfFile:
                case Commands.Suspend:
                case Commands.Abort:
                    break;
                default:
                    throw new ArgumentOutOfRangeException(
                      nameof(command), command, "Only standalone control commands (BRK, IP, AO, AYT, EC, EL, GA, NOP, EOF, SUSP, ABORT) can be sent with SendCommand. Option negotiation verbs (DO, DONT, WILL, WONT, SB, SE, IAC) go through the RFC 1143 negotiation API.");
            }

            if (command == Commands.GoAhead && Negotiation.IsEnabledByUs((int)Options.SuppressGoAhead))
            {
                return;
            }

            if (WriteStream.Connected && !cancellationToken.IsCancellationRequested)
            {
                await SendRateLimit.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    // A command frame has no data bytes, so no IAC escaping is needed.
                    await WriteStream.WriteAsync([(byte)Commands.InterpretAsCommand, (byte)command], 0, 2, cancellationToken).ConfigureAwait(false);
                    Context.NoteWritten(2);
                }
                finally
                {
                    SendRateLimit.Release();
                }

                // RFC 1184 §5.8: a sent function with FLUSHIN/FLUSHOUT also fires
                // its flush actions. Runs after the semaphore is released — the
                // FLUSHOUT send takes it itself; FLUSHIN goes straight to the
                // separate OOB channel and takes no lock.
                await ProcessSlcFlushAsync(command, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Sends a TELNET Synch signal: TCP Urgent notification with DM as the
        /// urgent octet (RFC 854). Requires the byte stream to be a
        /// <see cref="TcpByteStream"/> over a real TCP connection; otherwise
        /// throws <see cref="NotSupportedException"/>.
        /// </summary>
        /// <param name="cancellationToken">A token to cancel the send.</param>
        /// <returns>An awaitable Task.</returns>
        public async Task SendSynchAsync(CancellationToken cancellationToken = default)
        {
            // Out-of-band support lives behind TcpByteStream: fakes and custom
            // IByteStream implementations cannot send TCP urgent data, so fail
            // loudly instead of half-implementing. DM in normal mode stays a NOP.
            if (ByteStream is not TcpByteStream stream)
            {
                throw new NotSupportedException("Synch (TCP urgent data) requires a real TCP connection (TcpByteStream); the current byte stream does not support out-of-band sends.");
            }

            if (ByteStream.Connected && !cancellationToken.IsCancellationRequested)
            {
                await SendRateLimit.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    // RFC 854 Synch: TCP Urgent notification with DM as the last (here
                    // the only) urgent octet. Verified on .NET 10 (loopback OOB spike).
                    await stream.SendUrgentAsync((byte)Commands.DataMark, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    SendRateLimit.Release();
                }
            }
        }

        /// <summary>
        /// Reads asynchronously from the session, terminating as soon as the <paramref name="terminator"/> is located.
        /// </summary>
        /// <param name="terminator">The terminator.</param>
        /// <returns>Any text read from the session.</returns>
        public Task<string> TerminatedReadAsync(string terminator)
        {
            return TerminatedReadAsync(terminator, TimeSpan.FromMilliseconds(DefaultTimeoutMs));
        }

        /// <summary>
        /// Reads asynchronously from the session, terminating as soon as the <paramref name="terminator"/> is located.
        /// </summary>
        /// <param name="terminator">The terminator.</param>
        /// <param name="timeout">The timeout.</param>
        /// <returns>Any text read from the session.</returns>
        public Task<string> TerminatedReadAsync(string terminator, TimeSpan timeout)
        {
            return TerminatedReadAsync(terminator, timeout, 1);
        }

        /// <summary>
        /// Reads asynchronously from the session, terminating as soon as the <paramref name="regex"/> is located.
        /// </summary>
        /// <param name="regex">The regex to match.</param>
        /// <param name="timeout">The timeout.</param>
        /// <returns>Any text read from the session.</returns>
        public Task<string> TerminatedReadAsync(Regex regex, TimeSpan timeout)
        {
            return TerminatedReadAsync(regex, timeout, 1);
        }

        /// <summary>
        /// Reads asynchronously from the session, terminating as soon as any of the <paramref name="terminators"/> is located.
        /// </summary>
        /// <param name="terminators">The terminators to search for.</param>
        /// <returns>Any text read from the session.</returns>
        public Task<string> TerminatedReadAsync(IEnumerable<string> terminators)
        {
            return TerminatedReadAsync(terminators, TimeSpan.FromMilliseconds(DefaultTimeoutMs));
        }

        /// <summary>
        /// Reads asynchronously from the session, terminating as soon as any of the <paramref name="terminators"/> is located.
        /// </summary>
        /// <param name="terminators">The terminators to search for.</param>
        /// <param name="timeout">The timeout.</param>
        /// <returns>Any text read from the session.</returns>
        public Task<string> TerminatedReadAsync(IEnumerable<string> terminators, TimeSpan timeout)
        {
            return TerminatedReadAsync(terminators, timeout, 1);
        }

        /// <summary>
        /// Reads asynchronously from the session, terminating as soon as any of the <paramref name="regexes"/> is matched.
        /// </summary>
        /// <param name="regexes">The regexes to match.</param>
        /// <returns>Any text read from the session.</returns>
        public Task<string> TerminatedReadAsync(IEnumerable<Regex> regexes)
        {
            return TerminatedReadAsync(regexes, TimeSpan.FromMilliseconds(DefaultTimeoutMs));
        }

        /// <summary>
        /// Reads asynchronously from the session, terminating as soon as any of the <paramref name="regexes"/> is matched.
        /// </summary>
        /// <param name="regexes">The regexes to match.</param>
        /// <param name="timeout">The timeout.</param>
        /// <returns>Any text read from the session.</returns>
        public Task<string> TerminatedReadAsync(IEnumerable<Regex> regexes, TimeSpan timeout)
        {
            return TerminatedReadAsync(regexes, timeout, 1);
        }

        /// <summary>
        /// Reads asynchronously from the session, terminating as soon as the <paramref name="terminator"/> is located.
        /// </summary>
        /// <param name="terminator">The terminator.</param>
        /// <param name="timeout">The maximum time to wait.</param>
        /// <param name="millisecondSpin">The millisecond spin between each read from the session.</param>
        /// <returns>Any text read from the session.</returns>
        public Task<string> TerminatedReadAsync(string terminator, TimeSpan timeout, int millisecondSpin)
        {
            return TerminatedReadAsync(terminator, timeout, millisecondSpin, CancellationToken.None);
        }

        /// <summary>
        /// Reads asynchronously from the session, terminating as soon as the <paramref name="terminator"/> is located.
        /// </summary>
        /// <param name="terminator">The terminator.</param>
        /// <param name="timeout">The maximum time to wait.</param>
        /// <param name="millisecondSpin">The millisecond spin between each read from the session.</param>
        /// <param name="cancellationToken">Token to cancel the read.</param>
        /// <returns>Any text read from the session.</returns>
        public async Task<string> TerminatedReadAsync(string terminator, TimeSpan timeout, int millisecondSpin, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(terminator);
            bool isTerminated(string x) => IsTerminatorLocated(terminator, x);
            var s = await TerminatedReadAsync(isTerminated, timeout, millisecondSpin, cancellationToken).ConfigureAwait(false);
            s = CutAtFirstTerminator(s, terminator);
            if (!isTerminated(s))
            {
                WriteLog($"Failed to terminate '{s}' with '{terminator}'");
            }

            return s;
        }

        private string CutAtFirstTerminator(string s, string terminator)
        {
            int at = s.IndexOf(terminator, StringComparison.Ordinal);
            if (at < 0)
            {
                return s;
            }

            int end = at + terminator.Length;
            PendingText = s.Substring(end);
            return s.Substring(0, end);
        }

        /// <summary>
        /// Reads asynchronously from the session, terminating as soon as the <paramref name="regex"/> is matched.
        /// </summary>
        /// <param name="regex">The regex to match.</param>
        /// <param name="timeout">The maximum time to wait.</param>
        /// <param name="millisecondSpin">The millisecond spin between each read from the session.</param>
        /// <returns>Any text read from the session.</returns>
        public Task<string> TerminatedReadAsync(Regex regex, TimeSpan timeout, int millisecondSpin)
        {
            return TerminatedReadAsync(regex, timeout, millisecondSpin, CancellationToken.None);
        }

        /// <summary>
        /// Reads asynchronously from the session, terminating as soon as the <paramref name="regex"/> is matched.
        /// </summary>
        /// <param name="regex">The regex to match.</param>
        /// <param name="timeout">The maximum time to wait.</param>
        /// <param name="millisecondSpin">The millisecond spin between each read from the session.</param>
        /// <param name="cancellationToken">Token to cancel the read.</param>
        /// <returns>Any text read from the session.</returns>
        public async Task<string> TerminatedReadAsync(Regex regex, TimeSpan timeout, int millisecondSpin, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(regex);
            bool isTerminated(string x) => IsRegexLocated(regex, x);
            var s = await TerminatedReadAsync(isTerminated, timeout, millisecondSpin, cancellationToken).ConfigureAwait(false);
            if (!isTerminated(s))
            {
                WriteLog($"Failed to match '{s}' with '{regex}'");
            }

            return s;
        }

        /// <summary>
        /// Reads asynchronously from the session, terminating as soon as any of the <paramref name="terminators"/> is located.
        /// </summary>
        /// <param name="terminators">The terminators to search for.</param>
        /// <param name="timeout">The maximum time to wait.</param>
        /// <param name="millisecondSpin">The millisecond spin between each read from the session.</param>
        /// <param name="cancellationToken">Token to cancel the read.</param>
        /// <returns>Any text read from the session.</returns>
        public async Task<string> TerminatedReadAsync(IEnumerable<string> terminators, TimeSpan timeout, int millisecondSpin, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(terminators);
            bool isTerminated(string x) => IsAnyTerminatorLocated(terminators, x);
            var s = await TerminatedReadAsync(isTerminated, timeout, millisecondSpin, cancellationToken).ConfigureAwait(false);
            if (!isTerminated(s))
            {
                WriteLog($"Failed to terminate '{s}' with any known terminator");
            }

            return s;
        }

        /// <summary>
        /// Reads asynchronously from the session, terminating as soon as any of the <paramref name="regexes"/> is matched.
        /// </summary>
        /// <param name="regexes">The regexes to match.</param>
        /// <param name="timeout">The maximum time to wait.</param>
        /// <param name="millisecondSpin">The millisecond spin between each read from the session.</param>
        /// <param name="cancellationToken">Token to cancel the read.</param>
        /// <returns>Any text read from the session.</returns>
        public async Task<string> TerminatedReadAsync(IEnumerable<Regex> regexes, TimeSpan timeout, int millisecondSpin, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(regexes);
            bool isTerminated(string x) => IsAnyRegexLocated(regexes, x);
            var s = await TerminatedReadAsync(isTerminated, timeout, millisecondSpin, cancellationToken).ConfigureAwait(false);
            if (!isTerminated(s))
            {
                WriteLog($"Failed to match '{s}' with any known pattern");
            }

            return s;
        }

        /// <summary>
        /// Authenticates the peer: sends <see cref="TelnetServerOptions.LoginUserPrompt"/>,
        /// reads a credential line, sends <see cref="TelnetServerOptions.LoginPasswordPrompt"/>,
        /// reads a credential line, and passes both to <paramref name="validate"/>.
        /// Retries up to <see cref="TelnetServerOptions.MaxLoginAttempts"/> times, then
        /// returns <c>false</c> (the session stays open — disconnect policy is the
        /// caller's). There is no client-side counterpart: callers script
        /// the peer side with explicit <c>TerminatedReadAsync</c> +
        /// <c>WriteLineAsync</c> exchanges against these prompts.
        /// A credential line that never terminates (timeout) fails closed: the
        /// attempt is abandoned and authentication returns <c>false</c>.
        /// The password line is never echoed back: echo-back is withheld for
        /// that line (negotiation state is untouched — no <c>WILL ECHO</c>
        /// goes out, since MUD clients render it as password mode) and
        /// restored afterwards. The username line echoes normally.
        /// </summary>
        /// <param name="validate">Validates a (user, password) pair. Exceptions propagate immediately.</param>
        /// <param name="timeout">The maximum time to wait for each credential line.</param>
        /// <param name="cancellationToken">Token to cancel the read.</param>
        /// <returns><c>true</c> when a pair validates within the attempt budget.</returns>
        public async Task<bool> AuthenticateAsync(Func<string, string, Task<bool>> validate, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(validate);
            ArgumentOutOfRangeException.ThrowIfLessThan(Settings.MaxLoginAttempts, 1);
            for (int attempt = 0; attempt < Settings.MaxLoginAttempts; attempt++)
            {
                await WriteAsync(Settings.LoginUserPrompt, cancellationToken).ConfigureAwait(false);
                string? user = await ReadCredentialLineAsync(timeout, cancellationToken).ConfigureAwait(false);
                if (user is null)
                {
                    return false;
                }

                await WriteAsync(Settings.LoginPasswordPrompt, cancellationToken).ConfigureAwait(false);
                string? password;
                echoBackSuppressed = true;
                try
                {
                    password = await ReadCredentialLineAsync(timeout, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    echoBackSuppressed = false;
                }

                if (password is null)
                {
                    return false;
                }

                if (await validate(user, password).ConfigureAwait(false))
                {
                    return true;
                }
            }

            return false;
        }

        private async Task<string?> ReadCredentialLineAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            string line;
            try
            {
                line = await TerminatedReadAsync("\n", timeout, 1, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Timed out mid-line: the stream position is unrecoverable, so fail
                // closed instead of validating a truncated secret.
                WriteLog("Authenticate: credential line never terminated; giving up.");
                return null;
            }

            return line.TrimEnd('\r', '\n');
        }

        // Withholds our echo-back while a secret line is read (see
        // AuthenticateAsync): fed to every per-read handler, restored after.
        private bool echoBackSuppressed;

        // First-data TLS sniff state (see ReadAsync): once a raw byte has
        // been seen, a 0x16 lead on a plaintext listener closes the session.
        private bool tlsHelloChecked;

        /// <summary>
        /// Gets the remote endpoint label for diagnostics (set by
        /// <see cref="TelnetServer.AcceptSessionAsync"/>).
        /// </summary>
        internal string? RemoteEndPoint { get; set; }

        private Task OfferEnableAsync(Options telnetOption, CancellationToken cancellationToken)
        {
            return SendRequestAsync(Negotiation.OfferEnable((int)telnetOption), telnetOption, cancellationToken);
        }

        private async Task SendRequestAsync(Commands? verb, Options option, CancellationToken cancellationToken)
        {
            if (verb is null)
            {
                return;
            }

            if (WriteStream.Connected && !cancellationToken.IsCancellationRequested)
            {
                // Copy out before the first await: the callee takes a
                // non-nullable verb, and narrowing a parameter across awaits is
                // not something to rely on here.
                Commands agreedVerb = verb.Value;
                await SendRateLimit.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await SendNegotiationBytesAsync(agreedVerb, option, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    SendRateLimit.Release();
                }
            }
        }

        // The single caller (SendRequestAsync) already drops null verbs, so the
        // non-nullable parameter lets the compiler enforce that contract.
        private async Task SendNegotiationBytesAsync(Commands verb, Options option, CancellationToken cancellationToken)
        {
            var buffer = new byte[] { (byte)Commands.InterpretAsCommand, (byte)verb, (byte)option };
            await WriteStream.WriteAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
            Context.NoteWritten(buffer.Length);
        }

        // The readuntil/readline replacement (see PendingText): poll plain
        // reads until the predicate holds or the timeout lapses. Reference
        // readuntil parity: never returns a partial — a missed deadline
        // throws TimeoutException (not "") and a buffer past the 64 KiB
        // reference limit throws (the C# analog of LimitOverrunError).
        private async Task<string> TerminatedReadAsync(Func<string, bool> isTerminated, TimeSpan timeout, int millisecondSpin, CancellationToken cancellationToken)
        {
            var endTimeout = DateTime.UtcNow.Add(timeout);
            var s = string.Empty;
            while (!isTerminated(s) && endTimeout >= DateTime.UtcNow)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = await ReadAsync(TimeSpan.FromMilliseconds(millisecondSpin), cancellationToken).ConfigureAwait(false);
                s += read;
                if (!isTerminated(s) && s.Length > TerminatedReadLimit)
                {
                    throw new InvalidOperationException(string.Format("Terminated read exceeded the {0}-character limit without locating the terminator.", TerminatedReadLimit));
                }
            }

            if (!isTerminated(s))
            {
                throw new TimeoutException("Terminated read timed out before locating the terminator.");
            }

            // CR NUL is the wire spelling of a lone CR (RFC 854): raw reads
            // preserve both bytes, line helpers normalize — same as the client.
            return s.Replace("\r\0", "\r", StringComparison.Ordinal);
        }

        private void WriteLog(string message)
        {
            Settings.Log?.Invoke(message);
            System.Diagnostics.Debug.WriteLine(message);
        }
    }
}
