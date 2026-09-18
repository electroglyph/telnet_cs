namespace telnet_cs.Server;

using System;
using System.Threading;
using System.Threading.Tasks;
using telnet_cs.IO;
using telnet_cs.Protocol;

/// <summary>
/// Game-driven ECHO for <see cref="ServerSession"/>: lets the game toggle
/// client-side echo on demand (password prompts), through the RFC 1143
/// machine, callable from any thread.
/// </summary>
public partial class ServerSession
{
    /// <summary>
    /// Toggles client-side echo: <c>suppress: true</c> sends <c>IAC WILL
    /// ECHO</c> (the client stops its local echo; the server takes over, as
    /// for a masked password prompt), <c>false</c> sends <c>IAC WONT
    /// ECHO</c> (the client resumes its local echo). Idempotent: repeating
    /// the active direction sends nothing, and a toggle queued behind an
    /// outstanding opposite request drains on the peer's reply (RFC 1143).
    /// The first manual call permanently stands down the deferred auto-ECHO
    /// offer for the session, so later TTYPE answers cannot auto-<c>WILL</c>
    /// past a game-driven <c>WONT</c> and unmask. Explicit caller intent
    /// wins over MUD-client fingerprinting (no <c>IsMudClient</c> gate).
    /// Under <see cref="TelnetServerOptions.DisableAllNegotiation"/> this is
    /// a complete no-op (no bytes, no state change).
    /// </summary>
    /// <param name="suppress"><c>true</c> to take over echo (<c>WILL</c>),
    /// <c>false</c> to hand it back (<c>WONT</c>).</param>
    /// <param name="cancellationToken">A token to cancel the send.</param>
    /// <returns>An awaitable Task.</returns>
    public Task SetEchoAsync(bool suppress, CancellationToken cancellationToken = default)
    {
        lock (collectorLock)
        {
            manualEcho = suppress;
        }

        return suppress
            ? OfferEnableAsync(Options.Echo, cancellationToken)
            : OfferDisableAsync(Options.Echo, cancellationToken);
    }

    /// <summary>
    /// Writes <paramref name="prompt"/> with the echo toggle fused ahead of
    /// it in a single atomic write: one raw <c>WriteStream</c> frame of
    /// <c>[IAC WILL|WONT ECHO] + prompt</c>, so no broadcast from another
    /// thread can land between the toggle and the prompt text (use for
    /// masked password prompts: <c>WriteWithEchoAsync("Password: ",
    /// suppress: true)</c>). When the toggle is idempotent or queued behind
    /// an outstanding opposite request, the prompt alone goes out — still a
    /// single write. <paramref name="prompt"/> may be empty (a bare toggle);
    /// <c>null</c> throws <see cref="ArgumentNullException"/>. Like
    /// <see cref="SetEchoAsync"/>, the first call permanently stands down
    /// the deferred auto-ECHO offer and bypasses the MUD-client gate.
    /// Under <see cref="TelnetServerOptions.DisableAllNegotiation"/> the
    /// toggle self-suppresses and the prompt is still written as plain
    /// application data in one atomic write, with no state change.
    /// </summary>
    /// <param name="prompt">The prompt text to write after the toggle.</param>
    /// <param name="suppress"><c>true</c> to take over echo (<c>WILL</c>),
    /// <c>false</c> to hand it back (<c>WONT</c>).</param>
    /// <param name="cancellationToken">A token to cancel the write.</param>
    /// <returns>An awaitable Task.</returns>
    public async Task WriteWithEchoAsync(string prompt, bool suppress, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        // Pre-encode with the session encoding (already IAC-escaped) before
        // touching any negotiation state, so a failure sends nothing.
        byte[] encoded = ByteStringConverter.ConvertStringToByteArray(prompt, Settings.TextEncoding);
        lock (collectorLock)
        {
            manualEcho = suppress;
        }

        // Pre-mutation DisableAllNegotiation guard (mirrors OfferEnableAsync /
        // OfferDisableAsync): the toggle self-suppresses with no Q-state
        // change, and the prompt still goes out as plain application data.
        Commands? verb = Settings.DisableAllNegotiation
            ? null
            : suppress
                ? Negotiation.OfferEnable((int)Options.Echo)
                : Negotiation.OfferDisable((int)Options.Echo);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, InternalCancellation.Token);
        if (WriteStream.Connected && !linked.Token.IsCancellationRequested)
        {
            // Fuse toggle + prompt into one frame before touching the
            // throttle, so the single choke-point write stays atomic: no
            // broadcast from another thread can land between the toggle
            // and the prompt text. Protocol bytes bypass
            // WriteAsync(byte[]) (it would IAC-escape the frame's own
            // IAC), so the raw frame goes out through the shared gate.
            byte[] frame = verb is null
                ? encoded
                : [(byte)Commands.InterpretAsCommand, (byte)verb.Value, (byte)Options.Echo, .. encoded];
            if (frame.Length == 0)
            {
                return;
            }

            await SendFrameLockedAsync(frame, linked.Token, Context.NoteWritten).ConfigureAwait(false);
        }
    }
}
