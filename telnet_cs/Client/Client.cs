namespace telnet_cs.Client
{
    using System;
    using System.Collections.Generic;
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Threading.Tasks;
    using telnet_cs.IO;
    using telnet_cs.Protocol;
    using telnet_cs.Transport;

    // Referencing https://support.microsoft.com/kb/231866?wa=wsignin1.0 and http://www.codeproject.com/Articles/19071/Quick-tool-A-minimalistic-Telnet-library got me started

    public partial class Client : BaseClient, IClient
    {
        /// <inheritdoc/>
        public Task<bool> TryLoginAsync(string userName, string password, int loginTimeoutMs, string lineFeed = LegacyLineFeed)
        {
            return TryLoginAsync(userName, password, loginTimeoutMs, ">", lineFeed);
        }

        /// <inheritdoc/>
        public async Task<bool> TryLoginAsync(string userName, string password, int loginTimeoutMs, string terminator, string lineFeed = LegacyLineFeed)
        {
            var result = await TrySendUsernameAndPasswordAsync(userName, password, loginTimeoutMs, lineFeed).ConfigureAwait(false);
            if (result)
            {
                result = await IsTerminatedWithAsync(loginTimeoutMs, terminator).ConfigureAwait(false);
            }

            return result;
        }

        /// <inheritdoc/>
        public Task WriteLineAsync(string command)
        {
            return WriteAsync(string.Format("{0}{1}", command, LegacyLineFeed));
        }

        /// <inheritdoc/>
        public Task WriteLineRfc854Async(string command)
        {
            return WriteAsync(string.Format("{0}{1}", command, Rfc854LineFeed));
        }

        /// <inheritdoc/>
        public Task WriteLineAsync(string command, string lineFeed = LegacyLineFeed)
        {
            return WriteAsync(string.Format("{0}{1}", command, lineFeed));
        }

        /// <inheritdoc/>
        public async Task WriteAsync(string command, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(command);
            if (Settings.TextEncoding != null)
            {
                // Custom encoding: pre-encode here so the exact bytes hit the stream.
                await WriteAsync(ByteStringConverter.ConvertStringToByteArray(command, Settings.TextEncoding), cancellationToken).ConfigureAwait(false);
                return;
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, InternalCancellation.Token);
            if (ByteStream.Connected && !linked.Token.IsCancellationRequested)
            {
                await SendRateLimit.WaitAsync(linked.Token).ConfigureAwait(false);
                try
                {
                    await ByteStream.WriteAsync(command, linked.Token).ConfigureAwait(false);
                }
                finally
                {
                    SendRateLimit.Release();
                }
            }
        }

        /// <inheritdoc/>
        public async Task WriteAsync(byte[] data, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(data);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, InternalCancellation.Token);
            if (ByteStream.Connected && !linked.Token.IsCancellationRequested)
            {
                await SendRateLimit.WaitAsync(linked.Token).ConfigureAwait(false);
                try
                {
                    await ByteStream.WriteAsync(data, 0, data.Length, linked.Token).ConfigureAwait(false);
                }
                finally
                {
                    SendRateLimit.Release();
                }
            }
        }

        /// <inheritdoc/>
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

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, InternalCancellation.Token);
            if (ByteStream.Connected && !linked.Token.IsCancellationRequested)
            {
                await SendRateLimit.WaitAsync(linked.Token).ConfigureAwait(false);
                try
                {
                    // A command frame has no data bytes, so no IAC escaping is needed.
                    await ByteStream.WriteAsync([(byte)Commands.InterpretAsCommand, (byte)command], 0, 2, linked.Token).ConfigureAwait(false);
                }
                finally
                {
                    SendRateLimit.Release();
                }

                // RFC 1184 §5.8: a sent function with FLUSHIN/FLUSHOUT also fires
                // its flush actions. Runs after the semaphore is released — the
                // FLUSHOUT send takes it itself; FLUSHIN goes straight to the
                // separate OOB channel and takes no lock.
                await ProcessSlcFlushAsync(command, linked.Token).ConfigureAwait(false);
            }
        }

        /// <inheritdoc/>
        public async Task SendSynchAsync(CancellationToken cancellationToken = default)
        {
            // Out-of-band support lives behind TcpByteStream: fakes and custom
            // IByteStream implementations cannot send TCP urgent data, so fail
            // loudly instead of half-implementing. DM in normal mode stays a NOP.
            if (ByteStream is not TcpByteStream stream)
            {
                throw new NotSupportedException("Synch (TCP urgent data) requires a real TCP connection (TcpByteStream); the current byte stream does not support out-of-band sends.");
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, InternalCancellation.Token);
            if (ByteStream.Connected && !linked.Token.IsCancellationRequested)
            {
                await SendRateLimit.WaitAsync(linked.Token).ConfigureAwait(false);
                try
                {
                    // RFC 854 Synch: TCP Urgent notification with DM as the last (here
                    // the only) urgent octet. Verified on .NET 10 (loopback OOB spike).
                    await stream.SendUrgentAsync((byte)Commands.DataMark, linked.Token).ConfigureAwait(false);
                }
                finally
                {
                    SendRateLimit.Release();
                }
            }
        }

        /// <inheritdoc/>
        public async Task<byte> ReceiveUrgentAsync(CancellationToken cancellationToken = default)
        {
            if (ByteStream is not TcpByteStream stream)
            {
                throw new NotSupportedException("Urgent (out-of-band) receive requires a real TCP connection (TcpByteStream); the current byte stream does not support it.");
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, InternalCancellation.Token);
            return await stream.ReceiveUrgentAsync(linked.Token).ConfigureAwait(false);
        }

        /// <inheritdoc/>
        public Task<string> TerminatedReadAsync(string terminator)
        {
            return TerminatedReadAsync(terminator, TimeSpan.FromMilliseconds(Client.DefaultTimeoutMs));
        }

        /// <inheritdoc/>
        public Task<string> TerminatedReadAsync(string terminator, TimeSpan timeout)
        {
            return TerminatedReadAsync(terminator, timeout, 1);
        }

        /// <inheritdoc/>
        public Task<string> TerminatedReadAsync(Regex regex, TimeSpan timeout)
        {
            return TerminatedReadAsync(regex, timeout, 1);
        }

        /// <inheritdoc/>
        public Task<string> TerminatedReadAsync(IEnumerable<string> terminators)
        {
            return TerminatedReadAsync(terminators, TimeSpan.FromMilliseconds(Client.DefaultTimeoutMs));
        }

        /// <inheritdoc/>
        public Task<string> TerminatedReadAsync(IEnumerable<string> terminators, TimeSpan timeout)
        {
            return TerminatedReadAsync(terminators, timeout, 1);
        }

        /// <inheritdoc/>
        public Task<string> TerminatedReadAsync(IEnumerable<Regex> regexes)
        {
            return TerminatedReadAsync(regexes, TimeSpan.FromMilliseconds(Client.DefaultTimeoutMs));
        }

        /// <inheritdoc/>
        public Task<string> TerminatedReadAsync(IEnumerable<Regex> regexes, TimeSpan timeout)
        {
            return TerminatedReadAsync(regexes, timeout, 1);
        }

        /// <inheritdoc/>
        public Task<string> TerminatedReadAsync(string terminator, TimeSpan timeout, int millisecondSpin)
        {
            return TerminatedReadAsync(terminator, timeout, millisecondSpin, CancellationToken.None);
        }

        /// <inheritdoc/>
        public async Task<string> TerminatedReadAsync(string terminator, TimeSpan timeout, int millisecondSpin, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(terminator);
            bool isTerminated(string x) => Client.IsTerminatorLocated(terminator, x);
            var s = await TerminatedReadAsync(isTerminated, timeout, millisecondSpin, cancellationToken).ConfigureAwait(false);
            if (!isTerminated(s))
            {
                WriteLog(string.Format("Failed to terminate '{0}' with '{1}'", s, terminator));
            }

            return s;
        }

        /// <inheritdoc/>
        public Task<string> TerminatedReadAsync(Regex regex, TimeSpan timeout, int millisecondSpin)
        {
            return TerminatedReadAsync(regex, timeout, millisecondSpin, CancellationToken.None);
        }

        /// <inheritdoc/>
        public async Task<string> TerminatedReadAsync(Regex regex, TimeSpan timeout, int millisecondSpin, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(regex);
            bool isTerminated(string x) => Client.IsRegexLocated(regex, x);
            var s = await TerminatedReadAsync(isTerminated, timeout, millisecondSpin, cancellationToken).ConfigureAwait(false);
            if (!isTerminated(s))
            {
                WriteLog(string.Format("Failed to match '{0}' with '{1}'", s, regex.ToString()));
            }

            return s;
        }

        /// <inheritdoc/>
        public async Task<string> TerminatedReadAsync(IEnumerable<string> terminators, TimeSpan timeout, int millisecondSpin, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(terminators);
            bool isTerminated(string x) => Client.IsAnyTerminatorLocated(terminators, x);
            var s = await TerminatedReadAsync(isTerminated, timeout, millisecondSpin, cancellationToken).ConfigureAwait(false);
            if (!isTerminated(s))
            {
                WriteLog(string.Format("Failed to terminate '{0}' with any known terminator", s));
            }

            return s;
        }

        /// <inheritdoc/>
        public async Task<string> TerminatedReadAsync(IEnumerable<Regex> regexes, TimeSpan timeout, int millisecondSpin, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(regexes);
            bool isTerminated(string x) => Client.IsAnyRegexLocated(regexes, x);
            var s = await TerminatedReadAsync(isTerminated, timeout, millisecondSpin, cancellationToken).ConfigureAwait(false);
            if (!isTerminated(s))
            {
                WriteLog(string.Format("Failed to match '{0}' with any known pattern", s));
            }

            return s;
        }

        /// <inheritdoc/>
        public Task<string> ReadAsync()
        {
            return ReadAsync(TimeSpan.FromMilliseconds(Client.DefaultTimeoutMs));
        }

        /// <inheritdoc/>
        public Task<string> ReadAsync(TimeSpan timeout)
        {
            return ReadAsync(timeout, CancellationToken.None);
        }

        /// <inheritdoc/>
        public async Task<string> ReadAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            // Serialise concurrent reads so interleaved calls cannot split a reply.
            // A cancelled wait means "no data", not an error.
            try
            {
                await ReadRateLimit.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return string.Empty;
            }

            try
            {
                // A per-read linked source: an IP command aborts this read without
                // cancelling the client's own InternalCancellation (which must
                // survive for subsequent reads). Safe to dispose: the handler no
                // longer disposes (or cancels) anything it does not own.
                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(InternalCancellation.Token, cancellationToken))
                using (var handler = new ByteStreamHandler(ByteStream, linked, MillisecondReadDelay))
                {
                    FeedHandler(handler);
                    await MaybeSendEnvironmentInfoAsync().ConfigureAwait(false);
                    return await handler.ReadAsync(timeout).ConfigureAwait(false);
                }
            }
            finally
            {
                ReadRateLimit.Release();
            }
        }

        private async Task<bool> TrySendUsernameAndPasswordAsync(string userName, string password, int loginTimeoutMs, string lineFeed)
        {
            var result = await TryAwaitTerminatorThenSendAsync(userName, loginTimeoutMs, lineFeed).ConfigureAwait(false);
            if (result)
            {
                result = await TryAwaitTerminatorThenSendAsync(password, loginTimeoutMs, lineFeed).ConfigureAwait(false);
            }

            return result;
        }

        private async Task<bool> TryAwaitTerminatorThenSendAsync(string value, int loginTimeoutMs, string lineFeed)
        {
            var isTerminated = await IsTerminatedWithAsync(loginTimeoutMs, ":").ConfigureAwait(false);
            if (isTerminated)
            {
                await WriteLineAsync(value, lineFeed).ConfigureAwait(false);
            }

            return isTerminated;
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

        private async Task<bool> IsTerminatedWithAsync(int loginTimeoutMs, string terminator)
        {
            return (await TerminatedReadAsync(terminator, TimeSpan.FromMilliseconds(loginTimeoutMs), 1).ConfigureAwait(false)).TrimEnd().EndsWith(terminator);
        }
    }
}
