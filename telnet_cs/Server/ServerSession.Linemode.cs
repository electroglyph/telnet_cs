namespace telnet_cs.Server;

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
    /// <summary>Sets one LINEMODE SLC table row (test/setup hook).</summary>
    /// <param name="function">The SLC function code (1–30).</param>
    /// <param name="level">The agreement level.</param>
    /// <param name="value">The character value.</param>
    /// <param name="flags">The FLUSHIN/FLUSHOUT modifier bits.</param>
    internal void SetLinemodeEntry(byte function, byte level, byte value, byte flags = 0) =>
      SharedLinemodeState.SetEntry(function, level, value, flags);

    /// <summary>
    /// Reads one LINEMODE SLC table row (test/observation hook, symmetric
    /// with <see cref="SetLinemodeEntry"/>).
    /// </summary>
    /// <param name="function">The SLC function code (1–30).</param>
    /// <returns>The stored level, value and flags.</returns>
    internal SlcEntry GetLinemodeEntry(byte function) => SharedLinemodeState.GetEntry(function);

    /// <summary>
    /// Reads the agreed LINEMODE MODE mask (test/observation hook).
    /// </summary>
    /// <returns>The MODE mask without the ACK bit.</returns>
    internal byte GetLinemodeMode() => SharedLinemodeState.Mode;

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

        byte[]? payload = LinemodeProtocol.BuildSlcExportPayload(SharedLinemodeState);
        if (payload is null)
        {
            WriteLog("PublishSpecialCharacters: no special characters configured; nothing sent.");
            return Task.CompletedTask;
        }

        return SendLinemodeFrameAsync(payload, cancellationToken);
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
          LinemodeProtocol.BuildSlcImportPayload(), cancellationToken);
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
    /// Answers peer SLC import requests (RFC 1184 §2.4 func 0), single or
    /// batched with other triplets (telnetlib3 <c>_slc_set</c> loops the
    /// whole list, so a func 0 rides along with per-triplet replies in one
    /// SLC frame). Triplets are processed in order into a single reply
    /// frame: a func 0 with DEFAULT ("send your table", level compared on
    /// the low two level bits per RFC 1184 §2.4 — ACK and flush bits ride
    /// along) resets the working table to the configured defaults and
    /// appends the full table, every NOSUPPORT row rendered as
    /// <c>[func, DEFAULT, 0]</c> so the peer may use its own values (never
    /// silent: the RFC says "send all those special characters"); a func 0
    /// with VALUE ("send current settings") appends the normal
    /// configured-rows export, silent when nothing is configured; other
    /// triplets go through the server SLC rules with their replies
    /// appended. The value octet of a func 0 is ignored — it is a request,
    /// not a table row. A DO FORWARDMASK trailer follows every answered
    /// block once LINEMODE is agreed on either side (reference
    /// <c>request_forwardmask</c>). Anything without a DEFAULT/VALUE func 0
    /// is left for the normal SLC path.
    /// </summary>
    /// <param name="payload">The LINEMODE payload.</param>
    /// <returns>True when the payload was an import request (consumed).</returns>
    private bool TryConsumeLinemodeImport(List<byte> payload)
    {
        if (payload.Count < 4
          || payload[0] != LinemodeProtocol.SetLocalCharacters
          || (payload.Count - 1) % 3 != 0
          || !ContainsImportRequest(payload))
        {
            return false;
        }

        List<byte> replies = [];
        foreach (var (function, modifier, value) in LinemodeProtocol.EnumerateSlcTriplets(payload))
        {
            if (LinemodeProtocol.IsSlcImportRequest(function, modifier))
            {
                bool defaults = (byte)(modifier & LinemodeProtocol.LevelBits) == LinemodeProtocol.LevelDefault;
                if (defaults)
                {
                    SharedLinemodeState.ResetToDefaults();
                }

                byte[]? triplets = SharedLinemodeState.ExportTriplets(forImport: defaults);
                if (triplets is not null)
                {
                    replies.AddRange(triplets);
                }
            }
            else if (function != 0)
            {
                (byte Modifier, byte Value)? reply =
                  SharedLinemodeState.ApplySlcAsServer(function, modifier, value);
                if (reply is not null)
                {
                    LinemodeProtocol.AppendSlcTriplet(replies, function, reply.Value.Modifier, reply.Value.Value);
                }
            }
            // A func 0 at any other level is not an import request:
            // dropped silently, like the normal SLC path does.
        }

        if (replies.Count == 0)
        {
            WriteLog("Linemode import request answered with no rows; nothing sent.");
            return true;
        }

        WriteLog("Sending: " + nameof(Options.LineMode) + " SLC table.");
        // Fire-and-forget: the reply rides SendRateLimit, so wire order
        // against other sends is still safe; a send failure here is
        // unobserved by design (the import was already consumed).
        _ = SendLinemodeFrameAsync([LinemodeProtocol.SetLocalCharacters, .. replies], CancellationToken.None);
        if (Negotiation.IsEnabledByUs((int)Options.LineMode) || Negotiation.IsEnabledByPeer((int)Options.LineMode))
        {
            byte[] mask = LinemodeProtocol.BuildForwardMask(Negotiation.IsEnabledByUs((int)Options.TransmitBinary));
            WriteLog("Sending: " + nameof(Options.LineMode) + " DO FORWARDMASK.");
            _ = SendLinemodeFrameAsync([(byte)Commands.Do, LinemodeProtocol.ForwardMask, .. mask], CancellationToken.None);
        }
        else
        {
            WriteLog("Skipping LINEMODE DO FORWARDMASK without LINEMODE agreement.");
        }

        return true;
    }

    /// <summary>
    /// Scans an SLC payload for a func 0 triplet at DEFAULT or VALUE level
    /// (level compared on the low two level bits — ACK and flush bits ride
    /// along). Caller guarantees an aligned triplet tail.
    /// </summary>
    /// <param name="payload">The LINEMODE payload.</param>
    /// <returns>True when the payload carries an import request.</returns>
    private static bool ContainsImportRequest(List<byte> payload)
    {
        foreach (var (function, modifier, _) in LinemodeProtocol.EnumerateSlcTriplets(payload))
        {
            if (LinemodeProtocol.IsSlcImportRequest(function, modifier))
            {
                return true;
            }
        }

        return false;
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

        SlcEntry entry = SharedLinemodeState.GetEntry(function);
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

    private Task SendLinemodeFrameAsync(byte[] payload, CancellationToken cancellationToken)
    {
        byte[] frame = EnvironmentProtocol.FrameSubnegotiation((int)Options.LineMode, payload);
        return SendFrameLockedAsync(frame, cancellationToken, Context.NoteWritten);
    }
}
