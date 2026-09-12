namespace telnet_cs.Client
{
    using System.Threading;
    using System.Threading.Tasks;
    using telnet_cs.Protocol;
    using telnet_cs.Transport;

    public partial class Client
    {
        /// <summary>
        /// Requests the server's special-character table (RFC 1184 §2.4: SLC
        /// func 0 with DEFAULT). Sends nothing unless LINEMODE is agreed (we
        /// are the WILL-sender); the server's answer is folded into the shared
        /// LINEMODE state by subsequent reads.
        /// </summary>
        /// <param name="cancellationToken">A token to cancel the send.</param>
        /// <returns>An awaitable Task.</returns>
        public Task ImportRemoteSpecialCharactersAsync(CancellationToken cancellationToken = default)
        {
            if (!Negotiation.IsEnabledByUs((int)Options.LineMode))
            {
                return Task.CompletedTask;
            }

            var frame = EnvironmentProtocol.FrameSubnegotiation(
              (int)Options.LineMode,
              [LinemodeProtocol.SetLocalCharacters, 0, LinemodeProtocol.LevelDefault, 0]);
            return SendFrameAsync(frame, cancellationToken);
        }

        /// <summary>
        /// Exports the configured special characters to the server (RFC 1184
        /// §5.5). Sends nothing unless LINEMODE is agreed, and nothing at all
        /// when no SLC row is configured (an all-NOSUPPORT export would wrongly
        /// tell the server to disable everything).
        /// </summary>
        /// <param name="cancellationToken">A token to cancel the send.</param>
        /// <returns>An awaitable Task.</returns>
        public Task ExportSpecialCharactersAsync(CancellationToken cancellationToken = default)
        {
            if (!Negotiation.IsEnabledByUs((int)Options.LineMode))
            {
                return Task.CompletedTask;
            }

            byte[]? triplets = linemodeState.ExportTriplets();
            if (triplets is null)
            {
                WriteLog("ExportSpecialCharacters: no special characters configured; nothing sent.");
                return Task.CompletedTask;
            }

            var frame = EnvironmentProtocol.FrameSubnegotiation(
              (int)Options.LineMode,
              [LinemodeProtocol.SetLocalCharacters, .. triplets]);
            return SendFrameAsync(frame, cancellationToken);
        }

        /// <summary>
        /// Sets one LINEMODE SLC table row (test/setup hook).
        /// </summary>
        /// <param name="function">The SLC function code (1–30).</param>
        /// <param name="level">The agreement level.</param>
        /// <param name="value">The character value.</param>
        /// <param name="flags">The FLUSHIN/FLUSHOUT modifier bits.</param>
        internal void SetLinemodeEntry(byte function, byte level, byte value, byte flags = 0) =>
          linemodeState.SetEntry(function, level, value, flags);

        /// <summary>
        /// Reads one LINEMODE SLC table row (test/observation hook, symmetric
        /// with <see cref="SetLinemodeEntry"/>).
        /// </summary>
        /// <param name="function">The SLC function code (1–30).</param>
        /// <returns>The stored level, value and flags.</returns>
        internal SlcEntry GetLinemodeEntry(byte function) => linemodeState.GetEntry(function);

        /// <summary>
        /// Reads the agreed LINEMODE MODE mask (test/observation hook).
        /// </summary>
        /// <returns>The MODE mask without the ACK bit.</returns>
        internal byte GetLinemodeMode() => linemodeState.Mode;

        /// <summary>
        /// RFC 1184 §5.8 flush side-effects for a just-sent control command:
        /// when the SLC table row for the sent function carries FLUSHIN, a
        /// Telnet Synch (urgent DM) goes out at the same time; when it carries
        /// FLUSHOUT, a DO TIMING-MARK follows. Sends nothing unless LINEMODE is
        /// agreed (we are the WILL-sender) — without that gate every
        /// <c>SendCommand</c> on a non-linemode session would emit DO
        /// TIMING-MARK unrequested. On a non-TCP stream the Synch half is logged
        /// and skipped rather than throwing: an automatic side-effect must not
        /// break sends on fakes or pipes.
        /// </summary>
        /// <param name="command">The control command that was just sent.</param>
        /// <param name="cancellationToken">A token to cancel the flush sends.</param>
        private async Task ProcessSlcFlushAsync(Commands command, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested
              || !Negotiation.IsEnabledByUs((int)Options.LineMode)
              || LinemodeProtocol.SlcFunctionForCommand(command) is not byte function)
            {
                return;
            }

            SlcEntry entry = linemodeState.GetEntry(function);
            if ((entry.Flags & LinemodeProtocol.FlagFlushIn) != 0)
            {
                if (ByteStream is TcpByteStream stream && ByteStream.Connected)
                {
                    await stream.SendUrgentAsync((byte)Commands.DataMark, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    WriteLog("Linemode FLUSHIN is set but the byte stream is not a connected TcpByteStream; Synch skipped.");
                }
            }

            if ((entry.Flags & LinemodeProtocol.FlagFlushOut) != 0)
            {
                await SendTimingMarkAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task SendFrameAsync(byte[] frame, CancellationToken cancellationToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
              cancellationToken, InternalCancellation.Token);
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

        private readonly LinemodeState linemodeState = new();
    }
}
