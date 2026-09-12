namespace telnet_cs.Client
{
    using System;
    using System.Collections.Generic;
    using System.Collections.Frozen;

    /// <summary>
    /// Byte-level input translation for retro terminal input (the reference
    /// <c>InputFilter</c>): longest-first escape-sequence replacement with
    /// prefix buffering, plus single-byte replacement. A trailing fragment
    /// that could still grow into a mapped sequence is held back until more
    /// input arrives or <see cref="Flush"/> forces it out (the reference
    /// ESC-delay behaviour; scheduling the flush after
    /// <see cref="EscapeDelay"/> is the caller's job).
    /// Instances are stateful (the hold-back buffer); share the static maps,
    /// not the filter.
    /// </summary>
    public sealed class InputFilter
    {
        /// <summary>
        /// Gets the default ESC-delay hint (the reference <c>ESC_DELAY</c>,
        /// 350 ms): how long to wait for a sequence tail before flushing.
        /// </summary>
        public static readonly TimeSpan DefaultEscapeDelay = TimeSpan.FromMilliseconds(350);

        /// <summary>
        /// Gets the ATASCII single-byte map (the reference
        /// <c>_INPUT_XLAT["atascii"]</c>): <c>DEL/BS → 0x7E</c>,
        /// <c>CR/LF → 0x9B</c>.
        /// </summary>
        public static IReadOnlyDictionary<byte, byte> AtasciiSingleBytes { get; } = new Dictionary<byte, byte>
        {
            [0x7F] = 0x7E,
            [0x08] = 0x7E,
            [0x0D] = 0x9B,
            [0x0A] = 0x9B,
        }.ToFrozenDictionary();

        /// <summary>
        /// Gets the PETSCII single-byte map (the reference
        /// <c>_INPUT_XLAT["petscii"]</c>): <c>DEL/BS → 0x14</c>.
        /// </summary>
        public static IReadOnlyDictionary<byte, byte> PetsciiSingleBytes { get; } = new Dictionary<byte, byte>
        {
            [0x7F] = 0x14,
            [0x08] = 0x14,
        }.ToFrozenDictionary();

        /// <summary>
        /// Gets the ATASCII escape-sequence map (the reference
        /// <c>_INPUT_SEQ_XLAT["atascii"]</c>): CSI + SS3 arrows, <c>3~</c>
        /// delete → backspace, TAB → ATASCII tab.
        /// </summary>
        public static IReadOnlyList<KeyValuePair<byte[], byte[]>> AtasciiSequences { get; } = BuildSequences(
          (new byte[] { 0x1B, 0x5B, 0x41 }, new byte[] { 0x1C }),
          (new byte[] { 0x1B, 0x5B, 0x42 }, new byte[] { 0x1D }),
          (new byte[] { 0x1B, 0x5B, 0x43 }, new byte[] { 0x1F }),
          (new byte[] { 0x1B, 0x5B, 0x44 }, new byte[] { 0x1E }),
          (new byte[] { 0x1B, 0x4F, 0x41 }, new byte[] { 0x1C }),
          (new byte[] { 0x1B, 0x4F, 0x42 }, new byte[] { 0x1D }),
          (new byte[] { 0x1B, 0x4F, 0x43 }, new byte[] { 0x1F }),
          (new byte[] { 0x1B, 0x4F, 0x44 }, new byte[] { 0x1E }),
          (new byte[] { 0x1B, 0x5B, 0x33, 0x7E }, new byte[] { 0x7E }),
          (new byte[] { 0x09 }, new byte[] { 0x7F }));

        /// <summary>
        /// Gets the PETSCII escape-sequence map (the reference
        /// <c>_INPUT_SEQ_XLAT["petscii"]</c>): CSI + SS3 arrows, <c>3~</c>
        /// delete → DEL, home, insert.
        /// </summary>
        public static IReadOnlyList<KeyValuePair<byte[], byte[]>> PetsciiSequences { get; } = BuildSequences(
          (new byte[] { 0x1B, 0x5B, 0x41 }, new byte[] { 0x91 }),
          (new byte[] { 0x1B, 0x5B, 0x42 }, new byte[] { 0x11 }),
          (new byte[] { 0x1B, 0x5B, 0x43 }, new byte[] { 0x1D }),
          (new byte[] { 0x1B, 0x5B, 0x44 }, new byte[] { 0x9D }),
          (new byte[] { 0x1B, 0x4F, 0x41 }, new byte[] { 0x91 }),
          (new byte[] { 0x1B, 0x4F, 0x42 }, new byte[] { 0x11 }),
          (new byte[] { 0x1B, 0x4F, 0x43 }, new byte[] { 0x1D }),
          (new byte[] { 0x1B, 0x4F, 0x44 }, new byte[] { 0x9D }),
          (new byte[] { 0x1B, 0x5B, 0x33, 0x7E }, new byte[] { 0x14 }),
          (new byte[] { 0x1B, 0x5B, 0x48 }, new byte[] { 0x13 }),
          (new byte[] { 0x1B, 0x5B, 0x32, 0x7E }, new byte[] { 0x94 }));

        private readonly List<KeyValuePair<byte[], byte[]>> sequences;
        private readonly Dictionary<byte, byte> singleBytes;
        private readonly List<byte> pending = new();

        /// <summary>
        /// Initialises a new instance of the <see cref="InputFilter"/> class.
        /// </summary>
        /// <param name="sequences">Escape-sequence replacements (longest match wins). Null means none.</param>
        /// <param name="singleBytes">Single-byte replacements. Null means none.</param>
        /// <param name="escapeDelay">The ESC-delay hint. Null selects <see cref="DefaultEscapeDelay"/>.</param>
        public InputFilter(
          IEnumerable<KeyValuePair<byte[], byte[]>>? sequences = null,
          IReadOnlyDictionary<byte, byte>? singleBytes = null,
          TimeSpan? escapeDelay = null)
        {
            this.sequences = sequences is null ? new List<KeyValuePair<byte[], byte[]>>() : new List<KeyValuePair<byte[], byte[]>>(sequences);
            foreach (var entry in this.sequences)
            {
                ArgumentNullException.ThrowIfNull(entry.Key);
                ArgumentNullException.ThrowIfNull(entry.Value);
                if (entry.Key.Length == 0)
                {
                    throw new ArgumentException("Sequence keys must be non-empty.", nameof(sequences));
                }
            }

            // Longest first, so a shared prefix (CSI vs SS3 tails, 3~/2~)
            // always prefers the full sequence.
            this.sequences.Sort(static (left, right) => right.Key.Length.CompareTo(left.Key.Length));
            this.singleBytes = singleBytes is null
              ? new Dictionary<byte, byte>()
              : new Dictionary<byte, byte>(singleBytes);
            EscapeDelay = escapeDelay ?? DefaultEscapeDelay;
        }

        /// <summary>
        /// Creates an ATASCII filter (single-byte + sequence maps).
        /// </summary>
        /// <param name="escapeDelay">The ESC-delay hint. Null selects <see cref="DefaultEscapeDelay"/>.</param>
        /// <returns>The filter.</returns>
        public static InputFilter CreateAtascii(TimeSpan? escapeDelay = null) =>
          new(AtasciiSequences, AtasciiSingleBytes, escapeDelay);

        /// <summary>
        /// Creates a PETSCII filter (single-byte + sequence maps).
        /// </summary>
        /// <param name="escapeDelay">The ESC-delay hint. Null selects <see cref="DefaultEscapeDelay"/>.</param>
        /// <returns>The filter.</returns>
        public static InputFilter CreatePetscii(TimeSpan? escapeDelay = null) =>
          new(PetsciiSequences, PetsciiSingleBytes, escapeDelay);

        /// <summary>
        /// Gets the ESC-delay hint.
        /// </summary>
        public TimeSpan EscapeDelay { get; }

        /// <summary>
        /// Gets whether unflushed input is held back (a possible sequence tail).
        /// </summary>
        public bool HasPending => pending.Count != 0;

        /// <summary>
        /// Feeds input bytes, returning the translated output. A trailing
        /// fragment that is a proper prefix of a mapped sequence is held for
        /// the next call (or <see cref="Flush"/>).
        /// </summary>
        /// <param name="data">The input bytes.</param>
        /// <returns>The translated bytes.</returns>
        public byte[] Feed(ReadOnlySpan<byte> data)
        {
            pending.AddRange(data.ToArray());
            return Drain(holdPartial: true);
        }

        /// <summary>
        /// Forces out held input: remaining bytes go through the single-byte
        /// map only (a lone ESC emits ESC).
        /// </summary>
        /// <returns>The translated bytes.</returns>
        public byte[] Flush() => Drain(holdPartial: false);

        private static IReadOnlyList<KeyValuePair<byte[], byte[]>> BuildSequences(params (byte[] From, byte[] To)[] entries)
        {
            var list = new List<KeyValuePair<byte[], byte[]>>(entries.Length);
            foreach (var (from, to) in entries)
            {
                list.Add(new KeyValuePair<byte[], byte[]>(from, to));
            }

            // Freeze the shared table so process-wide state cannot be
            // mutated through a cast; instances sort a private copy.
            return list.AsReadOnly();
        }

        private byte[] Drain(bool holdPartial)
        {
            var output = new List<byte>(pending.Count);
            int i = 0;
            while (i < pending.Count)
            {
                int matchLength = -1;
                byte[]? replacement = null;
                foreach (var entry in sequences)
                {
                    if (StartsWith(i, entry.Key))
                    {
                        matchLength = entry.Key.Length;
                        replacement = entry.Value;
                        break;
                    }
                }

                if (matchLength >= 0)
                {
                    output.AddRange(replacement!);
                    i += matchLength;
                    continue;
                }

                if (holdPartial && IsProperPrefix(i))
                {
                    break;
                }

                byte b = pending[i];
                output.Add(singleBytes.TryGetValue(b, out byte mapped) ? mapped : b);
                i++;
            }

            pending.RemoveRange(0, i);
            return [.. output];
        }

        private bool StartsWith(int offset, byte[] key)
        {
            if (pending.Count - offset < key.Length)
            {
                return false;
            }

            for (int k = 0; k < key.Length; k++)
            {
                if (pending[offset + k] != key[k])
                {
                    return false;
                }
            }

            return true;
        }

        private bool IsProperPrefix(int offset)
        {
            int remaining = pending.Count - offset;
            foreach (var entry in sequences)
            {
                if (entry.Key.Length <= remaining)
                {
                    continue;
                }

                bool prefix = true;
                for (int k = 0; k < remaining; k++)
                {
                    if (pending[offset + k] != entry.Key[k])
                    {
                        prefix = false;
                        break;
                    }
                }

                if (prefix)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
