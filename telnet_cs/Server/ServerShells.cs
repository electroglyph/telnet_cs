namespace telnet_cs.Server
{
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
        /// </summary>
        /// <param name="session">The accepted session to drive.</param>
        /// <param name="cancellationToken">Token to stop the loop.</param>
        /// <returns>An awaitable Task.</returns>
        public static async Task RunReplAsync(ServerSession session, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(session);
            await session.WriteAsync($"Ready.{LineFeed.Rfc854}", cancellationToken).ConfigureAwait(false);
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
        }

        /// <summary>
        /// Accumulates read slices until a <c>\n</c>-terminated line
        /// completes. Returns null on disconnect or cancellation (an empty
        /// slice with a live peer simply waits again).
        /// </summary>
        private static async Task<string?> ReadLineAsync(ServerSession session, System.Text.StringBuilder pending, CancellationToken cancellationToken)
        {
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
                int newline = accumulated.IndexOf('\n');
                if (newline >= 0)
                {
                    pending.Remove(0, newline + 1);
                    return accumulated.Substring(0, newline).TrimEnd('\r');
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
              $"quit/help/version/negotiation/stats/environ{LineFeed.Rfc854}", cancellationToken).ConfigureAwait(false);
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
                var (us, him) = session.Negotiation.GetStates((int)option);
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

        private static async Task<bool> UnknownAsync(ServerSession session, CancellationToken cancellationToken)
        {
            await session.WriteAsync($"no such command.{LineFeed.Rfc854}", cancellationToken).ConfigureAwait(false);
            return true;
        }
    }
}
