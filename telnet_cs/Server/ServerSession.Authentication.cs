namespace telnet_cs.Server;

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using telnet_cs.Protocol;
using telnet_cs.Transport;

/// <summary>
/// Server-role credential handshake: username/password prompting, attempt pacing and exhaustion disconnect. Split from <see cref="ServerSession"/>; wire behavior is unchanged.
/// </summary>
public partial class ServerSession
{
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
        authPumpStanddown = true;
        try
        {
            for (int attempt = 0; attempt < Settings.MaxLoginAttempts; attempt++)
            {
                await WriteAsync(Settings.LoginUserPrompt, cancellationToken).ConfigureAwait(false);
                string? user = await ReadCredentialLineAsync(timeout, cancellationToken).ConfigureAwait(false);
                if (user is null)
                {
                    WriteLog($"auth-exhausted: endpoint={CapEndpoint()} attempt={attempt + 1} reason=timeout");
                    await DelayBetweenAttemptsAsync(cancellationToken).ConfigureAwait(false);
                    if (ShouldDisconnectOnExhaustion())
                    {
                        await SendAuthExhaustedNoticeAsync(cancellationToken).ConfigureAwait(false);
                        try
                        {
                            Close();
                        }
                        catch
                        {
                        }
                    }

                    return false;
                }

                await WriteAsync(Settings.LoginPasswordPrompt, cancellationToken).ConfigureAwait(false);
                string? password = await ReadCredentialLineAsync(timeout, cancellationToken).ConfigureAwait(false);

                if (password is null)
                {
                    WriteLog($"auth-exhausted: endpoint={CapEndpoint()} attempt={attempt + 1} reason=timeout");
                    await DelayBetweenAttemptsAsync(cancellationToken).ConfigureAwait(false);
                    if (ShouldDisconnectOnExhaustion())
                    {
                        await SendAuthExhaustedNoticeAsync(cancellationToken).ConfigureAwait(false);
                        try
                        {
                            Close();
                        }
                        catch
                        {
                        }
                    }

                    return false;
                }

                bool ok = await validate(user, password).ConfigureAwait(false);
                if (ok)
                {
                    MarkHandshakeComplete();
                    return true;
                }

                WriteLog($"auth-exhausted: endpoint={CapEndpoint()} attempt={attempt + 1} reason=bad-credentials");
                await DelayBetweenAttemptsAsync(cancellationToken).ConfigureAwait(false);
            }

            WriteLog($"auth-exhausted: endpoint={CapEndpoint()} attempt={Settings.MaxLoginAttempts} reason=exhausted");
            await DelayBetweenAttemptsAsync(cancellationToken).ConfigureAwait(false);
            if (ShouldDisconnectOnExhaustion())
            {
                await SendAuthExhaustedNoticeAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    Close();
                }
                catch
                {
                }
            }

            return false;
        }
        finally
        {
            authPumpStanddown = false;
        }
    }

    private bool ShouldDisconnectOnExhaustion()
    {
        try
        {
            return Settings.DisconnectOnExhaustion;
        }
        catch
        {
            return false;
        }
    }

    private async Task DelayBetweenAttemptsAsync(CancellationToken callerToken)
    {
        TimeSpan delay;
        try
        {
            delay = Settings.LoginAttemptDelay;
        }
        catch
        {
            delay = TimeSpan.FromSeconds(1);
        }

        if (delay <= TimeSpan.Zero)
        {
            return;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(callerToken, InternalCancellation.Token);
        await Task.Delay(delay, linked.Token).ConfigureAwait(false);
    }

    private async Task SendAuthExhaustedNoticeAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, cancellationToken, InternalCancellation.Token);
            await WriteAsync("\r\nLogin failed.\r\n", linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
#pragma warning disable CA1031 // Defensive close path: never throw out of hardening.
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex.Message);
        }
#pragma warning restore CA1031
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

    // Stands the background pump down for the whole credential exchange
    // (see AuthenticateAsync): explicit reads drive the wire, so password
    // bytes are never double-consumed.
    private volatile bool authPumpStanddown;

    // First-data TLS sniff state (see ReadAsync): once a raw byte has
    // been seen, a 0x16 lead on a plaintext listener closes the session.
    private bool tlsHelloChecked;

    /// <summary>
    /// Gets the remote endpoint label for diagnostics (set by
    /// <see cref="TelnetServer.AcceptSessionAsync"/>).
    /// </summary>
    internal string? RemoteEndPoint { get; set; }
}
