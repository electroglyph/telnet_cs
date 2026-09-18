namespace telnet_cs.Client
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading.Tasks;
    using telnet_cs.Protocol;

    public partial class Client
    {
        /// <summary>
        /// Sends an ENVIRON INFO update when the configured environment values
        /// changed since the last read and the peer agreed to receive them
        /// (we are the WILL-sender, RFC 1408 §4.4 / RFC 1572 §4). Always re-baselines, so a
        /// change is reported once. The update rides the negotiated option:
        /// NEW_ENVIRON when agreed (what reference peers DO), else OLD_ENVIRON.
        /// </summary>
        private async Task MaybeSendEnvironmentInfoAsync()
        {
            var snapshot = SnapshotEnvironment();
            var changed = _environmentSnapshot is not null && _environmentSnapshot != snapshot;
            _environmentSnapshot = snapshot;
            // Prefer NEW_ENVIRON (RFC 1572): reference servers only DO NEW, so
            // an OLD-only gate drops updates on NEW-only sessions.
            Options? option = (Negotiation.IsEnabledByUs((int)Options.NewEnvironment), Negotiation.IsEnabledByUs((int)Options.OldEnvironment)) switch
            {
                (true, _) => Options.NewEnvironment,
                (_, true) => Options.OldEnvironment,
                _ => null,
            };
            if (option is null)
            {
                return;
            }

            if (!changed)
            {
                return;
            }

            var (term, lang, columns, lines) = SystemEnvironment();
            var colorTerm = System.Environment.GetEnvironmentVariable("COLORTERM") ?? string.Empty;
            var info = EnvironmentProtocol.BuildResponse(
              EnvironmentProtocol.Info,
              [],
              Settings.EnvironmentUser,
              Settings.EnvironmentDisplay,
              Settings.EnvironmentUserVars,
              term,
              lang,
              columns,
              lines,
              colorTerm);
            var frame = EnvironmentProtocol.FrameSubnegotiation((int)option.Value, info);
            if (WriteStream.Connected && !InternalCancellation.Token.IsCancellationRequested)
            {
                await SendRateLimit.WaitAsync(InternalCancellation.Token).ConfigureAwait(false);
                try
                {
                    await WriteStream.WriteAsync(frame, 0, frame.Length, InternalCancellation.Token).ConfigureAwait(false);
                }
                finally
                {
                    SendRateLimit.Release();
                }
            }
        }

        private string SnapshotEnvironment()
        {
            var (term, lang, columns, lines) = SystemEnvironment();
            var colorTerm = System.Environment.GetEnvironmentVariable("COLORTERM") ?? string.Empty;
            var parts = new List<string>
      {
        Settings.EnvironmentUser ?? string.Empty,
        Settings.EnvironmentDisplay ?? string.Empty,
        term ?? string.Empty,
        lang ?? string.Empty,
        columns ?? string.Empty,
        lines ?? string.Empty,
        colorTerm,
      };
            foreach (var pair in Settings.EnvironmentUserVars.OrderBy(static p => p.Key, StringComparer.Ordinal))
            {
                parts.Add(pair.Key);
                parts.Add(pair.Value);
            }

            return string.Join("\0", parts);
        }

        /// <summary>
        /// Derives the volunteered session parameters (<c>TERM</c>,
        /// <c>LANG</c>, <c>COLUMNS</c>, <c>LINES</c>) from the client
        /// settings, mirroring the <see cref="telnet_cs.IO.ByteStreamHandler"/>
        /// reply path: effective terminal type, <c>C</c> without an explicit
        /// encoding (else <c>en_US.&lt;encoding&gt;</c> with hyphens stripped,
        /// the single spelling both paths share), and the effective
        /// window size.
        /// </summary>
        private (string? Term, string? Lang, string? Columns, string? Lines) SystemEnvironment()
        {
            var term = Settings.TerminalType ?? TerminalType;
            var lang = Settings.TextEncoding is null ? "C" : "en_US." + Settings.TextEncoding.WebName.Replace("-", string.Empty, StringComparison.Ordinal);
            var (width, height) = NawsProtocol.GetEffectiveSize(Settings.WindowWidth, Settings.WindowHeight);
            return (
              string.IsNullOrEmpty(term) ? null : term,
              lang,
              width.ToString(System.Globalization.CultureInfo.InvariantCulture),
              height.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        private string? _environmentSnapshot;
    }
}
