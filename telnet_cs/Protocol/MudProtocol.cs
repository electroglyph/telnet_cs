namespace telnet_cs.Protocol
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Text;
    using System.Text.Json;
    using System.Text.Json.Nodes;

    /// <summary>
    /// Framing-level encode/decode helpers for the MUD options (MSDP 69,
    /// MSSP 70, GMCP 201, ZMP 93, ATCP 200, Aardwolf 102). Payloads exclude the
    /// option byte and IAC SB/SE framing; IAC escaping is applied by the
    /// transport framing layer, not here. Shapes mirror telnetlib3's
    /// <c>mud.py</c> ground truth.
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

        private static readonly Encoding StrictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        private static readonly IReadOnlyDictionary<byte, string> AardwolfChannels = new Dictionary<byte, string>
        {
            [100] = "status",
            [101] = "tick",
            [102] = "affect",
            [103] = "group",
            [104] = "skill",
            [105] = "quest",
            [106] = "spell",
            [107] = "stat",
            [108] = "message",
        };

        /// <summary>
        /// Decodes with <paramref name="encoding"/> (UTF-8 by default),
        /// falling back to Latin-1 when the primary decoding fails.
        /// </summary>
        /// <param name="buf">The raw bytes.</param>
        /// <param name="encoding">The primary encoding, or null for UTF-8.</param>
        private static string DecodeBestEffort(ReadOnlySpan<byte> buf, Encoding? encoding)
        {
            var primary = encoding ?? StrictUtf8;
            try
            {
                return primary.GetString(buf);
            }
            catch (DecoderFallbackException)
            {
                return Latin1.GetString(buf);
            }
        }

        /// <summary>
        /// Encodes a GMCP message carrying a package name only (no data body).
        /// </summary>
        /// <param name="package">The dotted package name.</param>
        public static byte[] GmcpEncode(string package)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(package);
            return Encoding.UTF8.GetBytes(package);
        }

        /// <summary>
        /// Encodes a GMCP message: <c>package [SP JSON]</c> in UTF-8, joining a
        /// pre-serialized JSON body with a single space.
        /// </summary>
        /// <param name="package">The dotted package name.</param>
        /// <param name="json">The optional JSON body (already serialized, or null).</param>
        public static byte[] GmcpEncode(string package, string? json)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(package);
            return json is null
                ? Encoding.UTF8.GetBytes(package)
                : Encoding.UTF8.GetBytes(package + " " + json);
        }

        /// <summary>
        /// Encodes a GMCP message, serializing <paramref name="data"/> to
        /// compact JSON (<c>(",", ":")</c> separators, no spaces).
        /// </summary>
        /// <param name="package">The dotted package name.</param>
        /// <param name="data">The optional body (dict, list, or primitive), or null for package-only.</param>
        public static byte[] GmcpEncodeData(string package, object? data)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(package);
            return data is null
                ? Encoding.UTF8.GetBytes(package)
                : Encoding.UTF8.GetBytes(package + " " + GmcpJson(data));
        }

        /// <summary>
        /// Decodes a GMCP message into its package and parsed JSON body (null
        /// when absent or blank). Malformed JSON throws
        /// <see cref="ArgumentException"/> (the ValueError equivalent).
        /// </summary>
        /// <param name="payload">The received payload bytes.</param>
        /// <param name="encoding">The primary text encoding, or null for UTF-8 with Latin-1 fallback.</param>
        public static (string Package, JsonNode? Data) GmcpDecode(ReadOnlySpan<byte> payload, Encoding? encoding = null)
        {
            var space = payload.IndexOf((byte)' ');
            if (space < 0)
            {
                return (DecodeBestEffort(payload, encoding), null);
            }

            var package = DecodeBestEffort(payload[..space], encoding);
            var text = DecodeBestEffort(payload[(space + 1)..], encoding);
            if (string.IsNullOrWhiteSpace(text))
            {
                return (package, null);
            }

            try
            {
                return (package, JsonNode.Parse(text));
            }
            catch (JsonException ex)
            {
                throw new ArgumentException("Invalid JSON in GMCP payload: " + ex.Message, nameof(payload), ex);
            }
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
        /// Encodes MSDP variables: dictionaries become <c>TABLE</c> values,
        /// enumerables become <c>ARRAY</c> values, null becomes the string
        /// <c>None</c> (the reference stringifies values), anything else
        /// becomes its string form; names and strings are UTF-8.
        /// </summary>
        /// <param name="values">The variable assignments.</param>
        public static byte[] MsdpEncode(IReadOnlyDictionary<string, object?> values)
        {
            ArgumentNullException.ThrowIfNull(values);
            var outBytes = new List<byte>();
            foreach (var (key, value) in values)
            {
                outBytes.Add(MsdpVar);
                outBytes.AddRange(Encoding.UTF8.GetBytes(key));
                outBytes.Add(MsdpVal);
                EncodeMsdpValue(outBytes, value);
            }

            return [.. outBytes];
        }

        private static void EncodeMsdpValue(List<byte> outBytes, object? value)
        {
            switch (value)
            {
                case null:
                    outBytes.AddRange(Encoding.UTF8.GetBytes("None"));
                    break;
                case string text:
                    outBytes.AddRange(Encoding.UTF8.GetBytes(text));
                    break;
                case IReadOnlyDictionary<string, object?> table:
                    outBytes.Add(MsdpTableOpen);
                    foreach (var (key, item) in table)
                    {
                        outBytes.Add(MsdpVar);
                        outBytes.AddRange(Encoding.UTF8.GetBytes(key));
                        outBytes.Add(MsdpVal);
                        EncodeMsdpValue(outBytes, item);
                    }

                    outBytes.Add(MsdpTableClose);
                    break;
                case IDictionary<string, object?> mutableTable:
                    outBytes.Add(MsdpTableOpen);
                    foreach (var (key, item) in mutableTable)
                    {
                        outBytes.Add(MsdpVar);
                        outBytes.AddRange(Encoding.UTF8.GetBytes(key));
                        outBytes.Add(MsdpVal);
                        EncodeMsdpValue(outBytes, item);
                    }

                    outBytes.Add(MsdpTableClose);
                    break;
                case IDictionary genericTable:
                    outBytes.Add(MsdpTableOpen);
                    foreach (DictionaryEntry entry in genericTable)
                    {
                        outBytes.Add(MsdpVar);
                        outBytes.AddRange(Encoding.UTF8.GetBytes(Convert.ToString(entry.Key, null) ?? string.Empty));
                        outBytes.Add(MsdpVal);
                        EncodeMsdpValue(outBytes, entry.Value);
                    }

                    outBytes.Add(MsdpTableClose);
                    break;
                case IEnumerable array:
                    outBytes.Add(MsdpArrayOpen);
                    foreach (var item in array)
                    {
                        outBytes.Add(MsdpVal);
                        EncodeMsdpValue(outBytes, item);
                    }

                    outBytes.Add(MsdpArrayClose);
                    break;
                default:
                    outBytes.AddRange(Encoding.UTF8.GetBytes(Convert.ToString(value, null) ?? string.Empty));
                    break;
            }
        }

        /// <summary>
        /// Decodes an MSDP payload into variable assignments. Scalar values are
        /// strings; nested <c>TABLE</c>/<c>ARRAY</c> markers decode recursively
        /// to dictionaries/lists. Bytes outside a <c>VAR</c> item are skipped
        /// as garbage; a name without a following <c>VAL</c> is dropped.
        /// </summary>
        /// <param name="payload">The received payload bytes.</param>
        /// <param name="encoding">The primary text encoding, or null for UTF-8 with Latin-1 fallback.</param>
        public static IReadOnlyDictionary<string, object?> MsdpDecode(ReadOnlySpan<byte> payload, Encoding? encoding = null)
        {
            var parser = new MsdpParser(payload, encoding);
            return parser.Parse();
        }

        private sealed class MsdpParser(ReadOnlySpan<byte> buf, Encoding? encoding)
        {
            private readonly byte[] buf = buf.ToArray();
            private int idx;

            public Dictionary<string, object?> Parse()
            {
                var result = new Dictionary<string, object?>(StringComparer.Ordinal);
                while (this.idx < this.buf.Length)
                {
                    if (this.buf[this.idx] == MsdpVar)
                    {
                        this.idx++;
                        var key = this.ReadKey();
                        if (this.idx < this.buf.Length && this.buf[this.idx] == MsdpVal)
                        {
                            this.idx++;
                            result[key] = this.ParseValue();
                        }
                    }
                    else
                    {
                        this.idx++;
                    }
                }

                return result;
            }

            public object? ParseValue()
            {
                if (this.idx >= this.buf.Length)
                {
                    return string.Empty;
                }

                var marker = this.buf[this.idx++];
                if (marker == MsdpTableOpen)
                {
                    return this.ParseTable();
                }

                if (marker == MsdpArrayOpen)
                {
                    return this.ParseArray();
                }

                this.idx--;
                return this.ReadString();
            }

            private Dictionary<string, object?> ParseTable()
            {
                var table = new Dictionary<string, object?>(StringComparer.Ordinal);
                while (this.idx < this.buf.Length && this.buf[this.idx] != MsdpTableClose)
                {
                    if (this.buf[this.idx] == MsdpVar)
                    {
                        this.idx++;
                        var key = this.ReadKey();
                        if (this.idx < this.buf.Length && this.buf[this.idx] == MsdpVal)
                        {
                            this.idx++;
                        }

                        table[key] = this.ParseValue();
                    }
                    else
                    {
                        // Skip malformed bytes so a network parser terminates
                        // instead of looping on input the table grammar rejects.
                        this.idx++;
                    }
                }

                if (this.idx < this.buf.Length)
                {
                    this.idx++;
                }

                return table;
            }

            private List<object?> ParseArray()
            {
                var array = new List<object?>();
                while (this.idx < this.buf.Length && this.buf[this.idx] != MsdpArrayClose)
                {
                    if (this.buf[this.idx] == MsdpVal)
                    {
                        this.idx++;
                    }

                    array.Add(this.ParseValue());
                }

                if (this.idx < this.buf.Length)
                {
                    this.idx++;
                }

                return array;
            }

            private string ReadString()
            {
                var start = this.idx;
                while (this.idx < this.buf.Length && this.buf[this.idx] is not (MsdpVar or MsdpVal or MsdpTableClose or MsdpArrayClose))
                {
                    this.idx++;
                }

                return DecodeBestEffort(this.buf.AsSpan(start, this.idx - start), encoding);
            }

            private string ReadKey()
            {
                var start = this.idx;
                while (this.idx < this.buf.Length && this.buf[this.idx] is not (MsdpVar or MsdpVal))
                {
                    this.idx++;
                }

                return DecodeBestEffort(this.buf.AsSpan(start, this.idx - start), encoding);
            }
        }

        /// <summary>
        /// Encodes MSSP variables: <c>VAR name (VAL value)*</c>. Each value is
        /// a string (single <c>VAL</c>) or a string enumerable (repeated
        /// <c>VAL</c>s); names and values are UTF-8.
        /// </summary>
        /// <param name="values">The variables.</param>
        public static byte[] MsspEncode(IReadOnlyDictionary<string, object> values)
        {
            ArgumentNullException.ThrowIfNull(values);
            var outBytes = new List<byte>();
            foreach (var (key, value) in values)
            {
                outBytes.Add(MsspVar);
                outBytes.AddRange(Encoding.UTF8.GetBytes(key));
                switch (value)
                {
                    case string text:
                        outBytes.Add(MsspVal);
                        outBytes.AddRange(Encoding.UTF8.GetBytes(text));
                        break;
                    case IEnumerable<string> items:
                        foreach (var item in items)
                        {
                            outBytes.Add(MsspVal);
                            outBytes.AddRange(Encoding.UTF8.GetBytes(item));
                        }

                        break;
                    default:
                        throw new ArgumentException($"MSSP value for '{key}' must be a string or a string enumerable.", nameof(values));
                }
            }

            return [.. outBytes];
        }

        /// <summary>
        /// Decodes an MSSP payload: a single <c>VAL</c> stays a string,
        /// repeated <c>VAL</c>s (or a repeated <c>VAR</c> name) merge into a
        /// string list; bytes outside an item are skipped as garbage and a
        /// name without a <c>VAL</c> is dropped.
        /// </summary>
        /// <param name="payload">The received payload bytes.</param>
        /// <param name="encoding">The primary text encoding, or null for UTF-8 with Latin-1 fallback.</param>
        public static IReadOnlyDictionary<string, object> MsspDecode(ReadOnlySpan<byte> payload, Encoding? encoding = null)
        {
            var buf = payload.ToArray();
            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            string? current = null;
            var idx = 0;
            while (idx < buf.Length)
            {
                if (buf[idx] == MsspVar)
                {
                    idx++;
                    var start = idx;
                    while (idx < buf.Length && buf[idx] is not (MsspVal or MsspVar))
                    {
                        idx++;
                    }

                    current = DecodeBestEffort(buf.AsSpan(start, idx - start), encoding);
                }
                else if (buf[idx] == MsspVal)
                {
                    idx++;
                    var start = idx;
                    while (idx < buf.Length && buf[idx] is not (MsspVal or MsspVar))
                    {
                        idx++;
                    }

                    var value = DecodeBestEffort(buf.AsSpan(start, idx - start), encoding);
                    if (current is not null)
                    {
                        if (result.TryGetValue(current, out var existing))
                        {
                            if (existing is List<string> list)
                            {
                                list.Add(value);
                            }
                            else
                            {
                                result[current] = new List<string> { (string)existing, value };
                            }
                        }
                        else
                        {
                            result[current] = value;
                        }
                    }
                }
                else
                {
                    idx++;
                }
            }

            return result;
        }

        /// <summary>
        /// Encodes a ZMP command: NUL-joined parts with a trailing NUL, in UTF-8.
        /// </summary>
        /// <param name="parts">The command followed by its arguments.</param>
        public static byte[] ZmpEncode(params string[] parts)
        {
            ArgumentNullException.ThrowIfNull(parts);
            var outBytes = new List<byte>();
            foreach (var part in parts)
            {
                outBytes.AddRange(Encoding.UTF8.GetBytes(part));
                outBytes.Add(0);
            }

            return [.. outBytes];
        }

        /// <summary>
        /// Decodes a ZMP command to <c>[command, arg1, arg2, ...]</c>, dropping
        /// the trailing empty segment; empty input decodes to an empty list.
        /// </summary>
        /// <param name="payload">The received payload bytes.</param>
        /// <param name="encoding">The primary text encoding, or null for UTF-8 with Latin-1 fallback.</param>
        public static IReadOnlyList<string> ZmpDecode(ReadOnlySpan<byte> payload, Encoding? encoding = null)
        {
            if (payload.IsEmpty)
            {
                return [];
            }

            var parts = new List<string>();
            var token = new List<byte>();
            foreach (var b in payload)
            {
                if (b == 0)
                {
                    parts.Add(DecodeBestEffort([.. token], encoding));
                    token.Clear();
                }
                else
                {
                    token.Add(b);
                }
            }

            parts.Add(DecodeBestEffort([.. token], encoding));
            if (parts.Count > 0 && parts[^1].Length == 0)
            {
                parts.RemoveAt(parts.Count - 1);
            }

            return parts;
        }

        /// <summary>
        /// Decodes an ATCP message: split on the first space; no space means an
        /// empty value, and empty input means an empty package.
        /// </summary>
        /// <param name="payload">The received payload bytes.</param>
        /// <param name="encoding">The primary text encoding, or null for UTF-8 with Latin-1 fallback.</param>
        public static (string Package, string Value) AtcpDecode(ReadOnlySpan<byte> payload, Encoding? encoding = null)
        {
            var space = payload.IndexOf((byte)' ');
            return space < 0
                ? (DecodeBestEffort(payload, encoding), string.Empty)
                : (DecodeBestEffort(payload[..space], encoding), DecodeBestEffort(payload[(space + 1)..], encoding));
        }

        /// <summary>
        /// Decodes an Aardwolf message: the first byte names the channel
        /// (100–108 mapped, anything else as <c>0x..</c>), a two-byte payload
        /// additionally carries <c>DataByte</c>, and any trailing bytes carry
        /// <c>DataBytes</c>. Empty input decodes to the <c>unknown</c> channel.
        /// </summary>
        /// <param name="payload">The received payload bytes (channel first).</param>
        public static AardwolfMessage AardwolfDecode(ReadOnlySpan<byte> payload)
        {
            if (payload.IsEmpty)
            {
                return new AardwolfMessage("unknown", 0, null, []);
            }

            var channelByte = payload[0];
            var channel = AardwolfChannels.TryGetValue(channelByte, out var name) ? name : $"0x{channelByte:x2}";
            return payload.Length switch
            {
                1 => new AardwolfMessage(channel, channelByte, null, []),
                2 => new AardwolfMessage(channel, channelByte, payload[1], [payload[1]]),
                _ => new AardwolfMessage(channel, channelByte, null, payload[1..].ToArray()),
            };
        }
    }
}
