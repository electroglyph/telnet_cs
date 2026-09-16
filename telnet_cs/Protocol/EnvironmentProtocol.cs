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

        /// <summary>
        /// Well-known <c>DISPLAY</c> variable name: single source of truth for
        /// the name bytes below and the server's effective-display recency rule.
        /// </summary>
        internal const string DisplayVariableName = "DISPLAY";
        private static readonly byte[] DisplayName = Encode(DisplayVariableName);

        /// <summary>
        /// Builds a verb-first ENVIRON payload: the verb byte followed by
        /// type/name/VALUE sequences. Named requests are answered
        /// requested-only in order (RFC 1572: only requested variables,
        /// same order, one response per explicitly requested
        /// <c>type ...</c>); undefined variables are echoed as a bare
        /// <c>type name</c> with no <c>VALUE</c>. Bare <c>VAR</c>/<c>USERVAR</c>
        /// markers (and an empty request) volunteer the defaults: the
        /// well-known variables (in order) then user variables. Entries with
        /// no configured value are omitted from volunteering; when nothing
        /// is configured the payload is the bare verb (valid empty IS).
        /// A bare <c>VAR</c> (or an empty request) also volunteers the session
        /// parameters <c>TERM</c>, <c>LANG</c>, <c>COLUMNS</c>, <c>LINES</c>
        /// and <c>COLORTERM</c>, matching telnetlib3's auto-sent
        /// <c>send_env</c> set; a bare <c>USERVAR</c> answers user variables
        /// alone. <c>VAR</c> and <c>USERVAR</c> stay distinct types.
        /// </summary>
        /// <param name="verb">The subnegotiation verb (<see cref="Is"/> or <see cref="Info"/>).</param>
        /// <param name="requestedTypes">The SEND type/name sequence, verb excluded.</param>
        /// <param name="user">Value for the well-known <c>USER</c> variable, or null to omit.</param>
        /// <param name="display">Value for the well-known <c>DISPLAY</c> variable, or null to omit.</param>
        /// <param name="userVars">User-defined <c>USERVAR</c> entries.</param>
        /// <param name="term">Value volunteered for <c>TERM</c>, or null to omit.</param>
        /// <param name="lang">Value volunteered for <c>LANG</c>, or null to omit.</param>
        /// <param name="columns">Value volunteered for <c>COLUMNS</c>, or null to omit.</param>
        /// <param name="lines">Value volunteered for <c>LINES</c>, or null to omit.</param>
        /// <param name="colorTerm">Value volunteered for <c>COLORTERM</c>, or null to omit.</param>
        internal static byte[] BuildResponse(
          byte verb,
          IEnumerable<byte> requestedTypes,
          string? user,
          string? display,
          IReadOnlyDictionary<string, string>? userVars,
          string? term = null,
          string? lang = null,
          string? columns = null,
          string? lines = null,
          string? colorTerm = null)
        {
            // RFC 1572 section 2: SEND is a request; responses are IS and INFO only.
            if (verb is not (Is or Info))
            {
                throw new ArgumentOutOfRangeException(nameof(verb), verb, "ENVIRON response verb must be IS or INFO.");
            }

            var requests = ParseSendRequests(requestedTypes);
            var entries = new List<(byte Type, byte[] Name, byte[]? Value)>();
            if (requests.Count == 0)
            {
                AddWellKnown(entries, user, display, term, lang, columns, lines, colorTerm);
                if (userVars is not null)
                {
                    foreach (var pair in userVars)
                    {
                        entries.Add((UserVar, Escape(Encode(pair.Key)), Escape(Encode(pair.Value))));
                    }
                }
            }
            else
            {
                var seen = new HashSet<(byte Type, string Name)>();
                foreach (var (type, name) in requests)
                {
                    if (name.Length == 0)
                    {
                        if (type == Var)
                        {
                            AddWellKnownDeduped(entries, seen, user, display, term, lang, columns, lines, colorTerm);
                        }
                        else if (userVars is not null)
                        {
                            foreach (var pair in userVars)
                            {
                                if (seen.Add((UserVar, pair.Key)))
                                {
                                    entries.Add((UserVar, Escape(Encode(pair.Key)), Escape(Encode(pair.Value))));
                                }
                            }
                        }
                    }
                    else if (seen.Add((type, name)))
                    {
                        if (type == Var)
                        {
                            var value = LookupWellKnown(name, user, display, term, lang, columns, lines, colorTerm);
                            entries.Add(value is null
                              ? (Var, Escape(Encode(name)), (byte[]?)null)
                              : (Var, Escape(Encode(name)), Escape(Encode(value))));
                        }
                        else
                        {
                            entries.Add(userVars is not null && userVars.TryGetValue(name, out var userValue)
                              ? (UserVar, Escape(Encode(name)), (byte[]?)Escape(Encode(userValue)))
                              : (UserVar, Escape(Encode(name)), (byte[]?)null));
                        }
                    }
                }
            }

            var payload = new List<byte>(entries.Count * 8 + 1) { verb };
            foreach (var (type, name, value) in entries)
            {
                payload.Add(type);
                payload.AddRange(name);
                if (value is not null)
                {
                    payload.Add(Value);
                    payload.AddRange(value);
                }
            }

            return [.. payload];
        }

        /// <summary>
        /// Builds the default NEW_ENVIRON SEND type/name sequence: one
        /// <c>VAR "name"</c> pair per requested well-known variable, closed by
        /// bare <c>VAR</c> and <c>USERVAR</c> markers inviting the client to
        /// volunteer anything else. Mirrors telnetlib3's
        /// <c>on_request_environ</c> default list. <c>USER</c> is excluded when
        /// the TTYPE cycle identified Microsoft telnet (exactly
        /// <c>ANSI</c>/<c>VT100</c>, case-sensitive): requesting it crashes
        /// <c>telnet.exe</c>.
        /// </summary>
        /// <param name="ttype1">First TTYPE answer, or null when unknown.</param>
        /// <param name="ttype2">Second TTYPE answer, or null when unknown.</param>
        internal static byte[] BuildDefaultSendRequest(string? ttype1, string? ttype2)
        {
            var names = new List<string>();
            if (ttype1 != "ANSI" || ttype2 != "VT100")
            {
                names.Add("USER");
            }

            names.AddRange(["LOGNAME", "DISPLAY", "LANG", "TERM", "TERM_PROGRAM", "COLUMNS", "LINES", "COLORTERM", "EDITOR", "IPADDRESS"]);

            var request = new List<byte>();
            foreach (var name in names)
            {
                request.Add(Var);
                request.AddRange(Encode(name));
            }

            request.Add(Var);
            request.Add(UserVar);
            return [.. request];
        }

        /// <summary>
        /// Reports whether a received environment map presumes BINARY
        /// capability even without explicit BINARY negotiation: a
        /// <c>CHARSET</c> entry, or a <c>LANG</c> entry carrying an encoding
        /// suffix (a dot, and anything but <c>C</c>). Mirrors telnetlib3's
        /// <c>on_environ</c> force-binary rule. Keys must already be
        /// upper-cased; empty values must already be dropped.
        /// </summary>
        internal static bool ShouldForceBinary(IReadOnlyDictionary<string, string> environ)
        {
            if (environ.ContainsKey("CHARSET"))
            {
                return true;
            }

            return environ.TryGetValue("LANG", out var lang) && lang.Contains('.') && lang != "C";
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
        /// <param name="maxEntries">Maximum entries to parse (CPU bound; extra input is left unparsed).</param>
        internal static List<(bool IsUserVar, string Name, string? Value)> ParseEntries(IReadOnlyList<byte> payload, int maxEntries = int.MaxValue)
        {
            var entries = new List<(bool IsUserVar, string Name, string? Value)>();
            var index = 1; // Skip the verb.
            while (index < payload.Count)
            {
                if (entries.Count >= maxEntries)
                {
                    break;
                }

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

        private static void AddWellKnown(
          List<(byte Type, byte[] Name, byte[]? Value)> entries,
          string? user,
          string? display,
          string? term,
          string? lang,
          string? columns,
          string? lines,
          string? colorTerm = null)
        {
            if (user is not null)
            {
                entries.Add((Var, (byte[])UserName.Clone(), Escape(Encode(user))));
            }

            if (display is not null)
            {
                entries.Add((Var, (byte[])DisplayName.Clone(), Escape(Encode(display))));
            }

            if (term is not null)
            {
                entries.Add((Var, Encode("TERM"), Escape(Encode(term))));
            }

            if (lang is not null)
            {
                entries.Add((Var, Encode("LANG"), Escape(Encode(lang))));
            }

            if (columns is not null)
            {
                entries.Add((Var, Encode("COLUMNS"), Escape(Encode(columns))));
            }

            if (lines is not null)
            {
                entries.Add((Var, Encode("LINES"), Escape(Encode(lines))));
            }

            if (colorTerm is not null)
            {
                entries.Add((Var, Encode("COLORTERM"), Escape(Encode(colorTerm))));
            }
        }

        private static void AddWellKnownDeduped(
          List<(byte Type, byte[] Name, byte[]? Value)> entries,
          HashSet<(byte Type, string Name)> seen,
          string? user,
          string? display,
          string? term,
          string? lang,
          string? columns,
          string? lines,
          string? colorTerm)
        {
            if (user is not null && seen.Add((Var, "USER")))
            {
                entries.Add((Var, (byte[])UserName.Clone(), Escape(Encode(user))));
            }

            if (display is not null && seen.Add((Var, DisplayVariableName)))
            {
                entries.Add((Var, (byte[])DisplayName.Clone(), Escape(Encode(display))));
            }

            if (term is not null && seen.Add((Var, "TERM")))
            {
                entries.Add((Var, Encode("TERM"), Escape(Encode(term))));
            }

            if (lang is not null && seen.Add((Var, "LANG")))
            {
                entries.Add((Var, Encode("LANG"), Escape(Encode(lang))));
            }

            if (columns is not null && seen.Add((Var, "COLUMNS")))
            {
                entries.Add((Var, Encode("COLUMNS"), Escape(Encode(columns))));
            }

            if (lines is not null && seen.Add((Var, "LINES")))
            {
                entries.Add((Var, Encode("LINES"), Escape(Encode(lines))));
            }

            if (colorTerm is not null && seen.Add((Var, "COLORTERM")))
            {
                entries.Add((Var, Encode("COLORTERM"), Escape(Encode(colorTerm))));
            }
        }

        private static string? LookupWellKnown(
          string name,
          string? user,
          string? display,
          string? term,
          string? lang,
          string? columns,
          string? lines,
          string? colorTerm) => name switch
          {
              "USER" => user,
              "DISPLAY" => display,
              "TERM" => term,
              "LANG" => lang,
              "COLUMNS" => columns,
              "LINES" => lines,
              "COLORTERM" => colorTerm,
              _ => null,
          };

        private static List<(byte Type, string Name)> ParseSendRequests(IEnumerable<byte> requested)
        {
            var bytes = requested as byte[] ?? [.. requested];
            var list = new List<(byte Type, string Name)>();
            var index = 0;
            while (index < bytes.Length)
            {
                var type = bytes[index];
                if (type != Var && type != UserVar)
                {
                    index++;
                    continue;
                }

                index++;
                var raw = new List<byte>();
                while (index < bytes.Length)
                {
                    var b = bytes[index];
                    if (b == Esc)
                    {
                        index++;
                        if (index < bytes.Length)
                        {
                            raw.Add(bytes[index]);
                            index++;
                        }

                        continue;
                    }

                    if (b == Var || b == UserVar)
                    {
                        break;
                    }

                    if (b == Value)
                    {
                        index++;
                        while (index < bytes.Length)
                        {
                            var v = bytes[index];
                            if (v == Esc)
                            {
                                index++;
                                if (index < bytes.Length)
                                {
                                    index++;
                                }

                                continue;
                            }

                            if (v == Var || v == UserVar)
                            {
                                break;
                            }

                            index++;
                        }

                        break;
                    }

                    raw.Add(b);
                    index++;
                }

                list.Add((type, System.Text.Encoding.Latin1.GetString([.. raw])));
            }

            return list;
        }

        private static byte[] Encode(string text) => System.Text.Encoding.Latin1.GetBytes(text);

        /// <summary>
        /// ESC-escapes embedded VAR/VALUE/ESC/USERVAR bytes (RFC 1408 §2).
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
