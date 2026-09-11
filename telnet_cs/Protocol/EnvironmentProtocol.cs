namespace telnet_cs.Protocol
{
    /// <summary>
    /// RFC 1408 ENVIRON (option 36) subnegotiation payload construction.
    /// All names and values use Latin-1 (NVT ASCII-compatible) encoding.
    /// </summary>
    internal static class EnvironmentProtocol
    {
        internal const byte Is = 0;
        internal const byte Send = 1;
        internal const byte Info = 2;

        internal const byte Var = 0;
        internal const byte Value = 1;
        internal const byte Esc = 2;
        internal const byte UserVar = 3;

        private static readonly byte[] UserName = [(byte)'U', (byte)'S', (byte)'E', (byte)'R'];
        private static readonly byte[] DisplayName = [(byte)'D', (byte)'I', (byte)'S', (byte)'P', (byte)'L', (byte)'A', (byte)'Y'];

        /// <summary>
        /// Builds a verb-first ENVIRON payload: the verb byte followed by
        /// type/name/VALUE sequences. Requested types are mirrored in order;
        /// an empty request selects the defaults (well-known variables, then
        /// user variables). Entries with no configured value are omitted; when
        /// nothing is configured the payload is the bare verb (valid empty IS).
        /// </summary>
        /// <param name="verb">The subnegotiation verb (<see cref="Is"/> or <see cref="Info"/>).</param>
        /// <param name="requestedTypes">The type bytes from the SEND request, verb excluded.</param>
        /// <param name="user">Value for the well-known <c>USER</c> variable, or null to omit.</param>
        /// <param name="display">Value for the well-known <c>DISPLAY</c> variable, or null to omit.</param>
        /// <param name="userVars">User-defined <c>USERVAR</c> entries.</param>
        internal static byte[] BuildResponse(
          byte verb,
          IEnumerable<byte> requestedTypes,
          string? user,
          string? display,
          IReadOnlyDictionary<string, string>? userVars)
        {
            var entries = new List<(byte Type, byte[] Name, byte[] Value)>();
            var seenAny = false;
            var seenTypes = new HashSet<byte>();
            foreach (var type in requestedTypes)
            {
                // A repeated type would emit the same block twice: answer each
                // requested type once, first-seen order preserved.
                if (!seenTypes.Add(type))
                {
                    continue;
                }

                seenAny = true;
                if (type == Var)
                {
                    AddWellKnown(entries, user, display);
                }
                else if (type == UserVar && userVars is not null)
                {
                    foreach (var pair in userVars)
                    {
                        entries.Add((UserVar, Escape(Encode(pair.Key)), Escape(Encode(pair.Value))));
                    }
                }
            }

            if (!seenAny)
            {
                AddWellKnown(entries, user, display);
                if (userVars is not null)
                {
                    foreach (var pair in userVars)
                    {
                        entries.Add((UserVar, Escape(Encode(pair.Key)), Escape(Encode(pair.Value))));
                    }
                }
            }

            var payload = new List<byte>(entries.Count * 8 + 1) { verb };
            foreach (var (type, name, value) in entries)
            {
                payload.Add(type);
                payload.AddRange(name);
                payload.Add(Value);
                payload.AddRange(value);
            }

            return [.. payload];
        }

        /// <summary>
        /// Frames a verb-first payload as <c>IAC SB option ... IAC SE</c>,
        /// doubling embedded IAC bytes (RFC 854).
        /// </summary>
        /// <param name="option">The option number.</param>
        /// <param name="verbFirstPayload">Payload bytes starting with the subnegotiation verb.</param>
        internal static byte[] FrameSubnegotiation(int option, byte[] verbFirstPayload)
        {
            return Frame(option, verbFirstPayload, escapeByte: (byte)Commands.InterpretAsCommand, trailingIac: true);
        }

        /// <summary>
        /// Frames a verb-first payload as <c>IAC SB option ... SE</c> with a
        /// bare SE terminator, doubling embedded SE bytes instead of IAC
        /// (RFC 859 STATUS).
        /// </summary>
        /// <param name="option">The option number.</param>
        /// <param name="verbFirstPayload">Payload bytes starting with the subnegotiation verb.</param>
        internal static byte[] FrameBareSe(int option, byte[] verbFirstPayload)
        {
            return Frame(option, verbFirstPayload, escapeByte: (byte)Commands.SubnegotiationEnd, trailingIac: false);
        }

        private static byte[] Frame(int option, byte[] verbFirstPayload, byte escapeByte, bool trailingIac)
        {
            var frame = new List<byte>(verbFirstPayload.Length + (trailingIac ? 5 : 4))
      {
        (byte)Commands.InterpretAsCommand,
        (byte)Commands.Subnegotiation,
        (byte)option,
      };
            foreach (var b in verbFirstPayload)
            {
                frame.Add(b);
                if (b == escapeByte)
                {
                    frame.Add(b);
                }
            }

            if (trailingIac)
            {
                frame.Add((byte)Commands.InterpretAsCommand);
            }

            frame.Add((byte)Commands.SubnegotiationEnd);
            return [.. frame];
        }

        /// <summary>
        /// Parses an IS or INFO payload (verb first) into entries. Names and
        /// values are Latin-1 decoded with ESC-escapes resolved. A type byte
        /// without VALUE is an undefined variable (null value); VALUE
        /// immediately followed by a type byte or the end is defined-but-empty.
        /// Entries with empty names are skipped (no usable key).
        /// </summary>
        /// <param name="payload">The received payload including the verb byte.</param>
        internal static List<(bool IsUserVar, string Name, string? Value)> ParseEntries(IReadOnlyList<byte> payload)
        {
            var entries = new List<(bool IsUserVar, string Name, string? Value)>();
            var index = 1; // Skip the verb.
            while (index < payload.Count)
            {
                var type = payload[index];
                if (type != Var && type != UserVar)
                {
                    index++;
                    continue;
                }

                index++;
                string name = ReadRun(payload, ref index);
                string? value = null;
                if (index < payload.Count && payload[index] == Value)
                {
                    index++;
                    value = ReadRun(payload, ref index);
                }

                if (name.Length > 0)
                {
                    entries.Add((type == UserVar, name, value));
                }
            }

            return entries;
        }

        private static string ReadRun(IReadOnlyList<byte> payload, ref int index)
        {
            var raw = new List<byte>();
            while (index < payload.Count)
            {
                var b = payload[index];
                if (b == Esc)
                {
                    // A trailing ESC is a truncated escape: it contributes no byte.
                    index++;
                    if (index < payload.Count)
                    {
                        raw.Add(payload[index]);
                        index++;
                    }

                    continue;
                }

                if (b is Var or Value or UserVar)
                {
                    break;
                }

                raw.Add(b);
                index++;
            }

            return System.Text.Encoding.Latin1.GetString([.. raw]);
        }

        private static void AddWellKnown(List<(byte Type, byte[] Name, byte[] Value)> entries, string? user, string? display)
        {
            if (user is not null)
            {
                entries.Add((Var, (byte[])UserName.Clone(), Escape(Encode(user))));
            }

            if (display is not null)
            {
                entries.Add((Var, (byte[])DisplayName.Clone(), Escape(Encode(display))));
            }
        }

        private static byte[] Encode(string text) => System.Text.Encoding.Latin1.GetBytes(text);

        /// <summary>
        /// ESC-escapes embedded VAR/VALUE/ESC/USERVAR bytes (RFC 1408 §4.3).
        /// </summary>
        private static byte[] Escape(byte[] raw)
        {
            if (raw.All(static b => b is not (Var or Value or Esc or UserVar)))
            {
                return raw;
            }

            var escaped = new List<byte>(raw.Length + 2);
            foreach (var b in raw)
            {
                if (b is Var or Value or Esc or UserVar)
                {
                    escaped.Add(Esc);
                }

                escaped.Add(b);
            }

            return [.. escaped];
        }
    }
}
