namespace telnet_cs.Protocol
{
    using telnet_cs.Client;
    using telnet_cs.IO;

    /// <summary>
    /// RFC 1091 terminal-type cycling: walks an ordered type list (most to
    /// least specific) across successive SENDs. The final entry is sent twice
    /// to signal end-of-list, then the next SEND wraps to the top (RFC 1091
    /// section 6). Owned by <see cref="Client"/> so the position survives
    /// across per-read <see cref="ByteStreamHandler"/> instances.
    /// </summary>
    internal sealed class TerminalTypeCycler
    {
        internal const int MaxLength = 40;

        internal const string Unknown = "UNKNOWN";

        private readonly IReadOnlyList<string> types;

        private readonly Lock sync = new();

        private int index;

        private bool endSignaled;

        internal TerminalTypeCycler(IReadOnlyList<string> types)
        {
            ArgumentNullException.ThrowIfNull(types);
            this.types = types.Count == 0 ? [Unknown] : types;
        }

        internal bool Matches(IReadOnlyList<string> other)
        {
            ArgumentNullException.ThrowIfNull(other);
            lock (sync)
            {
                return types.SequenceEqual(other, StringComparer.OrdinalIgnoreCase);
            }
        }

        internal string Next()
        {
            lock (sync)
            {
                string current = types[index];
                if (index == types.Count - 1)
                {
                    if (endSignaled)
                    {
                        index = 0;
                        endSignaled = false;
                    }
                    else
                    {
                        endSignaled = true;
                    }
                }
                else
                {
                    index++;
                }

                return current;
            }
        }
    }
}
