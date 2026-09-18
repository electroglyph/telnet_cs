namespace telnet_cs.IO;

using System.Text;
using telnet_cs.Protocol;
using telnet_cs.Transport;
/// <summary>
/// Byte-level wire pump: single-byte lookahead reads, MCCP feed, drain and teardown, plus RFC 854 Synch discard scanning. Split from <see cref="ByteStreamHandler"/>; wire behavior is unchanged.
/// </summary>
public partial class ByteStreamHandler
{

    /// <summary>
    /// Enters RFC 854 Synch discard mode: data is dropped until in-band
    /// <c>IAC DM</c>. The urgent-data trigger calls this automatically on
    /// real TCP streams; tests call it directly for hermetic coverage.
    /// </summary>
    internal void EnterSynchDiscard()
    {
        InSynchDiscard = true;
    }

    /// <summary>
    /// Non-blocking RFC 854 Synch trigger plus scan entry: consumes one
    /// pending TCP urgent byte when the stream is a real
    /// <see cref="TcpByteStream"/> with urgent data waiting (and we are not
    /// already discarding), then runs the discard scan while the mode holds.
    /// Split out of <c>RetrieveAndParseResponse</c> to keep that method
    /// under the complexity gate. Returns null when not discarding.
    /// </summary>
    private async Task<bool?> RetrieveSynchDiscardAsync(StringBuilder sb, List<byte> rawBytes, List<int> opByteCounts)
    {
        if (PollSynchTrigger())
        {
            EnterSynchDiscard();
        }

        return InSynchDiscard
          ? await RetrieveAndParseSynchDiscard(sb, rawBytes, opByteCounts).ConfigureAwait(false)
          : null;
    }

    /// <summary>
    /// Non-blocking RFC 854 Synch trigger: consumes one pending TCP urgent
    /// byte when the stream is a real <see cref="TcpByteStream"/> with
    /// urgent data waiting (and we are not already discarding). Split out
    /// of <c>RetrieveAndParseResponse</c> to keep that method under the
    /// complexity gate.
    /// </summary>
    /// <returns>True when a Synch scan should start.</returns>
    private bool PollSynchTrigger()
    {
        return !InSynchDiscard && byteStream is TcpByteStream tcp && tcp.TryConsumeUrgentSignal() is not null;
    }

    /// <summary>
    /// RFC 854 Synch scan: discard data until <c>IAC DM</c>. Interesting
    /// signals and all other commands dispatch through the normal
    /// <see cref="InterpretNextAsCommand"/> path; EC/EL are swallowed (the
    /// RFC excludes them); escaped IAC is data (dropped). Nothing here
    /// surfaces, so this always returns false; the mode ends only at DM,
    /// so end-of-urgent never cuts the scan short and a later urgent byte
    /// re-triggers a fresh scan.
    /// </summary>
    private async Task<bool> RetrieveAndParseSynchDiscard(StringBuilder sb, List<byte> rawBytes, List<int> opByteCounts)
    {
        var input = ReadNextByte();
        if (input != IacByte)
        {
            // Data (or end-of-stream): dropped, never surfaced.
            return false;
        }

        var verb = TryReadByte();
        if (verb == -1)
        {
            return false;
        }

        if (verb == (int)Commands.DataMark)
        {
            InSynchDiscard = false;
            return false;
        }

        if (verb is (int)Commands.EraseCharacter or (int)Commands.EraseLine)
        {
            return false;
        }

        await InterpretNextAsCommand(sb, rawBytes, opByteCounts, verb).ConfigureAwait(false);
        return false;
    }

    private void NoteInboundByte(int raw)
    {
        if (raw == -1)
        {
            return;
        }

        InboundWireBytes++;
        if (firstInboundByte == -1)
        {
            firstInboundByte = raw;
        }
    }

