namespace telnet_cs.Server
{
    using telnet_cs.Protocol;
    using telnet_cs.Transport;

    /// <summary>
    /// Server-role LINEMODE (S4): the session is the DO-sender. MODE masks
    /// from the peer are answered with the server rules (EDIT/TRAPSIG kept,
    /// RFC 1184 §2.2) via the handler flag; SLC handling is role-symmetric
    /// except import requests (SLC func 0), which the session answers with
    /// its full table through the subnegotiation hook. FORWARDMASK and
    /// MODE/STATUS/TIMING-MARK peer replies need no new code (silent
    /// consumption and the shared snapshot path already do the right thing).
    /// </summary>
    public partial class ServerSession
    {
        private readonly LinemodeState linemodeState = new();

        /// <summary>Sets one LINEMODE SLC table row (test/setup hook).</summary>
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
        /// Sends a LINEMODE MODE mask to the peer (RFC 1184 §2.2). Sends nothing
        /// unless LINEMODE is agreed (the peer answered our DO). The peer's
        /// MODE+ACK answer is folded into the shared state by subsequent reads;
        /// an ACKed change the peer will not follow is adopted silently, per
        /// the server rule.
        /// </summary>
        /// <param name="mode">The MODE mask to propose (EDIT/TRAPSIG bits).</param>
        /// <param name="cancellationToken">A token to cancel the send.</param>
        /// <returns>An awaitable Task.</returns>
        public Task SendModeAsync(byte mode, CancellationToken cancellationToken = default)
        {
            if (!Negotiation.IsEnabledByPeer((int)Options.LineMode))
            {
                return Task.CompletedTask;
            }

            return SendLinemodeFrameAsync([LinemodeProtocol.Mode, mode], cancellationToken);
        }

        /// <summary>
        /// Proposes a FORWARDMASK to the peer (RFC 1184 §2.3: server-only DO
        /// plus 0–32 mask octets). Sends nothing unless LINEMODE is agreed.
        /// The peer's WILL/WONT answer is consumed silently by subsequent reads.
        /// </summary>
        /// <param name="mask">The forward-mask octets (at most 32).</param>
        /// <param name="cancellationToken">A token to cancel the send.</param>
        /// <returns>An awaitable Task.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="mask"/> has more than 32 octets.</exception>
        public Task SendForwardMaskAsync(byte[] mask, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(mask);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(mask.Length, 32);
            if (!Negotiation.IsEnabledByPeer((int)Options.LineMode))
            {
                return Task.CompletedTask;
            }

            return SendLinemodeFrameAsync([(byte)Commands.Do, LinemodeProtocol.ForwardMask, .. mask], cancellationToken);
        }

        /// <summary>
        /// Publishes the special characters to the peer (RFC 1184 §5.5):
        /// the explicitly configured rows, or the BSD reference defaults when
        /// nothing was configured. Sends nothing unless LINEMODE is agreed
        /// (the peer answered our DO).
        /// </summary>
        /// <param name="cancellationToken">A token to cancel the send.</param>
        /// <returns>An awaitable Task.</returns>
        public Task PublishSpecialCharactersAsync(CancellationToken cancellationToken = default)
        {
            if (!Negotiation.IsEnabledByPeer((int)Options.LineMode))
            {
                return Task.CompletedTask;
            }

            byte[]? triplets = linemodeState.ExportTriplets();
            if (triplets is null)
            {
                WriteLog("PublishSpecialCharacters: no special characters configured; nothing sent.");
                return Task.CompletedTask;
            }

            return SendLinemodeFrameAsync([LinemodeProtocol.SetLocalCharacters, .. triplets], cancellationToken);
        }

        /// <summary>
        /// Requests the peer's special-character table (RFC 1184 §2.4: SLC
        /// func 0 with DEFAULT). Sends nothing unless LINEMODE is agreed; the
        /// peer's answer is folded into the shared LINEMODE state by subsequent
        /// reads.
        /// </summary>
        /// <param name="cancellationToken">A token to cancel the send.</param>
        /// <returns>An awaitable Task.</returns>
        public Task RequestRemoteSpecialCharactersAsync(CancellationToken cancellationToken = default)
        {
            if (!Negotiation.IsEnabledByPeer((int)Options.LineMode))
            {
                return Task.CompletedTask;
            }

            return SendLinemodeFrameAsync(
              [LinemodeProtocol.SetLocalCharacters, 0, LinemodeProtocol.LevelDefault, 0], cancellationToken);
        }

