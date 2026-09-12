namespace telnet_cs.Client
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using telnet_cs.Protocol;

    public abstract partial class BaseClient
    {
        /// <summary>
        /// The poll slice for <see cref="WaitForNegotiationAsync"/>: each
        /// iteration pumps at most this much wire time before re-checking the
        /// condition, so a satisfied condition is observed promptly without
        /// busy-spinning.
        /// </summary>
        private static readonly TimeSpan NegotiationPollSlice = TimeSpan.FromMilliseconds(50);

        /// <summary>
        /// Gets the persistent RFC 1143 negotiation state for this connection.
        /// Both roles keep one (<c>Client.Negotiation</c>,
        /// <c>ServerSession.Negotiation</c>); this accessor lets the shared
        /// helpers below consult it without knowing the role.
        /// </summary>
        protected abstract NegotiationState SessionNegotiation { get; }

        /// <summary>
        /// Reads asynchronously from the connection. Implemented per role
        /// (<c>Client</c>, <c>ServerSession</c>); abstract here so
        /// <see cref="WaitForNegotiationAsync"/> can pump the wire.
        /// </summary>
        /// <param name="timeout">The timeout.</param>
        /// <param name="cancellationToken">Token to cancel the read. Cancellation returns the partial text read so far.</param>
        /// <returns>Any text read from the connection.</returns>
        public abstract Task<string> ReadAsync(TimeSpan timeout, CancellationToken cancellationToken);

        /// <summary>
        /// Sends <c>IAC GA</c> (Go-Ahead, RFC 854) unless Suppress-GA is in
        /// effect in either direction, in which case GA is a NOP and nothing
        /// is sent. Mirrors the reference <c>send_ga</c>, which returns
        /// <c>False</c> once the local WILL-SGA state holds after
        /// <c>DO SGA</c> (unlike <c>SendCommand(Commands.GoAhead)</c>, which
        /// transmits unconditionally).
        /// </summary>
        /// <param name="cancellationToken">A token to cancel the send.</param>
        /// <returns><c>true</c> when the GA byte pair was sent, <c>false</c> when SGA suppressed it.</returns>
        public async Task<bool> SendGaAsync(CancellationToken cancellationToken = default)
        {
            if (SessionNegotiation.IsEnabledByUs((int)Options.SuppressGoAhead) ||
                SessionNegotiation.IsEnabledByPeer((int)Options.SuppressGoAhead))
            {
                return false;
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, InternalCancellation.Token);
            if (!ByteStream.Connected || linked.Token.IsCancellationRequested)
            {
                return false;
            }

            await SendRateLimit.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                await ByteStream.WriteAsync([(byte)Commands.InterpretAsCommand, (byte)Commands.GoAhead], 0, 2, linked.Token).ConfigureAwait(false);
            }
            finally
            {
                SendRateLimit.Release();
            }

            return true;
        }

        /// <summary>
        /// Pumps the wire until <paramref name="condition"/> holds of the
        /// session negotiation state, the <paramref name="timeout"/> elapses,
        /// or <paramref name="cancellationToken"/> fires. The generic
        /// counterpart to the per-collector <c>PollForResponseAsync</c> pollers:
        /// where those wait for one subnegotiation reply, this waits for any
        /// caller-supplied negotiation predicate (the reference
        /// <c>wait_for</c>/<c>wait_for_condition</c> hook).
        /// </summary>
        /// <param name="condition">The predicate over the live negotiation state.</param>
        /// <param name="timeout">The maximum time to wait.</param>
        /// <param name="cancellationToken">Token to cancel the wait.</param>
        /// <returns><c>true</c> when the condition held before the deadline, otherwise <c>false</c> (timeout or cancellation).</returns>
        public async Task<bool> WaitForNegotiationAsync(Func<NegotiationState, bool> condition, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(condition);
            var deadline = DateTimeOffset.UtcNow + timeout;
            var buffered = string.Empty;
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    if (condition(SessionNegotiation))
                    {
                        return true;
                    }

                    var remaining = deadline - DateTimeOffset.UtcNow;
                    if (remaining <= TimeSpan.Zero)
                    {
                        return false;
                    }

                    try
                    {
                        var pumped = await ReadAsync(remaining < NegotiationPollSlice ? remaining : NegotiationPollSlice, cancellationToken).ConfigureAwait(false);
                        if (!string.IsNullOrEmpty(pumped))
                        {
                            buffered += pumped;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        return false;
                    }
                }

                return false;
            }
            finally
            {
                if (!string.IsNullOrEmpty(buffered))
                {
                    PendingText = buffered + PendingText;
                }
            }
        }

        /// <summary>
        /// Pumps the wire until <paramref name="telnetOption"/> is enabled on
        /// the requested side (<paramref name="local"/> selects our grant vs
        /// the peer's), the <paramref name="timeout"/> elapses, or
        /// <paramref name="cancellationToken"/> fires. Named-option form of
        /// <see cref="WaitForNegotiationAsync"/> (the reference
        /// <c>wait_for(remote/local, ...)</c> hook).
        /// </summary>
        /// <param name="telnetOption">The option to wait for.</param>
        /// <param name="local"><c>true</c> to wait for our grant, <c>false</c> for the peer's.</param>
        /// <param name="timeout">The maximum time to wait.</param>
        /// <param name="cancellationToken">Token to cancel the wait.</param>
        /// <returns><c>true</c> when the option enabled before the deadline, otherwise <c>false</c>.</returns>
        public Task<bool> WaitForOptionEnabledAsync(Options telnetOption, bool local, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            return WaitForNegotiationAsync(
              negotiation => local
                ? negotiation.IsEnabledByUs((int)telnetOption)
                : negotiation.IsEnabledByPeer((int)telnetOption),
              timeout,
              cancellationToken);
        }
    }
}
