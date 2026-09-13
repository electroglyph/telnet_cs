namespace telnet_cs.Server
{
    using System;
    using System.Collections.Generic;
    using System.IO;

    /// <summary>
    /// Per-session runtime state for a <see cref="ServerSession"/>: activity
    /// timestamps and character counters (driving the idle timeout and the
    /// status logger), an optional typescript recorder, and an open property
    /// bag for shell-specific data. The session-owned counterpart to the
    /// reference <c>TelnetSessionContext</c>: only the transcript/typescript
    /// and statistics roles port over — raw-mode/input-filter/autoreply
    /// belong to the interactive client shell, which this library does not
    /// ship (see the §12 scope decision in <c>feature.md</c>).
    /// </summary>
    public sealed class TelnetSessionContext
    {
        /// <summary>
        /// Gets when the session object was created (UTC).
        /// </summary>
        public DateTimeOffset ConnectedAtUtc { get; } = DateTimeOffset.UtcNow;

        /// <summary>
        /// Gets the last peer activity (UTC). Any inbound wire moves it — even
        /// IAC-only frames with no text (keepalives must not idle out) — while
        /// pure transmits never do, so a transmit-only server still times out.
        /// </summary>
        public DateTimeOffset LastActivityUtc { get; private set; } = DateTimeOffset.UtcNow;

        /// <summary>
        /// Gets how long the session has been idle.
        /// </summary>
        public TimeSpan Idle => DateTimeOffset.UtcNow - LastActivityUtc;

        /// <summary>
        /// Gets the count of raw wire bytes received, negotiation frames
        /// included (the reference counts <c>len(data)</c> on receipt, so a
        /// multibyte character contributes its encoded length and every IAC
        /// frame counts). Fed by the session from the per-read handler's
        /// wire counters; decoded text is never counted here.
        /// </summary>
        public long CharsReceived { get; private set; }

        /// <summary>
        /// Gets the count of raw wire bytes written, protocol frames
        /// included (the reference counts <c>len(buf)</c> on transmit, so an
        /// escaped IAC counts twice). Fed by every session write path with
        /// the exact on-the-wire length.
        /// </summary>
        public long CharsSent { get; private set; }

        /// <summary>
        /// Gets or sets the typescript recorder. When set, every text chunk
        /// read is appended raw. Writes are not recorded. A failing writer is
        /// detached, never fatal to the session.
        /// </summary>
        public TextWriter? Typescript { get; set; }

        /// <summary>
        /// Gets the open property bag for shell-specific data (the analogue
        /// of the reference MUD-data slots on <c>writer.ctx</c>).
        /// </summary>
        public IDictionary<string, object?> Properties { get; } = new Dictionary<string, object?>(StringComparer.Ordinal);

        internal void NoteActivity()
        {
            LastActivityUtc = DateTimeOffset.UtcNow;
        }

        internal void NoteRead(string text)
        {
            LastActivityUtc = DateTimeOffset.UtcNow;
            RecordTranscript(text);
        }

        /// <summary>
        /// Adds raw wire-byte deltas observed below the text layer (IAC
        /// frames, MCCP-compressed bytes, bytes consumed by negotiation
        /// replies). The session calls this once per read with the
        /// per-read handler's counters. Inbound bytes — even a lone IAC NOP
        /// with no text — mark peer activity (the reference stamps
        /// <c>_last_received</c> before parsing).
        /// </summary>
        internal void NoteWireTransfer(long bytesReceived, long bytesSent)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(bytesReceived);
            ArgumentOutOfRangeException.ThrowIfNegative(bytesSent);
            CharsReceived += bytesReceived;
            CharsSent += bytesSent;
            if (bytesReceived > 0)
            {
                LastActivityUtc = DateTimeOffset.UtcNow;
            }
        }

        internal void NoteWritten(int byteCount)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(byteCount);
            CharsSent += byteCount;
        }

        private void RecordTranscript(string text)
        {
            var writer = Typescript;
            if (writer is null || text.Length == 0)
            {
                return;
            }

#pragma warning disable CA1031 // Do not catch general exception types
            try
            {
                writer.Write(text);
            }
            catch (Exception ex)
            {
                // A dead transcript must not kill the session: detach and note.
                Typescript = null;
                System.Diagnostics.Debug.WriteLine(ex.Message);
            }
#pragma warning restore CA1031 // Do not catch general exception types
        }
    }
}
