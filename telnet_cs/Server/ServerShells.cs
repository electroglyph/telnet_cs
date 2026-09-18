namespace telnet_cs.Server;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using telnet_cs.Client;
using telnet_cs.Protocol;

/// <summary>
/// Ready-made server shells: call one with an accepted
/// <see cref="ServerSession"/> (there is no shell registry on
/// <see cref="TelnetServer"/> — it returns bare sessions for the caller
/// to drive). Currently the REPL subset of the reference
/// <c>telnet_server_shell</c>: prompt loop with per-prompt Go-Ahead and
/// introspection commands. The reference toggle/dump/raw-protocol
/// commands have no counterpart (no live option toggling or byte-dump
/// surface exists on sessions).
/// </summary>
public static class ServerShells
{
    /// <summary>
    /// One REPL line-wait slice: short enough to re-check cancellation
    /// and disconnects, long enough to avoid spin noise. Slices repeat
    /// until a line completes (a plain <c>ReadAsync</c> slice rather than
    /// <c>TerminatedReadAsync</c>, whose deadline form would log a
    /// failure line on every quiet slice).
    /// </summary>
    private static readonly TimeSpan ReplReadSlice = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Runs a line REPL on <paramref name="session"/> until
    /// <c>quit</c>, disconnect, or <paramref name="cancellationToken"/>:
    /// banner, <c>tel:sh&gt; </c> prompt, Go-Ahead per prompt (skipped
    /// when <c>NeverSendGa</c>; <c>SendGaAsync</c> still suppresses it
    /// while SGA is in effect), then a <c>\n</c>-terminated line.
    /// The session transport is closed on exit (the reference shell
    /// always closes its writer when the loop ends).
    /// </summary>
    /// <param name="session">The accepted session to drive.</param>
    /// <param name="cancellationToken">Token to stop the loop.</param>
    /// <returns>An awaitable Task.</returns>
    public static async Task RunReplAsync(ServerSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        // The reference greets TLS peers with "Ready (secure: <ver>).":
        // only the TLS-vs-plain distinction survives here (no handshake
        // version string is kept), falling back to the bare "Ready.".
        string banner = session.IsTls ? "Ready (secure: TLS)." : "Ready.";
        await session.WriteAsync($"{banner}{LineFeed.Rfc854}", cancellationToken).ConfigureAwait(false);
        var pending = new System.Text.StringBuilder();
        while (!cancellationToken.IsCancellationRequested && session.IsConnected)
        {
            await session.WriteAsync("tel:sh> ", cancellationToken).ConfigureAwait(false);
            if (!session.Settings.NeverSendGa)
            {
                await session.SendGaAsync(cancellationToken).ConfigureAwait(false);
            }

            string? line = await ReadLineAsync(session, pending, cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            await session.WriteAsync(LineFeed.Rfc854, cancellationToken).ConfigureAwait(false);
            if (line.Length == 0)
            {
                continue;
            }

            bool cont = await HandleCommandAsync(session, line, cancellationToken).ConfigureAwait(false);
            if (!cont)
            {
                break;
            }
        }

        session.Close();
    }

    /// <summary>
    /// Accumulates read slices until a <c>\n</c>-terminated line
    /// completes. Backspace (<c>\b</c>) and delete (<c>\x7f</c>) erase the
    /// previous character with a <c>"\b \b"</c> echo (the reference
    /// <c>_LineEditor</c>). Returns null on disconnect or cancellation (an
    /// empty slice with a live peer simply waits again).
    /// </summary>
    private static async Task<string?> ReadLineAsync(ServerSession session, System.Text.StringBuilder pending, CancellationToken cancellationToken)
    {
        int cap;
        try
        {
            cap = session.Settings.MaxReplLineLength;
        }
        catch
        {
            cap = 4096;
        }

        if (cap > 0 && pending.Length > cap)
        {
            int overLength = pending.Length;
            pending.Clear();
            try
            {
                await session.WriteAsync("\r\nLine too long.\r\n", cancellationToken).ConfigureAwait(false);
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
            try
            {
                session.Settings.Log?.Invoke($"buffer-cap: endpoint={session.RemoteEndPoint ?? "unknown"} repl-pending={overLength} cap={cap}");
            }
            catch
            {
            }

            try
            {
                session.Close();
            }
            catch
            {
            }

            return null;
        }

        var line = new System.Text.StringBuilder();
        int echoBudget = cap > 0 ? cap * 3 : int.MaxValue;
        while (!cancellationToken.IsCancellationRequested && session.IsConnected)
        {
            string chunk;
            try
            {
                chunk = await session.ReadAsync(ReplReadSlice, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return null;
            }

            pending.Append(chunk);
            string accumulated = pending.ToString();
            pending.Clear();
            bool sawNewline = false;
            for (int i = 0; i < accumulated.Length; i++)
            {
                char c = accumulated[i];
                if (c is '\b' or '\x7f')
                {
                    if (line.Length > 0)
                    {
                        line.Length--;
                        if (echoBudget >= 3)
                        {
                            echoBudget -= 3;
                            try
                            {
                                await session.WriteAsync("\b \b", cancellationToken).ConfigureAwait(false);
                            }
                            catch (OperationCanceledException)
                            {
                                return null;
                            }
                        }
                    }

                    continue;
                }

                if (c == '\n')
                {
                    // Stash anything past the newline for the next line.
                    pending.Append(accumulated.Substring(i + 1));
                    sawNewline = true;
                    break;
                }

                line.Append(c);
            }

            if (cap > 0 && line.Length > cap)
            {
                pending.Clear();
                try
                {
                    await session.WriteAsync("\r\nLine too long.\r\n", cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return null;
                }
#pragma warning disable CA1031 // Defensive close path: never throw out of hardening.
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(ex.Message);
                }
#pragma warning restore CA1031
                try
                {
                    session.Settings.Log?.Invoke($"buffer-cap: endpoint={session.RemoteEndPoint ?? "unknown"} repl-line={line.Length} cap={cap}");
                }
                catch
                {
                }

                try
                {
                    session.Close();
                }
                catch
                {
                }

                return null;
            }

            if (sawNewline)
            {
                return line.ToString().TrimEnd('\r');
            }

            if (cap > 0 && pending.Length > cap)
            {
                // No newline in this chunk but pending grew (should be
                // empty here unless future logic stashes): fail closed.
                pending.Clear();
                try
                {
                    session.Close();
                }
                catch
                {
                }

                return null;
            }
        }

        return null;
    }

    private static Task<bool> HandleCommandAsync(ServerSession session, string line, CancellationToken cancellationToken)
    {
        return (line.ToLowerInvariant()) switch
        {
            "quit" => QuitAsync(session, cancellationToken),
            "help" => HelpAsync(session, cancellationToken),
            "version" => VersionAsync(session, cancellationToken),
            "negotiation" => NegotiationAsync(session, cancellationToken),
            "stats" => StatsAsync(session, cancellationToken),
            "environ" => EnvironAsync(session, cancellationToken),
            "slc" => SlcAsync(session, cancellationToken),
            _ => UnknownAsync(session, cancellationToken),
        };
    }

    private static async Task<bool> QuitAsync(ServerSession session, CancellationToken cancellationToken)
    {
        await session.WriteAsync($"Goodbye.{LineFeed.Rfc854}", cancellationToken).ConfigureAwait(false);
        return false;
    }

    private static async Task<bool> HelpAsync(ServerSession session, CancellationToken cancellationToken)
    {
        await session.WriteAsync(
          $"quit/help/version/negotiation/stats/environ/slc{LineFeed.Rfc854}", cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static async Task<bool> VersionAsync(ServerSession session, CancellationToken cancellationToken)
    {
        string version = typeof(ServerShells).Assembly.GetName().Version?.ToString() ?? "?";
        await session.WriteAsync($"telnet_cs {version}{LineFeed.Rfc854}", cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static async Task<bool> NegotiationAsync(ServerSession session, CancellationToken cancellationToken)
    {
        // Curated introspection list (NegotiationState exposes no
        // enumeration): name plus both RFC 1143 side states.
        Options[] options =
        [
          Options.TransmitBinary,
          Options.Echo,
          Options.SuppressGoAhead,
          Options.Status,
          Options.TimingMark,
          Options.TerminalType,
          Options.TerminalSpeed,
          Options.WindowSize,
          Options.RemoteFlowControl,
          Options.LineMode,
          Options.XDisplay,
          Options.OldEnvironment,
          Options.NewEnvironment,
          Options.CharacterSet,
          Options.Mccp2,
          Options.Mccp3,
          Options.Gmcp,
        ];
        var report = new List<string>(options.Length);
        foreach (var option in options)
        {
            var (us, him) = session.Negotiation[(int)option];
            report.Add($"{option}: us={us} him={him}");
        }

        await session.WriteAsync(string.Join(LineFeed.Rfc854, report) + LineFeed.Rfc854, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static async Task<bool> StatsAsync(ServerSession session, CancellationToken cancellationToken)
    {
        var context = session.Context;
        await session.WriteAsync(
          $"rx={context.CharsReceived} tx={context.CharsSent} idle={context.Idle.TotalSeconds:F0}s{LineFeed.Rfc854}",
          cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static async Task<bool> EnvironAsync(ServerSession session, CancellationToken cancellationToken)
    {
        var entries = session.ClientEnvironment;
        string body = entries.Count == 0
          ? "(empty)"
          : string.Join(LineFeed.Rfc854, entries.Select(kv => $"{kv.Key}={kv.Value}"));
        await session.WriteAsync(body + LineFeed.Rfc854, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static async Task<bool> SlcAsync(ServerSession session, CancellationToken cancellationToken)
    {
        // The reference "slc" command writes the special-line-characters
        // table (get_slcdata): one row per function with its level,
        // value and modifier flags.
        var rows = new List<string>(LinemodeProtocol.MaxFunction);
        for (byte function = 1; function <= LinemodeProtocol.MaxFunction; function++)
        {
            var entry = session.GetLinemodeEntry(function);
            rows.Add($"func={function} level={entry.Level} value={entry.Value} flags={entry.Flags}");
        }

        await session.WriteAsync(
          $"Special Line Characters:{LineFeed.Rfc854}{string.Join(LineFeed.Rfc854, rows)}{LineFeed.Rfc854}",
          cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static async Task<bool> UnknownAsync(ServerSession session, CancellationToken cancellationToken)
    {
        await session.WriteAsync($"no such command.{LineFeed.Rfc854}", cancellationToken).ConfigureAwait(false);
        return true;
    }
}
