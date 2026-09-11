namespace telnet_cs.Protocol
{
    using System;
    using System.Collections.Generic;
    using System.Text;
    using System.Text.Json;

    /// <summary>
    /// Framing-level encode/decode helpers for the MUD options (MSDP 69,
    /// MSSP 70, GMCP 201, ZMP 93, ATCP 200, Aardwolf 102). Payloads exclude the
    /// option byte and IAC SB/SE framing; IAC escaping is applied by the
    /// transport framing layer, not here.
    /// </summary>
    public static class MudProtocol
    {
        /// <summary>MSDP value follows a variable name (1).</summary>
        public const byte MsdpVar = 1;

        /// <summary>MSDP/MSSP value follows (2).</summary>
        public const byte MsdpVal = 2;

        /// <summary>MSDP table open (3).</summary>
        public const byte MsdpTableOpen = 3;

        /// <summary>MSDP table close (4).</summary>
        public const byte MsdpTableClose = 4;

        /// <summary>MSDP array open (5).</summary>
        public const byte MsdpArrayOpen = 5;

        /// <summary>MSDP array close (6).</summary>
        public const byte MsdpArrayClose = 6;

        /// <summary>MSSP variable follows (1).</summary>
        public const byte MsspVar = 1;

        /// <summary>MSSP value follows (2).</summary>
        public const byte MsspVal = 2;

        private static readonly Encoding Latin1 = Encoding.Latin1;

        /// <summary>
        /// Encodes a GMCP message: <c>package [SP JSON]</c> in UTF-8.
        /// </summary>
        /// <param name="package">The dotted package name.</param>
        /// <param name="json">The optional JSON body (already serialized, or null).</param>
        public static byte[] GmcpEncode(string package, string? json = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(package);
            return json is null
                ? Encoding.UTF8.GetBytes(package)
                : Encoding.UTF8.GetBytes(package + " " + json);
        }

        /// <summary>
        /// Decodes a GMCP message into its package and raw JSON body (null when absent).
        /// </summary>
        /// <param name="payload">The received payload bytes.</param>
        public static (string Package, string? Json) GmcpDecode(ReadOnlySpan<byte> payload)
        {
            var text = Encoding.UTF8.GetString(payload);
            var space = text.IndexOf(' ');
            return space < 0 ? (text, null) : (text[..space], text[(space + 1)..]);
        }

        /// <summary>
        /// Serializes <paramref name="value"/> to compact JSON for GMCP bodies.
        /// </summary>
        /// <param name="value">The value to serialize.</param>
        public static string GmcpJson(object? value)
        {
            return JsonSerializer.Serialize(value);
        }

        /// <summary>
        /// Encodes an MSDP table: flat <c>VAR name VAL value</c> pairs (string values only).
        /// </summary>
        /// <param name="values">The variable assignments.</param>
        public static byte[] MsdpEncode(IReadOnlyDictionary<string, string> values)
        {
            ArgumentNullException.ThrowIfNull(values);
            var outBytes = new List<byte>();
            foreach (var (key, value) in values)
            {
                outBytes.Add(MsdpVar);
                outBytes.AddRange(Latin1.GetBytes(key));
                outBytes.Add(MsdpVal);
                outBytes.AddRange(Latin1.GetBytes(value));
            }

            return [.. outBytes];
        }

        /// <summary>
        /// Decodes a flat MSDP payload into variable assignments. Nested
        /// table/array markers are passed through as raw bytes in values.
        /// </summary>
        /// <param name="payload">The received payload bytes.</param>
        public static IReadOnlyDictionary<string, string> MsdpDecode(ReadOnlySpan<byte> payload)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            string? name = null;
            var token = new List<byte>();
            var readingValue = false;
            foreach (var b in payload)
            {
                if (b == MsdpVar)
                {
                    if (name is not null && readingValue)
                    {
                        result[name] = Latin1.GetString([.. token]);
                    }

                    name = null;
                    token.Clear();
                    readingValue = false;
                }
                else if (b == MsdpVal)
                {
                    name = Latin1.GetString([.. token]);
                    token.Clear();
                    readingValue = true;
                }
                else
                {
                    token.Add(b);
                }
            }

            if (name is not null && readingValue)
            {
                result[name] = Latin1.GetString([.. token]);
            }

            return result;
        }

        /// <summary>
        /// Encodes MSSP variables: <c>VAR name (VAL value)*</c>; list values
        /// repeat VAL for multi-valued variables.
        /// </summary>
        /// <param name="values">The variables (single string or string list per name).</param>
        public static byte[] MsspEncode(IReadOnlyDictionary<string, IReadOnlyList<string>> values)
        {
            ArgumentNullException.ThrowIfNull(values);
            var outBytes = new List<byte>();
            foreach (var (key, items) in values)
            {
                outBytes.Add(MsspVar);
                outBytes.AddRange(Latin1.GetBytes(key));
                foreach (var item in items)
                {
                    outBytes.Add(MsspVal);
                    outBytes.AddRange(Latin1.GetBytes(item));
                }
            }

            return [.. outBytes];
        }

        /// <summary>
        /// Decodes an MSSP payload: single VAL stays a one-item list; repeated
        /// VALs accumulate in arrival order.
        /// </summary>
        /// <param name="payload">The received payload bytes.</param>
        public static IReadOnlyDictionary<string, IReadOnlyList<string>> MsspDecode(ReadOnlySpan<byte> payload)
        {
            var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            string? current = null;
            List<string>? items = null;
            var token = new List<byte>();
            foreach (var b in payload)
            {
                if (b == MsspVar)
                {
                    if (current == string.Empty)
                    {
                        // Name arrived with no VAL: valueless variable.
                        result[Latin1.GetString([.. token])] = [];
                    }
                    else if (current is not null)
                    {
                        if (token.Count > 0)
                        {
                            (items ??= []).Add(Latin1.GetString([.. token]));
                        }

                        result[current] = items ?? [];
                    }

                    current = string.Empty;
                    items = null;
                    token.Clear();
                }
                else if (b == MsspVal)
                {
                    if (current == string.Empty)
                    {
                        current = Latin1.GetString([.. token]);
                        token.Clear();
                        items = [];
                    }
                    else if (current is not null)
                    {
                        (items ??= []).Add(Latin1.GetString([.. token]));
                        token.Clear();
                    }
                    else
                    {
                        token.Add(b);
                    }
                }
                else
                {
                    token.Add(b);
                }
            }

            if (current == string.Empty)
            {
                result[Latin1.GetString([.. token])] = [];
            }
            else if (current is not null)
            {
                if (token.Count > 0)
                {
                    (items ??= []).Add(Latin1.GetString([.. token]));
                }

                result[current] = items ?? [];
            }

            return result;
        }

        /// <summary>
        /// Encodes a ZMP command: NUL-joined arguments with a trailing NUL.
        /// </summary>
        /// <param name="parts">The command followed by its arguments.</param>
        public static byte[] ZmpEncode(params string[] parts)
        {
            ArgumentNullException.ThrowIfNull(parts);
            var outBytes = new List<byte>();
            foreach (var part in parts)
            {
                outBytes.AddRange(Latin1.GetBytes(part));
                outBytes.Add(0);
            }

            return [.. outBytes];
        }

        /// <summary>
        /// Decodes a ZMP command, dropping the trailing empty segment.
        /// </summary>
        /// <param name="payload">The received payload bytes.</param>
        public static IReadOnlyList<string> ZmpDecode(ReadOnlySpan<byte> payload)
        {
            var parts = new List<string>();
            var token = new List<byte>();
            foreach (var b in payload)
            {
                if (b == 0)
                {
                    parts.Add(Latin1.GetString([.. token]));
                    token.Clear();
                }
                else
                {
                    token.Add(b);
                }
            }

            if (token.Count > 0)
            {
                parts.Add(Latin1.GetString([.. token]));
            }

            if (parts.Count > 0 && parts[^1].Length == 0)
            {
                parts.RemoveAt(parts.Count - 1);
            }

            return parts;
        }

        /// <summary>
        /// Decodes an ATCP message: split on the first space; no space means an
        /// empty value.
        /// </summary>
        /// <param name="payload">The received payload bytes.</param>
        public static (string Package, string Value) AtcpDecode(ReadOnlySpan<byte> payload)
        {
            var text = Latin1.GetString(payload);
            var space = text.IndexOf(' ');
            return space < 0 ? (text, string.Empty) : (text[..space], text[(space + 1)..]);
        }

        /// <summary>
        /// Decodes an Aardwolf message into its channel byte and data bytes.
        /// </summary>
        /// <param name="payload">The received payload bytes (channel first).</param>
        public static (byte Channel, byte[] Data) AardwolfDecode(ReadOnlySpan<byte> payload)
        {
            if (payload.IsEmpty)
            {
                throw new ArgumentException("Aardwolf payload must carry a channel byte.", nameof(payload));
            }

            return (payload[0], payload[1..].ToArray());
        }
    }
}