        /// <summary>
        /// Receives a single byte with TCP urgent (out-of-band) semantics: the
        /// receive side of the RFC 854 Synch signal (TCP Urgent + DM). Requires
        /// the byte stream to be a <see cref="TcpByteStream"/> over a real TCP
        /// connection; otherwise throws <see cref="NotSupportedException"/>.
        /// </summary>
        /// <param name="cancellationToken">A token to cancel the receive.</param>
        /// <returns>The urgent byte received (DM, 242, for a Synch).</returns>
        public async Task<byte> ReceiveUrgentAsync(CancellationToken cancellationToken = default)
        {
            if (ByteStream is not TcpByteStream stream)
            {
                throw new NotSupportedException("Urgent (out-of-band) receive requires a real TCP connection (TcpByteStream); the current byte stream does not support it.");
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
              cancellationToken, InternalCancellation.Token);
            return await stream.ReceiveUrgentAsync(linked.Token).ConfigureAwait(false);
        }

        /// <summary>
        /// Answers peer SLC import requests (RFC 1184 §2.4 func 0). Func 0 with
        /// DEFAULT ("send your table") resets the working table to the
        /// configured defaults (telnetlib3 <c>_slc_process</c>) and is answered
        /// with the full table, every NOSUPPORT row rendered as
        /// <c>[func, DEFAULT, 0]</c> so the peer may use its own values (never
        /// silent: the RFC says "send all those special characters"). Func 0
        /// with VALUE ("send current settings") is answered with the normal
        /// configured-rows export, silent when nothing is configured. Only
        /// the low two level bits are compared (the ACK and flush modifier
        /// bits ride along on replies), and the value octet is ignored — func
        /// 0 is a request, not a table row. Anything else is left for the
        /// normal SLC path.
        /// </summary>
        /// <param name="payload">The LINEMODE payload.</param>
        /// <returns>True when the payload was an import request (consumed).</returns>
        private bool TryConsumeLinemodeImport(List<byte> payload)
        {
            if (payload.Count != 4
              || payload[0] != LinemodeProtocol.SetLocalCharacters
              || payload[1] != 0)
            {
                return false;
            }

            byte level = (byte)(payload[2] & LinemodeProtocol.LevelBits);
            if (level != LinemodeProtocol.LevelDefault && level != LinemodeProtocol.LevelValue)
            {
                return false;
            }

            bool defaults = payload[2] == LinemodeProtocol.LevelDefault;
            if (defaults)
            {
                linemodeState.ResetToDefaults();
            }
            byte[]? triplets = linemodeState.ExportTriplets(forImport: defaults);
            if (triplets is null)
            {
                WriteLog("Linemode current settings requested with no special characters configured; nothing sent.");
                return true;
            }

            WriteLog("Sending: " + nameof(Options.LineMode) + " SLC table.");
            // Fire-and-forget: the reply rides SendRateLimit, so wire order
            // against other sends is still safe; a send failure here is
            // unobserved by design (the import was already consumed).
            _ = SendLinemodeFrameAsync([LinemodeProtocol.SetLocalCharacters, .. triplets], CancellationToken.None);
            return true;
        }

        /// <summary>
        /// RFC 1184 §5.8 flush side-effects for a just-sent control command:
        /// when the SLC table row for the sent function carries FLUSHIN, a
        /// Telnet Synch (urgent DM) goes out at the same time; when it carries
        /// FLUSHOUT, a DO TIMING-MARK follows. Sends nothing unless LINEMODE is
        /// agreed (the peer answered our DO) — without that gate every
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
              || !Negotiation.IsEnabledByPeer((int)Options.LineMode)
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
                await RequestEnableAsync(Options.TimingMark, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task SendLinemodeFrameAsync(byte[] payload, CancellationToken cancellationToken)
        {
            byte[] frame = EnvironmentProtocol.FrameSubnegotiation((int)Options.LineMode, payload);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
              cancellationToken, InternalCancellation.Token);
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