    /// <summary>
    /// Writes <paramref name="count"/> bytes to the peer and accounts
    /// them as outbound wire bytes. The single choke point for every
    /// handler reply, so session counters match the reference
    /// <c>len(buf)</c> transmit accounting.
    /// </summary>
    private async Task WriteWireAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        await byteStream.WriteAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
        OutboundWireBytes += count;
    }

    /// <summary>
    /// Reads the next byte, honouring the single-byte pushback stash.
    /// I/O failures (<see cref="System.IO.IOException"/>, including read
    /// timeouts) and over-reads surface as -1; anything else the stream
    /// throws (notably <see cref="System.Net.Sockets.SocketException"/>)
    /// propagates to the caller.
    /// </summary>
    private int ReadNextByte()
    {
        if (pushbackByte.HasValue)
        {
            var pending = pushbackByte.Value;
            pushbackByte = null;
            return pending;
        }

        return TryReadByte();
    }

    /// <summary>
    /// Consumed-byte entry point: every successfully taken input byte
    /// clears <see cref="SlcReceived"/> (telnetlib3 resets
    /// <c>slc_received</c> at the top of <c>feed_byte</c>), so only a
    /// data byte delivered through the snooping appends can set it.
    /// </summary>
    private int TryReadByte()
    {
        var taken = TryReadByteCore();
        if (taken != -1)
        {
            SlcReceived = null;
        }

        return taken;
    }

    /// <summary>
    /// Blind continuation read: never polls <see cref="IByteStream.Available"/>
    /// (fakes and real sockets alike may report 0 mid-sequence), mapping I/O
    /// and over-read failures to -1. While an MCCP stream is armed, serves
    /// decompressed output (feeding whatever the wire reports available);
    /// when the stream stalls mid-flow with an empty wire, the next wire
    /// byte is awaited and fed to the inflater too — it belongs to the
    /// compressed stream, and consuming it raw would desync inflation
    /// (the inflater misses a byte and fails on the one after).
    /// After a clean stream end serves the queued post-stream plaintext.
    /// </summary>
    /// <summary>
    /// Wire-byte entry point: every definitive end-of-stream from the
    /// transport (peer FIN, TLS close_notify, pipe close) closes the
    /// stream, so <c>Connected</c> flips false and every layer above
    /// observes the disconnect instead of spinning on empty reads.
    /// Only -1 closes, and only on transports where -1 is definitive
    /// end-of-stream (<see cref="Transport.TcpByteStream"/> reports -1
    /// solely for a socket 0-byte read, a dead socket, or disposal —
    /// never for "drained"; <see cref="Transport.DuplexPipe"/> reports
    /// -1 solely once an end is closed). Scripted or otherwise
    /// refillable streams also report -1 when merely drained, so they
    /// keep the historical no-close behavior here. Timeout expirations
    /// surface as <see cref="System.IO.IOException"/>, never -1, so a
    /// transient stall can never close the session.
    /// </summary>
    /// <returns>The unsigned byte cast to an integer, or -1 if at the end of the stream.</returns>
    private int TakeWireByte()
    {
        int next = byteStream.ReadByte();
        if (next == -1 && byteStream is Transport.TcpByteStream or Transport.DuplexEnd)
        {
            byteStream.Close();
        }

        return next;
    }

    private int TryReadByteCore()
    {
        var mccp = MccpStream;
        if (mccp is not null)
        {
            if (mccp.TryTakeReady(out var inflated))
            {
                return inflated;
            }

            if (Mccp2Active || Mccp3Active)
            {
                // A stall with an empty wire must not fall through to the
                // raw read below: sync-flushed MCCP chunks arrive
                // separately, and the byte that ends the stall is still
                // compressed. Loop until output, end, failure, or I/O
                // timeout/close; the blocking feed waits exactly as long
                // as the raw read would have.
                while (true)
                {
                    DrainMccp(mccp);
                    if (mccp.Failed)
                    {
                        break;
                    }

                    if (mccp.StreamEnded)
                    {
                        FinishMccpStream();
                    }

                    if (mccp.TryTakeReady(out inflated))
                    {
                        return inflated;
                    }

                    if (!mccp.IsActive)
                    {
                        break;
                    }

                    int next;
                    try
                    {
                        next = TakeWireByte();
                    }
                    catch (System.IO.IOException)
                    {
                        return -1;
                    }
                    catch (InvalidOperationException)
                    {
                        return -1;
                    }

                    if (next == -1)
                    {
                        return -1;
                    }

                    NoteInboundByte(next);
                    mccp.Feed((byte)next);
                }

                if (mccp.Failed)
                {
                    ShutdownMccpCorrupt();
                    // A corrupt chunk feeds the reader nothing: whatever
                    // is still buffered belonged to the failed stream, so
                    // discard it instead of parsing garbage as telnet.
                    // Bytes arriving in later reads parse raw as normal
                    // (agreement has ended).
                    while (byteStream.Available > 0)
                    {
                        int dropped;
                        try
                        {
                            dropped = TakeWireByte();
                        }
                        catch (System.IO.IOException)
                        {
                            break;
                        }
                        catch (InvalidOperationException)
                        {
                            break;
                        }

                        if (dropped == -1)
                        {
                            break;
                        }

                        NoteInboundByte(dropped);
                    }
                }
            }
            else if (mccp.TryTakeTrailing(out var resumed))
            {
                return resumed;
            }
        }

        try
        {
            int raw = TakeWireByte();
            NoteInboundByte(raw);
            return raw;
        }
        catch (System.IO.IOException)
        {
            return -1;
        }
        catch (InvalidOperationException)
        {
            return -1;
        }
    }

    /// <summary>
    /// Feeds the MCCP decompressor from whatever the wire reports
    /// available (never blocking: a stall simply waits for the next
    /// read; the decompressor's footer probe tells stall from Z_FINISH).
    /// </summary>
    /// <param name="mccp">The session-owned decompressor.</param>
    private void DrainMccp(MccpDecompressor mccp)
    {
        while (!mccp.HasOutput && !mccp.StreamEnded && !mccp.Failed && byteStream.Available > 0)
        {
            int raw;
            try
            {
                raw = byteStream.ReadByte();
            }
            catch (System.IO.IOException)
            {
                return;
            }
            catch (InvalidOperationException)
            {
                return;
            }

            if (raw == -1)
            {
                return;
            }

            NoteInboundByte(raw);
            mccp.Feed((byte)raw);
        }
    }

    /// <summary>
    /// Ends MCCP agreement after a clean Z_FINISH: the flags drop so
    /// later bytes read raw again, while the queued post-stream
    /// plaintext keeps serving from the session-owned stream.
    /// </summary>
    private void FinishMccpStream()
    {
        WriteLog("MCCP stream ended; resuming plaintext.");
        Mccp2Active = false;
        Mccp3Active = false;
        MccpStateChanged?.Invoke(false, false, MccpStream);
    }

    /// <summary>
    /// Ends MCCP agreement after corrupt data: queued output is already
    /// dropped by the decompressor (the reader is fed nothing), the
    /// stream reference is released, and a refusal goes out at the next
    /// async flush point. The refusal is this stack's extension (the
    /// reference only clears state and logs): WONT withdraws our own
    /// offer, DONT refuses the peer's (chosen when the stream armed).
    /// </summary>
    private void ShutdownMccpCorrupt()
    {
        WriteLog("MCCP decompression failed; refusing compression and resuming plaintext.");
        Mccp2Active = false;
        Mccp3Active = false;
        MccpStream = null;
        MccpStateChanged?.Invoke(false, false, null);
        if (mccpArmedWont)
        {
            Negotiation.ReceivedDont(mccpShutdownOption);
        }
        else
        {
            Negotiation.ReceivedWont(mccpShutdownOption);
        }

        mccpShutdownWont = mccpArmedWont;
        mccpShutdownPending = true;
    }

    /// <summary>
    /// Ends inbound MCCP inflation after the peer takes agreement back
    /// (<c>IAC WONT</c> / <c>IAC DONT</c>): stops feeding the
    /// decompressor and reports the loss session-side.
    /// Already-buffered bytes still drain (the stream reference is kept,
    /// like <see cref="FinishMccpStream"/>). Outbound compression is
    /// deliberately untouched: the reference never stops its compressor
    /// on WONT/DONT, so our write views keep running to disconnect.
    /// </summary>
    /// <param name="inputOption">The MCCP option that ended.</param>
    private void TeardownInboundMccp(int inputOption)
    {
        if (inputOption == (int)Options.Mccp2)
        {
            Mccp2Active = false;
        }
        else
        {
            Mccp3Active = false;
        }

        mccpShutdownPending = false;
        mccpShutdownWont = false;
        MccpStateChanged?.Invoke(Mccp2Active, Mccp3Active, MccpStream);
    }

    private async Task FlushMccpShutdownAsync()
    {
        if (mccpShutdownPending)
        {
            mccpShutdownPending = false;
            bool sendWont = mccpShutdownWont;
            mccpShutdownWont = false;
            if (sendWont)
            {
                await SendWont(mccpShutdownOption).ConfigureAwait(false);
            }
            else
            {
                await SendDont(mccpShutdownOption).ConfigureAwait(false);
            }
        }
    }
}
