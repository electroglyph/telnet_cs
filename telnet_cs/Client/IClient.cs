namespace telnet_cs.Client
{
  using System;
  using System.Collections.Generic;
  using System.Text.RegularExpressions;
  using System.Threading;
  using System.Threading.Tasks;
  using telnet_cs.Protocol;
  using telnet_cs.Transport;

  /// <summary>
  /// Telnet Client behaviour.
  /// </summary>
  public interface IClient : IBaseClient
  {
    /// <summary>
    /// Gets the persistent RFC 1143 negotiation state for this connection.
    /// </summary>
    NegotiationState Negotiation { get; }

    /// <summary>
    /// Reads asynchronously from the stream.
    /// </summary>
    /// <returns>Any text read from the stream.</returns>
    Task<string> ReadAsync();

    /// <summary>
    /// Reads asynchronously from the stream.
    /// </summary>
    /// <param name="timeout">The timeout.</param>
    /// <param name="cancellationToken">Token to cancel the read. Cancellation returns the partial text read so far.</param>
    /// <returns>Any text read from the stream.</returns>
    Task<string> ReadAsync(TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>
    /// Reads asynchronously from the stream, terminating as soon as the <paramref name="regex"/> is located.
    /// </summary>
    /// <param name="regex">The regex to match.</param>
    /// <param name="timeout">The timeout.</param>
    /// <returns>Any text read from the stream.</returns>
    Task<string> TerminatedReadAsync(Regex regex, TimeSpan timeout);

    /// <summary>
    /// Reads asynchronously from the stream, terminating as soon as the <paramref name="regex"/> is matched.
    /// </summary>
    /// <param name="regex">The regex to match.</param>
    /// <param name="timeout">The maximum time to wait.</param>
    /// <param name="millisecondSpin">The millisecond spin between each read from the stream.</param>
    /// <param name="cancellationToken">Token to cancel the read.</param>
    /// <returns>Any text read from the stream.</returns>
    Task<string> TerminatedReadAsync(Regex regex, TimeSpan timeout, int millisecondSpin, CancellationToken cancellationToken);

    /// <summary>
    /// Reads asynchronously from the stream, terminating as soon as any of the <paramref name="terminators"/> is located.
    /// </summary>
    /// <param name="terminators">The terminators to search for.</param>
    /// <returns>Any text read from the stream.</returns>
    Task<string> TerminatedReadAsync(IEnumerable<string> terminators);

    /// <summary>
    /// Reads asynchronously from the stream, terminating as soon as any of the <paramref name="terminators"/> is located.
    /// </summary>
    /// <param name="terminators">The terminators to search for.</param>
    /// <param name="timeout">The timeout.</param>
    /// <returns>Any text read from the stream.</returns>
    Task<string> TerminatedReadAsync(IEnumerable<string> terminators, TimeSpan timeout);

    /// <summary>
    /// Reads asynchronously from the stream, terminating as soon as any of the <paramref name="terminators"/> is located.
    /// </summary>
    /// <param name="terminators">The terminators to search for.</param>
    /// <param name="timeout">The maximum time to wait.</param>
    /// <param name="millisecondSpin">The millisecond spin between each read from the stream.</param>
    /// <param name="cancellationToken">Token to cancel the read.</param>
    /// <returns>Any text read from the stream.</returns>
    Task<string> TerminatedReadAsync(IEnumerable<string> terminators, TimeSpan timeout, int millisecondSpin, CancellationToken cancellationToken);

    /// <summary>
    /// Reads asynchronously from the stream, terminating as soon as any of the <paramref name="regexes"/> is matched.
    /// </summary>
    /// <param name="regexes">The regexes to match.</param>
    /// <returns>Any text read from the stream.</returns>
    Task<string> TerminatedReadAsync(IEnumerable<Regex> regexes);

    /// <summary>
    /// Reads asynchronously from the stream, terminating as soon as any of the <paramref name="regexes"/> is matched.
    /// </summary>
    /// <param name="regexes">The regexes to match.</param>
    /// <param name="timeout">The timeout.</param>
    /// <returns>Any text read from the stream.</returns>
    Task<string> TerminatedReadAsync(IEnumerable<Regex> regexes, TimeSpan timeout);

    /// <summary>
    /// Reads asynchronously from the stream, terminating as soon as any of the <paramref name="regexes"/> is matched.
    /// </summary>
    /// <param name="regexes">The regexes to match.</param>
    /// <param name="timeout">The maximum time to wait.</param>
    /// <param name="millisecondSpin">The millisecond spin between each read from the stream.</param>
    /// <param name="cancellationToken">Token to cancel the read.</param>
    /// <returns>Any text read from the stream.</returns>
    Task<string> TerminatedReadAsync(IEnumerable<Regex> regexes, TimeSpan timeout, int millisecondSpin, CancellationToken cancellationToken);

    /// <summary>
    /// Reads asynchronously from the stream, terminating as soon as the <paramref name="terminator"/> is located.
    /// </summary>
    /// <param name="terminator">The terminator.</param>
    /// <returns>Any text read from the stream.</returns>
    Task<string> TerminatedReadAsync(string terminator);

    /// <summary>
    /// Reads asynchronously from the stream, terminating as soon as the <paramref name="terminator"/> is located.
    /// </summary>
    /// <param name="terminator">The terminator.</param>
    /// <param name="timeout">The timeout.</param>
    /// <returns>Any text read from the stream.</returns>
    Task<string> TerminatedReadAsync(string terminator, TimeSpan timeout);

    /// <summary>
    /// Reads asynchronously from the stream, terminating as soon as the <paramref name="terminator"/> is located.
    /// </summary>
    /// <param name="terminator">The terminator.</param>
    /// <param name="timeout">The maximum time to wait.</param>
    /// <param name="millisecondSpin">The millisecond spin between each read from the stream.</param>
    /// <param name="cancellationToken">Token to cancel the read.</param>
    /// <returns>Any text read from the stream.</returns>
    Task<string> TerminatedReadAsync(string terminator, TimeSpan timeout, int millisecondSpin, CancellationToken cancellationToken);

    /// <summary>
    /// Syntactic sugar; tries to login asynchronously, passing in a default LineTerminator of ">".
    /// Anticipates a terminator (TerminatedRead); responds with username (WriteLine).
    /// Anticipates another terminator (TerminatedRead); responds with password (WriteLine).
    /// This is just a proxy for common Telnet behavour, but of course it relies on the Server implementing the expected behaviour.
    /// If the server you're connecting to does anything different, just use custom TerminatedReads followed by WriteLines.
    /// </summary>
    /// <param name="userName">The user name.</param>
    /// <param name="password">The password.</param>
    /// <param name="loginTimeoutMs">The login timeout ms.</param>
    /// <param name="lineFeed">The line feed to use. Issue 38: According to RFC 854, CR+LF should be the default a client sends. For backward compatibility \n maintained.</param>
    /// <returns>True if successful.</returns>
    Task<bool> TryLoginAsync(string userName, string password, int loginTimeoutMs, string lineFeed = Client.LegacyLineFeed);

    /// <summary>
    /// Syntactic sugar; tries to login asynchronously. 
    /// Anticipates a terminator (TerminatedRead); responds with username (WriteLine).
    /// Anticipates another terminator (TerminatedRead); responds with password (WriteLine).
    /// This is just a proxy for common Telnet behavour, but of course it relies on the Server implementing the expected behaviour.
    /// If the server you're connecting to does anything different, just use custom TerminatedReads followed by WriteLines.
    /// </summary>
    /// <param name="userName">The user name.</param>
    /// <param name="password">The password.</param>
    /// <param name="loginTimeoutMs">The login timeout ms.</param>
    /// <param name="terminator">The prompt terminator to anticipate.</param>
    /// <param name="lineFeed">The line feed to use. Issue 38: According to RFC 854, CR+LF should be the default a client sends. For backward compatibility \n maintained.</param>
    /// <returns>True if successful.</returns>
    Task<bool> TryLoginAsync(string userName, string password, int loginTimeoutMs, string terminator, string lineFeed = Client.LegacyLineFeed);

    /// <summary>
    /// Writes the specified <paramref name="data"/> to the server.
    /// </summary>
    /// <param name="data">The byte array to send.</param>
    /// <param name="cancellationToken">A token to cancel the write.</param>
    /// <returns>An awaitable Task.</returns>
    Task WriteAsync(byte[] data, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes the specified <paramref name="command"/> to the server.
    /// </summary>
    /// <param name="command">The command.</param>
    /// <param name="cancellationToken">A token to cancel the write.</param>
    /// <returns>An awaitable Task.</returns>
    Task WriteAsync(string command, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes the specified <paramref name="command"/> to the server.
    /// </summary>
    /// <param name="command">The command.</param>
    /// <returns>An awaitable Task.</returns>
    Task WriteLineAsync(string command);

    /// <summary>
    /// Writes the specified <paramref name="command"/> to the server.
    /// </summary>
    /// <param name="command">The command.</param>
    /// <param name="lineFeed">The type of lineFeed to use. For legacy reasons the default "\n" is supplied, but to be RFC854 compliant "\r\n" should be supplied.</param>
    /// <returns>An awaitable Task.</returns>
    Task WriteLineAsync(string command, string lineFeed = Client.LegacyLineFeed);

    /// <summary>
    /// Writes the specified <paramref name="command"/> to the server.
    /// </summary>
    /// <param name="command">The command.</param>
    /// <returns>An awaitable Task.</returns>
    Task WriteLineRfc854Async(string command);

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
    Task SendCommand(Commands command, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a TELNET Synch signal: TCP Urgent notification with DM as the
    /// urgent octet (RFC 854). Requires the byte stream to be a
    /// <see cref="TcpByteStream"/> over a real TCP connection; otherwise
    /// throws <see cref="NotSupportedException"/>.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the send.</param>
    /// <returns>An awaitable Task.</returns>
    Task SendSynchAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks the peer to enable <paramref name="telnetOption"/> (sends
    /// <c>IAC DO</c>), unless already enabled, already negotiating, or
    /// refused without new stimulus. An explicit call is new stimulus.
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
    /// Asks the peer to place a timing mark (sends <c>IAC DO
    /// TIMING-MARK</c>, RFC 860). The peer returns <c>IAC WILL
    /// TIMING-MARK</c> once everything sent before the mark has drained,
    /// which implements the round-trip and flush-discard patterns of
    /// RFC 860 §5.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the send.</param>
    /// <returns>An awaitable Task.</returns>
    Task SendTimingMarkAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-sends the NAWS terminal size (RFC 1073) when it changed since the
    /// last report. Sends nothing unless we are the WILL-sender: a server
    /// <c>DON'T</c> after accept suppresses further updates.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the send.</param>
    /// <returns>An awaitable Task.</returns>
    Task RefreshWindowSizeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Requests the server's LINEMODE special-character table (RFC 1184
    /// §2.4: SLC func 0 with DEFAULT). Sends nothing unless LINEMODE is
    /// agreed (we are the WILL-sender).
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the send.</param>
    /// <returns>An awaitable Task.</returns>
    Task ImportRemoteSpecialCharactersAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Exports the configured LINEMODE special characters to the server
    /// (RFC 1184 §5.5). Sends nothing unless LINEMODE is agreed, and
    /// nothing at all when no SLC row is configured.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the send.</param>
    /// <returns>An awaitable Task.</returns>
    Task ExportSpecialCharactersAsync(CancellationToken cancellationToken = default);
  }
}
