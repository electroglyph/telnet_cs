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
        /// Sends the server opening preset: WILL for each offered option and DO
        /// for each requested one (see <see cref="TelnetServerOptions"/>), each
        /// tracked by <see cref="Negotiation"/> so repeats stay silent per
        /// RFC 1143. Called by <see cref="TelnetServer.AcceptSessionAsync"/>;
        /// call it yourself after accepting outside a <see cref="TelnetServer"/>.
        /// A fully toggled-off preset sends nothing.
        /// </summary>
        /// <param name="cancellationToken">A token to cancel the send.</param>
        /// <returns>An awaitable Task.</returns>
        public async Task SendOpeningPresetAsync(CancellationToken cancellationToken = default)
        {
            if (Settings.OfferEcho)
            {
                await OfferEnableAsync(Options.Echo, cancellationToken).ConfigureAwait(false);
            }

            if (Settings.OfferSuppressGoAhead)
            {
                await OfferEnableAsync(Options.SuppressGoAhead, cancellationToken).ConfigureAwait(false);
            }

            if (Settings.RequestTerminalType)
            {
                await RequestEnableAsync(Options.TerminalType, cancellationToken).ConfigureAwait(false);
            }

            if (Settings.RequestTerminalSpeed)
            {
                await RequestEnableAsync(Options.TerminalSpeed, cancellationToken).ConfigureAwait(false);
            }

            if (Settings.RequestWindowSize)
            {
                await RequestEnableAsync(Options.WindowSize, cancellationToken).ConfigureAwait(false);
            }

            if (Settings.RequestEnvironment)
            {
                await RequestEnableAsync(Options.OldEnvironment, cancellationToken).ConfigureAwait(false);
            }

            if (Settings.RequestXDisplay)
            {
                await RequestEnableAsync(Options.XDisplay, cancellationToken).ConfigureAwait(false);
            }

            if (Settings.RequestLinemode)
            {
                await RequestEnableAsync(Options.LineMode, cancellationToken).ConfigureAwait(false);
            }

            if (Settings.RequestNewEnvironment)
            {
                await RequestEnableAsync(Options.NewEnvironment, cancellationToken).ConfigureAwait(false);
            }

            if (Settings.RequestCharacterSet)
            {
                await RequestEnableAsync(Options.CharacterSet, cancellationToken).ConfigureAwait(false);
            }

            if (Settings.RequestSendLocation)
            {
                await RequestEnableAsync(Options.SendLocation, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Asks the peer to enable <paramref name="telnetOption"/> (sends
        /// <c>IAC DO</c>), unless already enabled, already negotiating, or
        /// refused without new stimulus (see <see cref="Negotiation"/>).
        /// An explicit call is new stimulus and clears a remembered refusal.
        /// </summary>
        /// <param name="telnetOption">The option to request.</param>
        /// <param name="cancellationToken">A token to cancel the send.</param>
        /// <returns>An awaitable Task.</returns>
        public Task RequestEnableAsync(Options telnetOption, CancellationToken cancellationToken = default)
        {
            return SendRequestAsync(Negotiation.RequestEnable((int)telnetOption), telnetOption, cancellationToken);
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

            if (ByteStream.Connected && !cancellationToken.IsCancellationRequested)
            {
                await SendRateLimit.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    // A command frame has no data bytes, so no IAC escaping is needed.
                    await ByteStream.WriteAsync([(byte)Commands.InterpretAsCommand, (byte)command], 0, 2, cancellationToken).ConfigureAwait(false);
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
            if (!isTerminated(s))
            {
                WriteLog($"Failed to terminate '{s}' with '{terminator}'");
            }

            return s;
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
        /// caller's). The server mirror of <c>Client.TryLoginAsync</c>.
        /// A credential line that never terminates (timeout) fails closed: the
        /// attempt is abandoned and authentication returns <c>false</c>.
        /// NOTE: the password line travels the agreed echo channel when the
        /// session echoes (see <see cref="TelnetServerOptions.OfferEcho"/>) —
        /// echo suppression for secrets is future work, not silent behavior.
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
                string? password = await ReadCredentialLineAsync(timeout, cancellationToken).ConfigureAwait(false);
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
            string line = await TerminatedReadAsync("\n", timeout, 1, cancellationToken).ConfigureAwait(false);
            if (!line.Contains('\n', StringComparison.Ordinal))
            {
                // Timed out mid-line: the stream position is unrecoverable, so fail
                // closed instead of validating a truncated secret.
                WriteLog("Authenticate: credential line never terminated; giving up.");
                return null;
            }

            return line.TrimEnd('\r', '\n');
        }

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

            if (ByteStream.Connected && !cancellationToken.IsCancellationRequested)
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
        private Task SendNegotiationBytesAsync(Commands verb, Options option, CancellationToken cancellationToken)
        {
            var buffer = new byte[] { (byte)Commands.InterpretAsCommand, (byte)verb, (byte)option };
            return ByteStream.WriteAsync(buffer, 0, buffer.Length, cancellationToken);
        }

        private async Task<string> TerminatedReadAsync(Func<string, bool> isTerminated, TimeSpan timeout, int millisecondSpin, CancellationToken cancellationToken)
        {
            var endTimeout = DateTime.UtcNow.Add(timeout);
            var s = string.Empty;
            while (!isTerminated(s) && endTimeout >= DateTime.UtcNow)
            {
                var read = await ReadAsync(TimeSpan.FromMilliseconds(millisecondSpin), cancellationToken).ConfigureAwait(false);
                s += read;
            }

            return s;
        }

        private void WriteLog(string message)
        {
            Settings.Log?.Invoke(message);
            System.Diagnostics.Debug.WriteLine(message);
        }
    }
}
