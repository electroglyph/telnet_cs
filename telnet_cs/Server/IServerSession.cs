namespace telnet_cs.Server
{
  using System;
  using System.Collections.Generic;
  using System.Text.RegularExpressions;
  using System.Threading;
  using System.Threading.Tasks;
  using telnet_cs.Protocol;

  /// <summary>
  /// Server session behaviour: the accept-side counterpart to
  /// <see cref="Client.IClient"/>. Shared I/O and negotiation members mirror
  /// <c>IClient</c>; the rest is server-role only (opening preset,
  /// authentication, subnegotiation requesters, LINEMODE server ops).
  /// <c>RefreshWindowSizeAsync</c> stays client-only and
  /// <c>AuthenticateAsync</c> stays server-only by design.
  /// </summary>
  public interface IServerSession : Client.IBaseClient
  {
    /// <summary>
    /// Gets the per-instance settings (live reference).
    /// </summary>
    TelnetServerOptions Settings { get; }

    /// <summary>
    /// Gets the persistent RFC 1143 negotiation state for this connection.
    /// </summary>
    NegotiationState Negotiation { get; }

    /// <summary>
    /// Reads asynchronously from the session.
    /// </summary>
    /// <returns>Any text read from the session.</returns>
    Task<string> ReadAsync();

    /// <summary>
    /// Reads asynchronously from the session.
    /// </summary>
    /// <param name="timeout">The timeout.</param>
    /// <returns>Any text read from the session.</returns>
    Task<string> ReadAsync(TimeSpan timeout);

    /// <summary>
    /// Reads asynchronously from the session.
    /// </summary>
    /// <param name="timeout">The timeout.</param>
    /// <param name="cancellationToken">Token to cancel the read. Cancellation returns the partial text read so far.</param>
    /// <returns>Any text read from the session.</returns>
    Task<string> ReadAsync(TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>
    /// Writes the specified <paramref name="command"/> to the peer.
    /// </summary>
    /// <param name="command">The command.</param>
    /// <param name="cancellationToken">A token to cancel the write.</param>
    /// <returns>An awaitable Task.</returns>
    Task WriteAsync(string command, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes the specified <paramref name="data"/> to the peer.
    /// </summary>
    /// <param name="data">The byte array to send.</param>
    /// <param name="cancellationToken">A token to cancel the write.</param>
    /// <returns>An awaitable Task.</returns>
    Task WriteAsync(byte[] data, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes the specified <paramref name="command"/> plus a legacy
    /// <c>"\n"</c> line feed to the peer.
    /// </summary>
    /// <param name="command">The command.</param>
    /// <param name="cancellationToken">A token to cancel the write.</param>
    /// <returns>An awaitable Task.</returns>
    Task WriteLineAsync(string command, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes the specified <paramref name="command"/> plus
    /// <paramref name="lineFeed"/> to the peer.
    /// </summary>
    /// <param name="command">The command.</param>
    /// <param name="lineFeed">The type of lineFeed to use.</param>
    /// <returns>An awaitable Task.</returns>
    Task WriteLineAsync(string command, string lineFeed);

    /// <summary>
    /// Writes the specified <paramref name="command"/> plus
    /// <paramref name="lineFeed"/> to the peer.
    /// </summary>
    /// <param name="command">The command.</param>
    /// <param name="lineFeed">The type of lineFeed to use.</param>
    /// <param name="cancellationToken">A token to cancel the write.</param>
    /// <returns>An awaitable Task.</returns>
    Task WriteLineAsync(string command, string lineFeed, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes the specified <paramref name="command"/> plus a
    /// <see cref="telnet_cs.Client.LineEnding"/> line feed to the peer.
    /// </summary>
    /// <param name="command">The command.</param>
    /// <param name="lineEnding">The line ending to use (<c>Lf</c> preserves the legacy default).</param>
    /// <param name="cancellationToken">A token to cancel the write.</param>
    /// <returns>An awaitable Task.</returns>
    Task WriteLineAsync(string command, telnet_cs.Client.LineEnding lineEnding, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes the specified <paramref name="command"/> plus an RFC 854
    /// compliant <c>"\r\n"</c> line feed to the peer.
    /// </summary>
    /// <param name="command">The command.</param>
    /// <param name="cancellationToken">A token to cancel the write.</param>
    /// <returns>An awaitable Task.</returns>
    Task WriteLineRfc854Async(string command, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads asynchronously from the session, terminating as soon as the <paramref name="terminator"/> is located.
    /// </summary>
    /// <param name="terminator">The terminator.</param>
    /// <returns>Any text read from the session.</returns>
    Task<string> TerminatedReadAsync(string terminator);

    /// <summary>
    /// Reads asynchronously from the session, terminating as soon as the <paramref name="terminator"/> is located.
    /// </summary>
    /// <param name="terminator">The terminator.</param>
    /// <param name="timeout">The timeout.</param>
    /// <returns>Any text read from the session.</returns>
    Task<string> TerminatedReadAsync(string terminator, TimeSpan timeout);

    /// <summary>
    /// Reads asynchronously from the session, terminating as soon as the <paramref name="regex"/> is located.
    /// </summary>
    /// <param name="regex">The regex to match.</param>
    /// <param name="timeout">The timeout.</param>
    /// <returns>Any text read from the session.</returns>
    Task<string> TerminatedReadAsync(Regex regex, TimeSpan timeout);

    /// <summary>
    /// Reads asynchronously from the session, terminating as soon as any of the <paramref name="terminators"/> is located.
    /// </summary>
    /// <param name="terminators">The terminators to search for.</param>
    /// <returns>Any text read from the session.</returns>
    Task<string> TerminatedReadAsync(IEnumerable<string> terminators);

    /// <summary>
    /// Reads asynchronously from the session, terminating as soon as any of the <paramref name="terminators"/> is located.
    /// </summary>
    /// <param name="terminators">The terminators to search for.</param>
    /// <param name="timeout">The timeout.</param>
    /// <returns>Any text read from the session.</returns>
    Task<string> TerminatedReadAsync(IEnumerable<string> terminators, TimeSpan timeout);

    /// <summary>
    /// Reads asynchronously from the session, terminating as soon as any of the <paramref name="regexes"/> is matched.
    /// </summary>
    /// <param name="regexes">The regexes to match.</param>
    /// <returns>Any text read from the session.</returns>
    Task<string> TerminatedReadAsync(IEnumerable<Regex> regexes);

    /// <summary>
    /// Reads asynchronously from the session, terminating as soon as any of the <paramref name="regexes"/> is matched.
    /// </summary>
    /// <param name="regexes">The regexes to match.</param>
    /// <param name="timeout">The timeout.</param>
    /// <returns>Any text read from the session.</returns>
    Task<string> TerminatedReadAsync(IEnumerable<Regex> regexes, TimeSpan timeout);

    /// <summary>
    /// Reads asynchronously from the session, terminating as soon as the <paramref name="terminator"/> is located.
    /// </summary>
    /// <param name="terminator">The terminator.</param>
    /// <param name="timeout">The maximum time to wait.</param>
    /// <param name="millisecondSpin">The millisecond spin between each read from the session.</param>
    /// <returns>Any text read from the session.</returns>
    Task<string> TerminatedReadAsync(string terminator, TimeSpan timeout, int millisecondSpin);

    /// <summary>
    /// Reads asynchronously from the session, terminating as soon as the <paramref name="terminator"/> is located.
    /// </summary>
    /// <param name="terminator">The terminator.</param>
    /// <param name="timeout">The maximum time to wait.</param>
    /// <param name="millisecondSpin">The millisecond spin between each read from the session.</param>
    /// <param name="cancellationToken">Token to cancel the read.</param>
    /// <returns>Any text read from the session.</returns>
    Task<string> TerminatedReadAsync(string terminator, TimeSpan timeout, int millisecondSpin, CancellationToken cancellationToken);

    /// <summary>
    /// Reads asynchronously from the session, terminating as soon as the <paramref name="regex"/> is matched.
    /// </summary>
    /// <param name="regex">The regex to match.</param>
    /// <param name="timeout">The maximum time to wait.</param>
    /// <param name="millisecondSpin">The millisecond spin between each read from the session.</param>
    /// <returns>Any text read from the session.</returns>
    Task<string> TerminatedReadAsync(Regex regex, TimeSpan timeout, int millisecondSpin);

    /// <summary>
    /// Reads asynchronously from the session, terminating as soon as the <paramref name="regex"/> is matched.
    /// </summary>
    /// <param name="regex">The regex to match.</param>
    /// <param name="timeout">The maximum time to wait.</param>
    /// <param name="millisecondSpin">The millisecond spin between each read from the session.</param>
    /// <param name="cancellationToken">Token to cancel the read.</param>
    /// <returns>Any text read from the session.</returns>
    Task<string> TerminatedReadAsync(Regex regex, TimeSpan timeout, int millisecondSpin, CancellationToken cancellationToken);

    /// <summary>
    /// Reads asynchronously from the session, terminating as soon as any of the <paramref name="terminators"/> is located.
    /// </summary>
    /// <param name="terminators">The terminators to search for.</param>
    /// <param name="timeout">The maximum time to wait.</param>
    /// <param name="millisecondSpin">The millisecond spin between each read from the session.</param>
    /// <param name="cancellationToken">Token to cancel the read.</param>
    /// <returns>Any text read from the session.</returns>
    Task<string> TerminatedReadAsync(IEnumerable<string> terminators, TimeSpan timeout, int millisecondSpin, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads asynchronously from the session, terminating as soon as any of the <paramref name="regexes"/> is matched.
    /// </summary>
    /// <param name="regexes">The regexes to match.</param>
    /// <param name="timeout">The maximum time to wait.</param>
    /// <param name="millisecondSpin">The millisecond spin between each read from the session.</param>
    /// <param name="cancellationToken">Token to cancel the read.</param>
    /// <returns>Any text read from the session.</returns>
    Task<string> TerminatedReadAsync(IEnumerable<Regex> regexes, TimeSpan timeout, int millisecondSpin, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a standalone TELNET control command as an <c>IAC &lt;cmd&gt;</c> frame.
    /// Option-negotiation verbs go through the RFC 1143 negotiation API instead.
    /// </summary>
    /// <param name="command">The control command to send.</param>
    /// <param name="cancellationToken">A token to cancel the send.</param>
    /// <returns>An awaitable Task.</returns>
    Task SendCommand(Commands command, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a TELNET Synch signal (TCP Urgent with DM). Requires a
    /// <c>Transport.TcpByteStream</c>; otherwise throws
    /// <see cref="NotSupportedException"/>.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the send.</param>
    /// <returns>An awaitable Task.</returns>
    Task SendSynchAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks the peer to enable <paramref name="telnetOption"/> (sends
    /// <c>IAC DO</c>), unless already enabled, negotiating, or refused
    /// without new stimulus.
    /// </summary>
    /// <param name="telnetOption">The option to request.</param>
    /// <param name="cancellationToken">A token to cancel the send.</param>
    /// <returns>An awaitable Task.</returns>
    Task RequestEnableAsync(Options telnetOption, CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks the peer to disable <paramref name="telnetOption"/> (sends
    /// <c>IAC DONT</c>), unless already disabled or already negotiating.
    /// </summary>
    /// <param name="telnetOption">The option to refuse.</param>
    /// <param name="cancellationToken">A token to cancel the send.</param>
    /// <returns>An awaitable Task.</returns>
    Task RequestDisableAsync(Options telnetOption, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends the server opening preset: WILL for each offered option and DO
    /// for each requested one (see <see cref="TelnetServerOptions"/>).
    /// A fully toggled-off preset sends nothing.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the send.</param>
    /// <returns>An awaitable Task.</returns>
    Task SendOpeningPresetAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Authenticates the peer with login/password prompts and
    /// <paramref name="validate"/>, retrying up to the configured attempt
    /// budget. A credential line that never terminates fails closed.
    /// </summary>
    /// <param name="validate">Validates a (user, password) pair. Exceptions propagate immediately.</param>
    /// <param name="timeout">The maximum time to wait for each credential line.</param>
    /// <param name="cancellationToken">Token to cancel the read.</param>
    /// <returns><c>true</c> when a pair validates within the attempt budget.</returns>
    Task<bool> AuthenticateAsync(Func<string, string, Task<bool>> validate, TimeSpan timeout, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the last terminal size reported by the peer (RFC 1073), or null
    /// when the peer never sent NAWS.
    /// </summary>
    (ushort Width, ushort Height)? ClientWindowSize { get; }

    /// <summary>
    /// Asks the peer for its terminal-type list (RFC 1091). The returned
    /// list ends at the first repeat; returns whatever arrived on timeout.
    /// </summary>
    /// <param name="timeout">The maximum time to wait for the full chain.</param>
    /// <param name="cancellationToken">A token to cancel the wait.</param>
    Task<IReadOnlyList<string>> RequestTerminalTypesAsync(TimeSpan timeout, CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks the peer for its terminal speed (RFC 1079), normalized.
    /// </summary>
    /// <param name="timeout">The maximum time to wait.</param>
    /// <param name="cancellationToken">A token to cancel the wait.</param>
    Task<string?> RequestTerminalSpeedAsync(TimeSpan timeout, CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks the peer for environment variables (RFC 1408).
    /// </summary>
    /// <param name="timeout">The maximum time to wait.</param>
    /// <param name="types">The variable types to request; null requests the defaults.</param>
    /// <param name="cancellationToken">A token to cancel the wait.</param>
    Task<IReadOnlyDictionary<string, string>> RequestEnvironmentAsync(TimeSpan timeout, byte[]? types = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a LINEMODE MODE mask (RFC 1184 §2.2) from the server role.
    /// </summary>
    /// <param name="mode">The MODE mask to send (without MODE_ACK).</param>
    /// <param name="cancellationToken">A token to cancel the send.</param>
    Task SendModeAsync(byte mode, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a LINEMODE FORWARDMASK (RFC 1184 §2.3) from the server role.
    /// </summary>
    /// <param name="mask">The forward mask bytes (at most 32).</param>
    /// <param name="cancellationToken">A token to cancel the send.</param>
    Task SendForwardMaskAsync(byte[] mask, CancellationToken cancellationToken = default);

    /// <summary>
    /// Exports the configured LINEMODE special characters to the peer
    /// (RFC 1184 §5.5).
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the send.</param>
    Task PublishSpecialCharactersAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Requests the peer's LINEMODE special-character table (RFC 1184 §2.4).
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the send.</param>
    Task RequestRemoteSpecialCharactersAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Receives a single byte with TCP urgent (out-of-band) semantics
    /// (RFC 854 Synch support). Requires a TCP-backed stream.
    /// </summary>
    /// <param name="cancellationToken">The token to monitor for cancellation requests.</param>
    /// <returns>The urgent byte received.</returns>
    Task<byte> ReceiveUrgentAsync(CancellationToken cancellationToken = default);
  }
}
