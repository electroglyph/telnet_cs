namespace telnet_cs.Client
{
    using System;
    using System.Collections.Generic;
    using System.Text.RegularExpressions;
    using System.Threading;
    using telnet_cs.Transport;

    /// <summary>
    /// The base class for Clients.
    /// </summary>
    public abstract partial class BaseClient : IBaseClient
    {
        /// <summary>
        /// The default time out ms.
        /// </summary>
        protected const int DefaultTimeoutMs = 100;

        /// <summary>
        /// The terminated-read buffer limit in characters (the reference
        /// <c>_DEFAULT_LIMIT</c> of 65536): a terminated read whose buffer
        /// passes this without locating its terminator throws instead of
        /// growing without bound (the C# analog of
        /// <c>LimitOverrunError</c>).
        /// </summary>
        protected const int TerminatedReadLimit = 65536;

        /// <summary>
        /// The default read delay ms.
        /// </summary>
        public const int DefaultMillisecondReadDelay = 16;

        /// <summary>
        /// The byte stream.
        /// </summary>
        private readonly IByteStream byteStream;

        // Serializes every PendingText read and write (see the property).
        private readonly Lock pendingTextLock = new();
        private string pendingText = string.Empty;

        /// <inheritdoc/>
        public int MillisecondReadDelay { get; set; } = DefaultMillisecondReadDelay;

        /// <summary>
        /// Raised when an unsuppressed Go-Ahead arrives (RFC 858 §5: GA is a NOP
        /// only while Suppress-GA is in effect; otherwise it is the NVT
        /// turn-taking signal). Raised synchronously on the read path that
        /// received it. GA suppressed by agreement is consumed silently and never
        /// raises this event.
        /// </summary>
        public event EventHandler? GoAheadReceived;

        /// <summary>
        /// Raises <see cref="GoAheadReceived"/>. Fed to the per-read handler's
        /// hook so the signal survives handler-per-read construction.
        /// </summary>
        protected void OnGoAheadReceived() => GoAheadReceived?.Invoke(this, EventArgs.Empty);

        /// <summary>
        /// Text read past a terminator by a terminated read, held for the next
        /// plain read so pipelined data is never lost. Terminated reads cut
        /// their result at the first terminator and stash the remainder here;
        /// plain reads drain it before touching the wire.
        /// This plus <c>TerminatedReadAsync</c> polling is the replacement for
        /// the reference <c>readuntil</c>/<c>readline</c>/<c>eager</c> family:
        /// an unterminated wait throws <c>TimeoutException</c> (never a
        /// partial), at the cost of one rolling-window poll per
        /// millisecond-spin step.
        /// All access serializes on <c>pendingTextLock</c>: compound updates
        /// use the atomic <c>DrainPendingText</c> / <c>AppendPendingText</c> /
        /// <c>PrependPendingText</c> helpers so operator-concurrent readers
        /// neither lose nor duplicate bytes. The helpers take only this lock,
        /// so an outer lock may be held across them only when it is always
        /// taken outside this one (the server drain holds <c>pumpLock</c>).
        /// </summary>
        protected string PendingText
        {
            get { lock (pendingTextLock) { return pendingText; } }
            set { lock (pendingTextLock) { pendingText = value; } }
        }

        /// <summary>
        /// Atomically drains <see cref="PendingText"/>, leaving it empty.
        /// </summary>
        /// <returns>The stashed text, or empty when none was stashed.</returns>
        protected string DrainPendingText()
        {
            lock (pendingTextLock)
            {
                string drained = pendingText;
                pendingText = string.Empty;
                return drained;
            }
        }

        /// <summary>
        /// Atomically appends <paramref name="text"/> to
        /// <see cref="PendingText"/>.
        /// </summary>
        /// <param name="text">The text to append.</param>
        protected void AppendPendingText(string text)
        {
            lock (pendingTextLock)
            {
                pendingText += text;
            }
        }

        /// <summary>
        /// Atomically prepends <paramref name="text"/> before
        /// <see cref="PendingText"/>.
        /// </summary>
        /// <param name="text">The text to prepend.</param>
        protected void PrependPendingText(string text)
        {
            lock (pendingTextLock)
            {
                pendingText = text + pendingText;
            }
        }

        /// <inheritdoc/>
        public bool IsConnected
        {
            get
            {
                return byteStream.Connected;
            }
        }

        /// <summary>
        /// Gets the byte stream.
        /// </summary>
        protected IByteStream ByteStream
        {
            get
            {
                return byteStream;
            }
        }

        /// <summary>
        /// Gets the stream outbound bytes are written to. The base
        /// implementation is the raw connection stream; the server session
        /// overrides it with its MCCP2 compressing view once the peer
        /// accepts outbound compression, so every writer (session methods,
        /// per-read handlers, go-ahead) compresses without knowing about it.
        /// Reads, connection checks and closes always use
        /// <see cref="ByteStream"/> directly.
        /// </summary>
        protected virtual IByteStream WriteStream => ByteStream;

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Determines whether the specified terminator has been located.
        /// </summary>
        /// <param name="terminator">The terminator to search for.</param>
        /// <param name="s">The content to search for the <paramref name="terminator"/>.</param>
        /// <returns>True if the terminator is located, otherwise false.</returns>
        protected static bool IsTerminatorLocated(string? terminator, string? s)
        {
            if (string.IsNullOrEmpty(s))
            {
                return false;
            }

            // Ordinal: terminators are protocol tokens, and a culture-aware
            // comparison treats NUL as ignorable (same class of bug as the old
            // ByteStringConverter "\0xFF" issue).
            return !string.IsNullOrEmpty(terminator) && s.Contains(terminator, StringComparison.Ordinal);
        }

        /// <summary>
        /// Determines whether any of the specified terminators has been located.
        /// An empty collection never matches (consistent with <see cref="IsTerminatorLocated"/> on empty input).
        /// </summary>
        /// <param name="terminators">The terminators to search for.</param>
        /// <param name="s">The content to search.</param>
        /// <returns>True if any terminator is located, otherwise false.</returns>
        protected static bool IsAnyTerminatorLocated(IEnumerable<string>? terminators, string? s)
        {
            if (terminators == null || string.IsNullOrEmpty(s))
            {
                return false;
            }

            foreach (var terminator in terminators)
            {
                if (IsTerminatorLocated(terminator, s))
                {
                    return true;
                }
            }

            return false;
        }
        /// <summary>
        /// Determines whether the specified Regex has matched. A null Regex never matches.
        /// </summary>
        /// <param name="regex">The Regex to search for.</param>
        /// <param name="s">The content to search for the <paramref name="regex"/>.</param>
        /// <returns>True if the Regex is matched, otherwise false.</returns>
        protected static bool IsRegexLocated(Regex? regex, string? s)
        {
            if (string.IsNullOrEmpty(s) || regex == null)
            {
                return false;
            }

            return regex.IsMatch(s);
        }

        /// <summary>
        /// Determines whether any of the specified Regexes has matched.
        /// An empty collection never matches.
        /// </summary>
        /// <param name="regexes">The Regexes to match.</param>
        /// <param name="s">The content to search.</param>
        /// <returns>True if any Regex matches, otherwise false.</returns>
        protected static bool IsAnyRegexLocated(IEnumerable<Regex>? regexes, string? s)
        {
            if (regexes == null || string.IsNullOrEmpty(s))
            {
                return false;
            }

            foreach (var regex in regexes)
            {
                if (IsRegexLocated(regex, s))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
