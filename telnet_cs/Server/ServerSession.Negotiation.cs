namespace telnet_cs.Server;

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
    /// <c>TelnetSessionBase</c> GA/waiter helpers.
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
        if (Settings.DisableAllNegotiation)
        {
            return;
        }

        if (Settings.RequestTerminalType)
        {
            await RequestEnableAsync(Options.TerminalType, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Sends the server advanced preset once negotiation advances (the
    /// reference <c>begin_advanced_negotiation</c>, gated by
    /// <c>negotiation_should_advance</c>): <c>WILL SGA</c> (suppressed
    /// when linemode is requested — the reference stays in NVT line mode),
    /// <c>WILL BINARY</c>, <c>DO NAWS</c> and <c>DO CHARSET</c> per the matching
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
        if (Settings.OfferSuppressGoAhead && !Settings.RequestLinemode)
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
            var (us, him) = Negotiation[option];
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
        if (Settings.DisableAllNegotiation)
        {
            return Task.CompletedTask;
        }

        var verb = ResolveEnableRequest(Negotiation, telnetOption);
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
        if (Settings.DisableAllNegotiation)
        {
            return Task.CompletedTask;
        }

        return SendRequestAsync(ResolveDisableRequest(Negotiation, telnetOption), telnetOption, cancellationToken);
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
        if (!TelnetCommands.IsStandaloneControl(command))
        {
            throw new ArgumentOutOfRangeException(
              nameof(command), command, "Only standalone control commands (BRK, IP, AO, AYT, EC, EL, GA, NOP, EOF, SUSP, ABORT) can be sent with SendCommand. Option negotiation verbs (DO, DONT, WILL, WONT, SB, SE, IAC) go through the RFC 1143 negotiation API.");
        }

        if (command == Commands.GoAhead && Negotiation.IsEnabledByUs((int)Options.SuppressGoAhead))
        {
            return;
        }

        if (await SendFrameLockedAsync([(byte)Commands.InterpretAsCommand, (byte)command], cancellationToken, Context.NoteWritten).ConfigureAwait(false))
        {
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

    private Task OfferEnableAsync(Options telnetOption, CancellationToken cancellationToken)
    {
        if (Settings.DisableAllNegotiation)
        {
            return Task.CompletedTask;
        }

        return SendRequestAsync(Negotiation.OfferEnable((int)telnetOption), telnetOption, cancellationToken);
    }

    private Task OfferDisableAsync(Options telnetOption, CancellationToken cancellationToken)
    {
        if (Settings.DisableAllNegotiation)
        {
            return Task.CompletedTask;
        }

        return SendRequestAsync(Negotiation.OfferDisable((int)telnetOption), telnetOption, cancellationToken);
    }

    private async Task SendRequestAsync(Commands? verb, Options option, CancellationToken cancellationToken)
    {
        if (verb is null)
        {
            return;
        }

        if (Settings.DisableAllNegotiation)
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
    private Task SendNegotiationBytesAsync(Commands verb, Options option, CancellationToken cancellationToken)
    {
        byte[] buffer = [(byte)Commands.InterpretAsCommand, (byte)verb, (byte)option];
        return SendRawBytesLockedAsync(buffer, cancellationToken);
    }

    // Raw protocol-byte choke point (assumes SendRateLimit is held):
    // protocol frames bypass WriteAsync(byte[]) because it IAC-escapes
    // its whole input, which would double the frame's own IAC.
    private async Task SendRawBytesLockedAsync(byte[] buffer, CancellationToken cancellationToken)
    {
        await WriteStream.WriteAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
        Context.NoteWritten(buffer.Length);
    }

    private void WriteLog(string message)
    {
        Settings.Log?.Invoke(message);
        System.Diagnostics.Debug.WriteLine(message);
    }
}
