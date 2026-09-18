namespace telnet_cs.Server;

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using telnet_cs.Protocol;
using telnet_cs.Transport;

/// <summary>
/// Server-role terminated reads: terminator-bounded line reads over the session pump plus the shared first-terminator cutter. Split from <see cref="ServerSession"/>; wire behavior is unchanged.
/// </summary>
public partial class ServerSession
{
    /// <summary>
    /// Reads asynchronously from the session, terminating as soon as the <paramref name="terminator"/> is located.
    /// </summary>
    /// <param name="terminator">The terminator.</param>
    /// <returns>Any text read from the session.</returns>
    public Task<string> TerminatedReadAsync(string terminator)
    {
        return TerminatedReadAsync(terminator, TimeSpan.FromMilliseconds(DefaultTimeoutMs));
    }

    /// <summary>
    /// Reads asynchronously from the session, terminating as soon as the <paramref name="terminator"/> is located.
    /// </summary>
    /// <param name="terminator">The terminator.</param>
    /// <param name="timeout">The timeout.</param>
    /// <returns>Any text read from the session.</returns>
    public Task<string> TerminatedReadAsync(string terminator, TimeSpan timeout)
    {
        return TerminatedReadAsync(terminator, timeout, 1);
    }

    /// <summary>
    /// Reads asynchronously from the session, terminating as soon as the <paramref name="regex"/> is located.
    /// </summary>
    /// <param name="regex">The regex to match.</param>
    /// <param name="timeout">The timeout.</param>
    /// <returns>Any text read from the session.</returns>
    public Task<string> TerminatedReadAsync(Regex regex, TimeSpan timeout)
    {
        return TerminatedReadAsync(regex, timeout, 1);
    }

    /// <summary>
    /// Reads asynchronously from the session, terminating as soon as any of the <paramref name="terminators"/> is located.
    /// </summary>
    /// <param name="terminators">The terminators to search for.</param>
    /// <returns>Any text read from the session.</returns>
    public Task<string> TerminatedReadAsync(IEnumerable<string> terminators)
    {
        return TerminatedReadAsync(terminators, TimeSpan.FromMilliseconds(DefaultTimeoutMs));
    }

    /// <summary>
    /// Reads asynchronously from the session, terminating as soon as any of the <paramref name="terminators"/> is located.
    /// </summary>
    /// <param name="terminators">The terminators to search for.</param>
    /// <param name="timeout">The timeout.</param>
    /// <returns>Any text read from the session.</returns>
    public Task<string> TerminatedReadAsync(IEnumerable<string> terminators, TimeSpan timeout)
    {
        return TerminatedReadAsync(terminators, timeout, 1);
    }

    /// <summary>
    /// Reads asynchronously from the session, terminating as soon as any of the <paramref name="regexes"/> is matched.
    /// </summary>
    /// <param name="regexes">The regexes to match.</param>
    /// <returns>Any text read from the session.</returns>
    public Task<string> TerminatedReadAsync(IEnumerable<Regex> regexes)
    {
        return TerminatedReadAsync(regexes, TimeSpan.FromMilliseconds(DefaultTimeoutMs));
    }

    /// <summary>
    /// Reads asynchronously from the session, terminating as soon as any of the <paramref name="regexes"/> is matched.
    /// </summary>
    /// <param name="regexes">The regexes to match.</param>
    /// <param name="timeout">The timeout.</param>
    /// <returns>Any text read from the session.</returns>
    public Task<string> TerminatedReadAsync(IEnumerable<Regex> regexes, TimeSpan timeout)
    {
        return TerminatedReadAsync(regexes, timeout, 1);
    }

    /// <summary>
    /// Reads asynchronously from the session, terminating as soon as the <paramref name="terminator"/> is located.
    /// </summary>
    /// <param name="terminator">The terminator.</param>
    /// <param name="timeout">The maximum time to wait.</param>
    /// <param name="millisecondSpin">The millisecond spin between each read from the session.</param>
    /// <returns>Any text read from the session.</returns>
    public Task<string> TerminatedReadAsync(string terminator, TimeSpan timeout, int millisecondSpin)
    {
        return TerminatedReadAsync(terminator, timeout, millisecondSpin, CancellationToken.None);
    }

    /// <summary>
    /// Reads asynchronously from the session, terminating as soon as the <paramref name="terminator"/> is located.
    /// </summary>
    /// <param name="terminator">The terminator.</param>
    /// <param name="timeout">The maximum time to wait.</param>
    /// <param name="millisecondSpin">The millisecond spin between each read from the session.</param>
    /// <param name="cancellationToken">Token to cancel the read.</param>
    /// <returns>Any text read from the session.</returns>
    public async Task<string> TerminatedReadAsync(string terminator, TimeSpan timeout, int millisecondSpin, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(terminator);
        bool isTerminated(string x) => IsTerminatorLocated(terminator, x);
        var s = await TerminatedReadAsync(isTerminated, timeout, millisecondSpin, cancellationToken).ConfigureAwait(false);
        s = CutAtFirstTerminator(s, terminator);
        if (!isTerminated(s))
        {
            WriteLog($"Failed to terminate '{s}' with '{terminator}'");
        }

        return s;
    }

