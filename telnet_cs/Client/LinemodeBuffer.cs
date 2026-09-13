namespace telnet_cs.Client
{
    using System;
    using System.Collections.Generic;
    using System.Collections.Frozen;
    using System.Text;
    using telnet_cs.Protocol;

    /// <summary>
    /// The result of feeding one character to <see cref="LinemodeBuffer"/>:
    /// text to display locally, plus bytes to send to the server (null while
    /// buffering).
    /// </summary>
    /// <param name="Echo">Text to display locally (may be empty).</param>
    /// <param name="Data">Bytes to send to the server, or null if buffering.</param>
    public readonly record struct LinemodeEdit(string Echo, byte[]? Data);

    /// <summary>
    /// Client-side line buffer for LINEMODE EDIT mode (RFC 1184 §3.1): local
    /// SLC editing (erase-char/line/word), signal trapping, forwardmask and
    /// CR/LF line sends. Mirrors the reference <c>LinemodeBuffer.feed</c>
    /// check order exactly — trapsig, EC, EL, EW, CR/LF, forwardmask, buffer —
    /// with the same echo strings (<c>"\b \b"</c> repeats, trigger echo) and
    /// UTF-8 line encoding.
    /// </summary>
    public sealed class LinemodeBuffer
    {
        /// <summary>
        /// Erase-word SLC function number (RFC 1184 §5.5: EW = 12). No shared
        /// constant exists yet, so it lives here.
        /// </summary>
        private const int SlcEraseWord = 12;

        /// <summary>
        /// Gets the default SLC value table matching the reference BSD defaults:
        /// EOF, EC, EL, IP, ABORT, SUSP, EW, AYT plus AO, RP, LNEXT, XON and
        /// XOFF. Used when the server sent no SLC triplets.
        /// </summary>
        public static IReadOnlyDictionary<int, int> DefaultSlc { get; } = new Dictionary<int, int>
        {
            [LinemodeProtocol.SlcEof] = 0x04,
            [LinemodeProtocol.SlcEraseCharacter] = 0x7F,
            [LinemodeProtocol.SlcEraseLine] = 0x15,
            [LinemodeProtocol.SlcIp] = 0x03,
            [LinemodeProtocol.SlcAbort] = 0x1C,
            [LinemodeProtocol.SlcSuspend] = 0x1A,
            [SlcEraseWord] = 0x17,
            [LinemodeProtocol.SlcAyt] = 0x14,
            [LinemodeProtocol.SlcAbortOutput] = 0x0F,
            [LinemodeProtocol.SlcReprint] = 0x12,
            [LinemodeProtocol.SlcLiteralNext] = 0x16,
            [LinemodeProtocol.SlcXon] = 0x11,
            [LinemodeProtocol.SlcXoff] = 0x13,
        }.ToFrozenDictionary();

        private readonly Dictionary<int, int> slcValues;
        private readonly HashSet<int> forwardMask;
        private readonly StringBuilder line = new();

        /// <summary>
        /// Initialises a new instance of the <see cref="LinemodeBuffer"/> class.
        /// </summary>
        /// <param name="slcValues">SLC function number → active byte value (unsupported functions simply never match). Null selects <see cref="DefaultSlc"/>.</param>
        /// <param name="forwardMask">Forwardmask byte values. Null means none.</param>
        /// <param name="trapSignal">When <c>true</c>, signal characters go out as <c>IAC</c> commands instead of buffering.</param>
        public LinemodeBuffer(
          IReadOnlyDictionary<int, int>? slcValues = null,
          IEnumerable<int>? forwardMask = null,
          bool trapSignal = false)
        {
            this.slcValues = slcValues is null ? new Dictionary<int, int>(DefaultSlc) : new Dictionary<int, int>(slcValues);
            this.forwardMask = forwardMask is null ? new HashSet<int>() : new HashSet<int>(forwardMask);
            TrapSignal = trapSignal;
        }

        /// <summary>
        /// Gets or sets whether signal characters are trapped to <c>IAC</c>
        /// commands (the reference <c>trapsig</c> flag, from LINEMODE TRAPSIG).
        /// </summary>
        public bool TrapSignal { get; set; }

        /// <summary>
        /// Gets the buffered character count.
        /// </summary>
        public int Length => line.Length;

        /// <summary>
        /// Feeds one character, returning the local echo plus optional send
        /// data (see <see cref="LinemodeEdit"/>).
        /// </summary>
        /// <param name="character">The typed character.</param>
        /// <returns>The echo text and optional send bytes.</returns>
        public LinemodeEdit Feed(char character)
        {
            int b = character;
            if (TrapSignal && TryTrap(b, out byte[]? command))
            {
                return new LinemodeEdit(string.Empty, command);
            }

            if (b == SlcValue(LinemodeProtocol.SlcEraseCharacter))
            {
                if (line.Length == 0)
                {
                    return new LinemodeEdit(string.Empty, null);
                }

                line.Remove(line.Length - 1, 1);
                return new LinemodeEdit("\b \b", null);
            }

            if (b == SlcValue(LinemodeProtocol.SlcEraseLine))
            {
                string echo = new StringBuilder().Insert(0, "\b \b", line.Length).ToString();
                line.Clear();
                return new LinemodeEdit(echo, null);
            }

            if (b == SlcValue(SlcEraseWord))
            {
                int popped = 0;
                // Skip trailing spaces first (POSIX VWERASE behaviour).
                while (line.Length != 0 && line[line.Length - 1] == ' ')
                {
                    line.Remove(line.Length - 1, 1);
                    popped++;
                }

                while (line.Length != 0 && line[line.Length - 1] != ' ')
                {
                    line.Remove(line.Length - 1, 1);
                    popped++;
                }

                return new LinemodeEdit(new StringBuilder().Insert(0, "\b \b", popped).ToString(), null);
            }

            if (character is '\r' or '\n')
            {
                string text = line.ToString() + character;
                line.Clear();
                return new LinemodeEdit(character.ToString(), Encoding.UTF8.GetBytes(text));
            }

            if (forwardMask.Contains(b))
            {
                string text = line.ToString() + character;
                line.Clear();
                return new LinemodeEdit(character.ToString(), Encoding.UTF8.GetBytes(text));
            }

            line.Append(character);
            return new LinemodeEdit(character.ToString(), null);
        }

        /// <summary>
        /// Flushes the buffered line unsent (UTF-8) and clears the buffer.
        /// </summary>
        /// <returns>The buffered bytes.</returns>
        public byte[] Flush()
        {
            byte[] data = Encoding.UTF8.GetBytes(line.ToString());
            line.Clear();
            return data;
        }

        /// <summary>
        /// Discards the buffered line.
        /// </summary>
        public void Clear() => line.Clear();

        private bool TryTrap(int b, out byte[]? command)
        {
            // The reference trapsig map (no AO): IP, ABORT, SUSP, EOF, BRK, AYT.
            (int Function, Commands Signal)[] traps =
            [
              (LinemodeProtocol.SlcIp, Commands.InterruptProcess),
              (LinemodeProtocol.SlcAbort, Commands.Abort),
              (LinemodeProtocol.SlcSuspend, Commands.Suspend),
              (LinemodeProtocol.SlcEof, Commands.EndOfFile),
              (LinemodeProtocol.SlcBreak, Commands.Break),
              (LinemodeProtocol.SlcAyt, Commands.AreYouThere),
            ];
            foreach (var (function, signal) in traps)
            {
                if (b == SlcValue(function))
                {
                    command = [(byte)Commands.InterpretAsCommand, (byte)signal];
                    return true;
                }
            }

            command = null;
            return false;
        }

        private int? SlcValue(int function) =>
          slcValues.TryGetValue(function, out int value) ? value : null;
    }
}
