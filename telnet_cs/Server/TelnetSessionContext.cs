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
        /// Gets the last text read or written (UTC). Reads that returned no
        /// data do not move it, so an idle peer is genuinely idle.
        /// </summary>
        public DateTimeOffset LastActivityUtc { get; private set; } = DateTimeOffset.UtcNow;

        /// <summary>
        /// Gets how long the session has been idle.
        /// </summary>
        public TimeSpan Idle => DateTimeOffset.UtcNow - LastActivityUtc;

        /// <summary>
        /// Gets the count of text characters read. Raw-byte writes bypass
        /// accounting (only the string write paths note their text).
        /// </summary>
        public long CharsReceived { get; private set; }

        /// <summary>
        /// Gets the count of text characters written. Raw-byte writes bypass
        /// accounting (only the string write paths note their text).
        /// </summary>
        public long CharsSent { get; private set; }

        /// <summary>
        /// Gets or sets the typescript recorder. When set, every text chunk
        /// read or written is appended raw (both directions, no prefixes —
        /// the reference records server output only; recording both keeps one
        /// hook for the two string paths). A failing writer is detached, never
        /// fatal to the session.
        /// </summary>
        public TextWriter? Typescript { get; set; }

        /// <summary>
        /// Gets the open property bag for shell-specific data (the analogue
        /// of the reference MUD-data slots on <c>writer.ctx</c>).
        /// </summary>
        public IDictionary<string, object?> Properties { get; } = new Dictionary<string, object?>(StringComparer.Ordinal);

        internal void NoteRead(string text)
        {
            LastActivityUtc = DateTimeOffset.UtcNow;
            CharsReceived += text.Length;
            RecordTranscript(text);
        }

        internal void NoteWritten(string text)
        {
            LastActivityUtc = DateTimeOffset.UtcNow;
            CharsSent += text.Length;
            RecordTranscript(text);
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