    private string CutAtFirstTerminator(string s, string terminator)
    {
        string cut = CutAtFirstTerminator(s, terminator, out string remainder);
        if (remainder.Length != 0)
        {
            PrependPendingText(remainder);
        }

        return cut;
    }

    /// <summary>
    /// Reads asynchronously from the session, terminating as soon as the <paramref name="regex"/> is matched.
    /// </summary>
    /// <param name="regex">The regex to match.</param>
    /// <param name="timeout">The maximum time to wait.</param>
    /// <param name="millisecondSpin">The millisecond spin between each read from the session.</param>
    /// <returns>Any text read from the session.</returns>
    public Task<string> TerminatedReadAsync(Regex regex, TimeSpan timeout, int millisecondSpin)
    {
        return TerminatedReadAsync(regex, timeout, millisecondSpin, CancellationToken.None);
    }

    /// <summary>
    /// Reads asynchronously from the session, terminating as soon as the <paramref name="regex"/> is matched.
    /// </summary>
    /// <param name="regex">The regex to match.</param>
    /// <param name="timeout">The maximum time to wait.</param>
    /// <param name="millisecondSpin">The millisecond spin between each read from the session.</param>
    /// <param name="cancellationToken">Token to cancel the read.</param>
    /// <returns>Any text read from the session.</returns>
    public async Task<string> TerminatedReadAsync(Regex regex, TimeSpan timeout, int millisecondSpin, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(regex);
        bool isTerminated(string x) => IsRegexLocated(regex, x);
        var s = await TerminatedReadAsync(isTerminated, timeout, millisecondSpin, cancellationToken).ConfigureAwait(false);
        if (!isTerminated(s))
        {
            WriteLog($"Failed to match '{s}' with '{regex}'");
        }

        return s;
    }

    /// <summary>
    /// Reads asynchronously from the session, terminating as soon as any of the <paramref name="terminators"/> is located.
    /// </summary>
    /// <param name="terminators">The terminators to search for.</param>
    /// <param name="timeout">The maximum time to wait.</param>
    /// <param name="millisecondSpin">The millisecond spin between each read from the session.</param>
    /// <param name="cancellationToken">Token to cancel the read.</param>
    /// <returns>Any text read from the session.</returns>
    public async Task<string> TerminatedReadAsync(IEnumerable<string> terminators, TimeSpan timeout, int millisecondSpin, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(terminators);
        bool isTerminated(string x) => IsAnyTerminatorLocated(terminators, x);
        var s = await TerminatedReadAsync(isTerminated, timeout, millisecondSpin, cancellationToken).ConfigureAwait(false);
        if (!isTerminated(s))
        {
            WriteLog($"Failed to terminate '{s}' with any known terminator");
        }

        return s;
    }

    /// <summary>
    /// Reads asynchronously from the session, terminating as soon as any of the <paramref name="regexes"/> is matched.
    /// </summary>
    /// <param name="regexes">The regexes to match.</param>
    /// <param name="timeout">The maximum time to wait.</param>
    /// <param name="millisecondSpin">The millisecond spin between each read from the session.</param>
    /// <param name="cancellationToken">Token to cancel the read.</param>
    /// <returns>Any text read from the session.</returns>
    public async Task<string> TerminatedReadAsync(IEnumerable<Regex> regexes, TimeSpan timeout, int millisecondSpin, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(regexes);
        bool isTerminated(string x) => IsAnyRegexLocated(regexes, x);
        var s = await TerminatedReadAsync(isTerminated, timeout, millisecondSpin, cancellationToken).ConfigureAwait(false);
        if (!isTerminated(s))
        {
            WriteLog($"Failed to match '{s}' with any known pattern");
        }

        return s;
    }

    // The readuntil/readline replacement (see PendingText): poll plain
    // reads until the predicate holds or the timeout lapses. Reference
    // readuntil parity: never returns a partial — a missed deadline
    // throws TimeoutException (not "") and a buffer past the configured
    // terminated-read limit (64 KiB default, 0 disables) throws (the C#
    // analog of LimitOverrunError).
    private async Task<string> TerminatedReadAsync(Func<string, bool> isTerminated, TimeSpan timeout, int millisecondSpin, CancellationToken cancellationToken)
    {
        var endTimeout = DateTime.UtcNow.Add(timeout);
        var s = string.Empty;
        while (!isTerminated(s) && endTimeout >= DateTime.UtcNow)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = await ReadAsync(TimeSpan.FromMilliseconds(millisecondSpin), cancellationToken).ConfigureAwait(false);
            s += read;
            int limit = MaxTerminatedReadChars ?? Settings.MaxTerminatedReadChars;
            if (limit > 0 && !isTerminated(s) && s.Length > limit)
            {
                throw new InvalidOperationException(TerminatedReadLimitMessage(limit));
            }
        }

        if (!isTerminated(s))
        {
            throw new TimeoutException("Terminated read timed out before locating the terminator.");
        }

        // CR NUL is the wire spelling of a lone CR (RFC 854): raw reads
        // preserve both bytes, line helpers normalize — same as the client.
        return s.Replace("\r\0", "\r", StringComparison.Ordinal);
    }
}
