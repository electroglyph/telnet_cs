namespace telnet_cs.Fuzz;

/// <summary>
/// One generated fuzz input: raw inbound bytes plus the chunk split points
/// used to feed them across successive reads (split delivery exercises the
/// IAC/SB resume paths that single-shot feeds never reach).
/// </summary>
internal sealed record FuzzInput(byte[] Bytes, int[] Splits);

/// <summary>
/// Structure-aware input generator. A seeded <see cref="Random"/> keeps runs
/// deterministic: the same (seed, iteration) always yields the same input.
/// Strategies mix raw biased bytes, IAC framing, MCCP streams, multibyte
/// UTF-8 shapes, corpus-seeded mutation (valid frames, concatenated,
/// crossed over, and corrupted, reach deep dispatch paths random bytes never
/// reach intact), and targeted per-option frames with valid, edge, and
/// malformed fields. Lengths skew tiny: most parser states are reachable in
/// a few bytes, and short inputs minimize faster.
/// </summary>
internal static class Mutator
{
    private const byte Iac = 255;
    private const byte Sb = 250;
    private const byte Se = 240;

    private static readonly byte[] Interesting =
    [
        255, 250, 240, 251, 252, 253, 254, 249, 248, 247, 242, 241, 239, 236,
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 13,
        24, 31, 32, 33, 35, 36, 39, 42, 44, 69, 70, 86, 87, 90, 91, 93, 102,
        104, 105, 118, 200, 201,
    ];

    private static readonly byte[] MudOptions = [69, 70, 86, 87, 90, 91, 93, 102, 200, 201];

    private static List<byte[]>? s_external;

    /// <summary>
    /// Loads previously saved novel inputs for the external-corpus strategy.
    /// Sorted load order keeps runs deterministic for a given directory.
    /// Missing directories and unreadable files are silently skipped: there
    /// is simply no external corpus to sample.
    /// </summary>
    public static void LoadExternalCorpus(string dir)
    {
        s_external = null;
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
        {
            return;
        }

        var files = Directory.GetFiles(dir, "*.bin");
        Array.Sort(files, StringComparer.Ordinal);
        var loaded = new List<byte[]>();
        foreach (var file in files.Take(500))
        {
            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(file);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            if (bytes.Length is > 0 and <= 65536)
            {
                loaded.Add(bytes);
            }
        }

        if (loaded.Count != 0)
        {
            s_external = loaded;
        }
    }

    /// <summary>
    /// Generates the input for one iteration, biasing the generator toward
    /// the target under test: storm inputs skew long and verb-heavy, REPL
    /// inputs look like terminal lines, everything else uses
    /// <see cref="Generate(int, int, int)"/>.
    /// </summary>
    public static FuzzInput GenerateForTarget(string target, int seed, int iteration, int maxBytes) => target switch
    {
        "storm" => GenerateStorm(seed, iteration, maxBytes),
        "repl" => GenerateRepl(seed, iteration, maxBytes),
        _ => Generate(seed, iteration, maxBytes),
    };

    /// <summary>
    /// Storm-shaped inputs: long bursts of negotiation verbs mixed with
    /// subnegotiations and text. Lengths skew long here — tripping the storm
    /// guard needs volume, and short verb runs are already covered by the
    /// targeted strategy.
    /// </summary>
    public static FuzzInput GenerateStorm(int seed, int iteration, int maxBytes)
    {
        var random = new Random(unchecked(seed * 31 + iteration));
        var length = maxBytes < 64
            ? maxBytes
            : random.Next(2) == 0
                ? random.Next(64, maxBytes + 1)
                : random.Next(1, 64);
        var buf = new List<byte>();
        while (buf.Count < length)
        {
            buf.AddRange(random.Next(10) switch
            {
                < 6 => StormBurst(random, random.Next(4, 60)),
                < 8 => BuildTargetedFrame(random),
                _ => TextRun(random),
            });
        }

        var bytes = Sized(buf, length, random);
        return new FuzzInput(bytes, PickSplits(random, bytes.Length));
    }

    /// <summary>
    /// REPL-shaped inputs: an optional negotiation prefix (the session still
    /// negotiates underneath the prompt loop) followed by command lines with
    /// varied line endings, overlong lines, backspaces, and empty submits.
    /// </summary>
    public static FuzzInput GenerateRepl(int seed, int iteration, int maxBytes)
    {
        var random = new Random(unchecked(seed * 31 + iteration));
        var length = random.Next(5) < 2
            ? random.Next(1, Math.Min(32, maxBytes) + 1)
            : random.Next(1, maxBytes + 1);
        var buf = new List<byte>();
        if (random.Next(3) == 0)
        {
            buf.AddRange(FillTargeted(random, Math.Min(64, maxBytes)));
        }

        string[] commands =
        [
            "help", "version", "negotiation", "stats", "environ", "slc",
            "quit", "QUIT", "Quit", "q", "", " ",
            "rm -rf /", "echo $TERM", "\b\b", "\u007fx",
            new string('a', 200), "help extra args", "bogus-command!",
        ];
        string[] endings = ["\r\n", "\n", "\r", "\r\0", ""];
        while (buf.Count < length)
        {
            buf.AddRange(Ascii(commands[random.Next(commands.Length)]));
            buf.AddRange(Ascii(endings[random.Next(endings.Length)]));
            if (random.Next(8) == 0)
            {
                buf.AddRange(StormBurst(random, random.Next(1, 6)));
            }
        }

        var bytes = Sized(buf, length, random);
        return new FuzzInput(bytes, PickSplits(random, bytes.Length));
    }

    /// <summary>
    /// Generates the input for one iteration.
    /// </summary>
    public static FuzzInput Generate(int seed, int iteration, int maxBytes)
    {
        var random = new Random(unchecked(seed * 31 + iteration));
        // Tiny-biased lengths: most states are reachable in a few bytes, and
        // short inputs minimize faster. Two in five inputs stay at or under
        // 32 bytes; the rest span the full range.
        var length = random.Next(5) < 2
            ? random.Next(1, Math.Min(32, maxBytes) + 1)
            : random.Next(1, maxBytes + 1);
        byte[] bytes;
        if (s_external is { Count: > 0 } && random.Next(4) == 0)
        {
            bytes = FillExternal(random, length);
        }
        else switch (random.Next(7))
            {
                case 0:
                    bytes = new byte[length];
                    FillBiased(random, bytes);
                    break;
                case 1:
                    bytes = new byte[length];
                    FillFrames(random, bytes);
                    break;
                case 2:
                    bytes = new byte[length];
                    FillMccpLike(random, bytes);
                    break;
                case 3:
                    bytes = new byte[length];
                    random.NextBytes(bytes);
                    SpliceCommands(random, bytes);
                    break;
                case 4:
                    bytes = FillCorpus(random, length);
                    break;
                case 5:
                    bytes = FillMultibyte(random, length);
                    break;
                default:
                    bytes = FillTargeted(random, length);
                    break;
            }

        return new FuzzInput(bytes, PickSplits(random, bytes.Length));
    }

    private static void FillBiased(Random random, byte[] bytes)
    {
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = random.Next(10) < 6
                ? Interesting[random.Next(Interesting.Length)]
                : (byte)random.Next(256);
        }
    }

    private static void FillFrames(Random random, byte[] bytes)
    {
        var i = 0;
        while (i < bytes.Length)
        {
            if (random.Next(3) == 0 && i + 2 < bytes.Length)
            {
                bytes[i++] = Iac;
                bytes[i++] = (byte)random.Next(256);
                if (random.Next(2) == 0 && i < bytes.Length)
                {
                    bytes[i++] = (byte)random.Next(256);
                }

                continue;
            }

            if (random.Next(4) == 0 && i + 4 < bytes.Length)
            {
                bytes[i++] = Iac;
                bytes[i++] = Sb;
                bytes[i++] = MudOptions[random.Next(MudOptions.Length)];
                var payload = random.Next(Math.Min(8, bytes.Length - i - 1));
                for (var j = 0; j < payload; j++)
                {
                    bytes[i++] = Interesting[random.Next(Interesting.Length)];
                }

                if (random.Next(4) != 0 && i + 1 < bytes.Length)
                {
                    bytes[i++] = Iac;
                    if (i < bytes.Length)
                    {
                        bytes[i++] = random.Next(3) == 0 ? (byte)random.Next(256) : Se;
                    }
                }

                continue;
            }

            bytes[i++] = (byte)random.Next(256);
        }
    }

    private static void FillMccpLike(Random random, byte[] bytes)
    {
        var i = 0;
        if (i + 6 < bytes.Length)
        {
            bytes[i++] = Iac;
            bytes[i++] = Sb;
            bytes[i++] = random.Next(2) == 0 ? (byte)86 : (byte)87;
            bytes[i++] = Iac;
            bytes[i++] = Se;
        }

        var deflated = DeflateSample(random);
        var take = Math.Min(deflated.Length, bytes.Length - i);
        Array.Copy(deflated, 0, bytes, i, take);
        i += take;
        while (i < bytes.Length)
        {
            bytes[i++] = (byte)random.Next(256);
        }

        if (random.Next(2) == 0 && bytes.Length > 2)
        {
            bytes[random.Next(bytes.Length)] = Iac;
        }
    }

    private static byte[] DeflateSample(Random random)
    {
        var plain = new byte[random.Next(16, 256)];
        for (var i = 0; i < plain.Length; i++)
        {
            plain[i] = random.Next(4) == 0 ? (byte)random.Next(256) : (byte)random.Next(32, 127);
        }

        using var output = new System.IO.MemoryStream();
        var header = random.Next(3);
        System.IO.Stream compressor = header switch
        {
            0 => new System.IO.Compression.ZLibStream(output, System.IO.Compression.CompressionLevel.Fastest),
            1 => new System.IO.Compression.GZipStream(output, System.IO.Compression.CompressionLevel.Fastest),
            _ => new System.IO.Compression.DeflateStream(output, System.IO.Compression.CompressionLevel.Fastest),
        };
        using (compressor)
        {
            compressor.Write(plain, 0, plain.Length);
        }

        var compressed = output.ToArray();
        if (random.Next(3) == 0 && compressed.Length > 4)
        {
            Array.Resize(ref compressed, random.Next(1, compressed.Length));
        }

        return compressed;
    }

    /// <summary>
    /// Builds a byte soup of ASCII runs, valid 2/3/4-byte UTF-8 sequences,
    /// and classic malformed shapes (truncated leads, lone continuations,
    /// overlongs, surrogate halves). Split delivery then straddles the
    /// multibyte boundaries, stressing the decoders' resume paths.
    /// </summary>
    private static byte[] FillMultibyte(Random random, int maxBytes)
    {
        var buf = new List<byte>();
        while (buf.Count < maxBytes)
        {
            switch (random.Next(8))
            {
                case 0:
                    buf.AddRange(Ascii("hello "));
                    break;
                case 1:
                    // Valid 2-byte sequence (U+00E9).
                    buf.AddRange([0xC3, 0xA9]);
                    break;
                case 2:
                    // Valid 3-byte sequence (U+20AC).
                    buf.AddRange([0xE2, 0x82, 0xAC]);
                    break;
                case 3:
                    // Valid 4-byte sequence (U+1F600).
                    buf.AddRange([0xF0, 0x9F, 0x98, 0x80]);
                    break;
                case 4:
                    // Truncated lead: the continuation arrives (or never
                    // arrives) in a later read.
                    buf.Add(random.Next(2) == 0 ? (byte)0xE2 : (byte)0xF0);
                    break;
                case 5:
                    // Lone continuation.
                    buf.Add((byte)random.Next(0x80, 0xC0));
                    break;
                case 6:
                    // Overlong "/" and surrogate half U+D800.
                    buf.AddRange(random.Next(2) == 0 ? [0xC0, 0xAF] : [0xED, 0xA0, 0x80]);
                    break;
                default:
                    buf.Add((byte)random.Next(256));
                    break;
            }
        }

        return Sized(buf, random.Next(1, maxBytes + 1), random);
    }

    private static void SpliceCommands(Random random, byte[] bytes)
    {
        var splices = random.Next(1, 4);
        for (var s = 0; s < splices && bytes.Length >= 3; s++)
        {
            var at = random.Next(bytes.Length - 2);
            bytes[at] = Iac;
            bytes[at + 1] = (byte)random.Next(240, 256);
            bytes[at + 2] = (byte)random.Next(256);
        }
    }

    private static int[] PickSplits(Random random, int length)
    {
        if (length < 2 || random.Next(2) == 0)
        {
            return [];
        }

        if (length <= 16 && random.Next(4) == 0)
        {
            // Byte drip: every byte arrives in its own read, stressing the
            // IAC/SB resume paths at every possible stall point.
            var all = new int[length - 1];
            for (var i = 1; i < length; i++)
            {
                all[i - 1] = i;
            }

            return all;
        }

        var chunks = random.Next(2, Math.Min(9, length + 1));
        var points = new SortedSet<int>();
        while (points.Count < chunks - 1)
        {
            points.Add(random.Next(1, length));
        }

        return [.. points];
    }

    private static byte[] Ascii(string text) => System.Text.Encoding.ASCII.GetBytes(text);

    private static byte[] Frame(byte option, byte[] body) => [Iac, Sb, option, .. body, Iac, Se];

    /// <summary>
    /// Concatenates 1-2 external-corpus entries and corrupts the result.
    /// Runs of this strategy turn one run's novel finds into the next run's
    /// starting points.
    /// </summary>
    private static byte[] FillExternal(Random random, int maxBytes)
    {
        var external = s_external;
        ArgumentNullException.ThrowIfNull(external);
        var buf = new List<byte>();
        var picks = random.Next(1, 3);
        for (var p = 0; p < picks; p++)
        {
            buf.AddRange(external[random.Next(external.Count)]);
        }

        MutateBytes(random, buf);
        return Sized(buf, random.Next(1, maxBytes + 1), random);
    }

    /// <summary>
    /// Concatenates 1-3 corpus entries and corrupts the result, so inputs
    /// keep a valid skeleton (agreed negotiation, well-formed frames) while
    /// probing the neighbourhood: bit flips, hostile overwrites, truncation,
    /// segment duplication, and single-point crossover between entries.
    /// </summary>
    private static byte[] FillCorpus(Random random, int maxBytes)
    {
        var segments = new List<byte[]>();
        var picks = random.Next(1, 4);
        for (var p = 0; p < picks; p++)
        {
            segments.Add(Corpus.Entries[random.Next(Corpus.Entries.Length)]);
        }

        if (segments.Count > 1 && random.Next(3) == 0)
        {
            // Havoc crossover: the prefix of one entry spliced to the suffix
            // of another mixes two valid skeletons into one ragged hybrid.
            var a = segments[random.Next(segments.Count)];
            var b = segments[random.Next(segments.Count)];
            segments.Add([.. a[..random.Next(a.Length + 1)], .. b[random.Next(b.Length + 1)..]]);
        }

        var buf = new List<byte>();
        foreach (var segment in segments)
        {
            buf.AddRange(segment);
        }

        MutateBytes(random, buf);
        return Sized(buf, random.Next(1, maxBytes + 1), random);
    }

    /// <summary>
    /// Concatenates 1-4 targeted per-option frames (valid, edge, and
    /// malformed field shapes) with occasional text, then lightly corrupts.
    /// </summary>
    private static byte[] FillTargeted(Random random, int maxBytes)
    {
        var buf = new List<byte>();
        var frames = random.Next(1, 5);
        for (var f = 0; f < frames; f++)
        {
            buf.AddRange(BuildTargetedFrame(random));
            if (random.Next(3) == 0)
            {
                buf.AddRange(TextRun(random));
            }
        }

        if (random.Next(4) == 0)
        {
            MutateBytes(random, buf);
        }

        return Sized(buf, random.Next(1, maxBytes + 1), random);
    }

    private static byte[] Sized(List<byte> buf, int length, Random random)
    {
        if (buf.Count > length)
        {
            buf.RemoveRange(length, buf.Count - length);
        }

        while (buf.Count < length)
        {
            buf.Add(Interesting[random.Next(Interesting.Length)]);
        }

        return [.. buf];
    }

    private static void MutateBytes(Random random, List<byte> buf)
    {
        if (buf.Count == 0)
        {
            return;
        }

        switch (random.Next(4))
        {
            case 0:
                var flips = random.Next(1, Math.Min(9, buf.Count + 1));
                for (var f = 0; f < flips; f++)
                {
                    var at = random.Next(buf.Count);
                    buf[at] ^= (byte)(1 << random.Next(8));
                }

                break;
            case 1:
                var writes = random.Next(1, Math.Min(5, buf.Count + 1));
                for (var w = 0; w < writes; w++)
                {
                    buf[random.Next(buf.Count)] = Interesting[random.Next(Interesting.Length)];
                }

                break;
            case 2:
                var cut = random.Next(1, buf.Count + 1);
                buf.RemoveRange(cut, buf.Count - cut);
                break;
            default:
                var at2 = random.Next(buf.Count);
                var segLen = random.Next(1, Math.Min(16, buf.Count - at2) + 1);
                buf.InsertRange(random.Next(buf.Count + 1), buf.GetRange(at2, segLen));
                break;
        }
    }

    private static readonly byte[] NegotiationOptions =
    [
        0, 1, 3, 5, 6, 18, 24, 25, 31, 32, 33, 34, 35, 36, 39, 42, 44,
        69, 70, 86, 87, 90, 91, 93, 102, 200, 201, 255,
    ];

    private static byte[] BuildTargetedFrame(Random random) => random.Next(20) switch
    {
        0 => NegotiationStorm(random),
        1 => NawsFrame(random),
        2 => TtypeFrame(random),
        3 => TspeedFrame(random),
        4 => EnvironFrame(random),
        5 => CharsetFrame(random),
        6 => LinemodeFrame(random),
        7 => StatusFrame(random),
        8 => XdisplocFrame(random),
        9 => SndlocFrame(random),
        10 => LflowFrame(random),
        11 => MsdpFrame(random),
        12 => MsspFrame(random),
        13 => ZmpFrame(random),
        14 => AtcpFrame(random),
        15 => GmcpTargeted(random),
        16 => ComPortFrame(random),
        17 => AardwolfFrame(random),
        18 => TlsHelloFrame(random),
        _ => TextRun(random),
    };

    private static byte[] NegotiationStorm(Random random) => StormBurst(random, random.Next(2, 9));

    private static byte[] StormBurst(Random random, int count)
    {
        var buf = new List<byte>();
        for (var i = 0; i < count; i++)
        {
            buf.Add(Iac);
            buf.Add((byte)(251 + random.Next(4)));
            buf.Add(random.Next(4) == 0 ? (byte)random.Next(256) : NegotiationOptions[random.Next(NegotiationOptions.Length)]);
        }

        return [.. buf];
    }

    private static byte[] NawsFrame(Random random) => random.Next(4) switch
    {
        0 => Frame(31, [(byte)(random.Next(256)), (byte)(random.Next(256)), (byte)(random.Next(256)), (byte)(random.Next(256))]),
        1 => Frame(31, [0, 0, 0, 0]),
        2 => Frame(31, [255, 255, 255, 255]),
        _ => Frame(31, RandomBytes(random, random.Next(0, 7))),
    };

    private static byte[] TtypeFrame(Random random)
    {
        string[] names = ["xterm", "ANSI", "vt100", "screen-256color", "MTTS 137", "MUDCLIENT", "TELNET",
            "xterm-256color", "DEC-VT100", "MTTS 141", "XTERM", "", new string('A', 64), "x\0y"];
        return random.Next(4) switch
        {
            0 => Frame(24, [0, .. Ascii(names[random.Next(names.Length)])]),
            1 => Frame(24, [1]),
            2 => Frame(24, [(byte)random.Next(2, 256), .. Ascii("xterm")]),
            _ => Frame(24, RandomBytes(random, random.Next(0, 10))),
        };
    }

    private static byte[] TspeedFrame(Random random)
    {
        string[] speeds = ["38400,38400", "115200,115200", "0,0", "9600", "abc,def", "", "38400", "1,2,3",
            "57600,57600", "0,1200", "38400,0", "999999,999999"];
        return random.Next(3) switch
        {
            0 => Frame(32, [0, .. Ascii(speeds[random.Next(speeds.Length)])]),
            1 => Frame(32, [1]),
            _ => Frame(32, RandomBytes(random, random.Next(0, 12))),
        };
    }

    private static byte[] EnvironFrame(Random random)
    {
        byte option = random.Next(2) == 0 ? (byte)36 : (byte)39;
        var body = new List<byte> { (byte)random.Next(0, 5) };
        string[] names = ["TERM", "LANG", "COLUMNS", "USER", "SYSTEMTYPE", "DISPLAY", "LINES", "TERMINAL-TYPE", "", "A B"];
        string[] values = ["xterm", "en_US.UTF-8", "80", "MTTS 137", "xterm-256color", ":0", "", "v\x1balue"];
        var entries = random.Next(0, 4);
        for (var i = 0; i < entries; i++)
        {
            body.Add(random.Next(4) == 0 ? (byte)3 : (byte)0);
            body.AddRange(Ascii(names[random.Next(names.Length)]));
            if (random.Next(5) != 0)
            {
                body.Add(1);
                body.AddRange(Ascii(values[random.Next(values.Length)]));
            }

            if (random.Next(6) == 0)
            {
                body.Add(2);
                body.Add((byte)random.Next(256));
            }
        }

        if (random.Next(6) == 0 && body.Count > 1)
        {
            body.RemoveRange(body.Count - 1, 1);
        }

        return Frame(option, [.. body]);
    }

    private static byte[] CharsetFrame(Random random)
    {
        string[] names = ["UTF-8", "US-ASCII", "LATIN-1", "BOGUS-999", "UTF8", "utf-8", "ISO-8859-1", "CP1252", "KOI8-R", ""];
        return random.Next(6) switch
        {
            0 => Frame(42, [1, .. Ascii(" " + string.Join(" ", names[..random.Next(1, 5)]))]),
            1 => Frame(42, [1, (byte)';', .. Ascii(string.Join(";", names[..3]))]),
            2 => Frame(42, [2, .. Ascii(names[random.Next(names.Length)])]),
            3 => Frame(42, [(byte)random.Next(3, 8)]),
            4 => Frame(42, [1]),
            _ => Frame(42, RandomBytes(random, random.Next(0, 16))),
        };
    }

    private static byte[] LinemodeFrame(Random random) => random.Next(5) switch
    {
        0 => Frame(34, [1, (byte)random.Next(256)]),
        1 => Frame(34, [1, (byte)random.Next(256), (byte)random.Next(256)]),
        2 => Frame(34, [2, .. RandomBytes(random, random.Next(0, 7))]),
        3 => SlcFrame(random),
        _ => Frame(34, [(byte)random.Next(4, 256), .. RandomBytes(random, random.Next(0, 8))]),
    };

    private static byte[] SlcFrame(Random random)
    {
        var body = new List<byte> { 3 };
        var triplets = random.Next(0, 5);
        for (var i = 0; i < triplets; i++)
        {
            body.Add((byte)random.Next(0, 14));
            body.Add((byte)random.Next(256));
            body.Add((byte)random.Next(256));
        }

        if (random.Next(3) == 0 && body.Count > 1)
        {
            var drop = random.Next(1, Math.Min(3, body.Count));
            body.RemoveRange(body.Count - drop, drop);
        }

        return Frame(34, [.. body]);
    }

    private static byte[] StatusFrame(Random random) => random.Next(3) switch
    {
        0 => Frame(5, [1]),
        1 => Frame(5, [0, .. RandomBytes(random, random.Next(0, 13))]),
        _ => Frame(5, RandomBytes(random, random.Next(0, 13))),
    };

    private static byte[] XdisplocFrame(Random random)
    {
        string[] displays = [":0", ":0.0", "", "host:1", new string('x', 40)];
        return random.Next(3) switch
        {
            0 => Frame(35, [0, .. Ascii(displays[random.Next(displays.Length)])]),
            1 => Frame(35, [1]),
            _ => Frame(35, RandomBytes(random, random.Next(0, 10))),
        };
    }

    private static byte[] SndlocFrame(Random random)
    {
        string[] locations = ["room-1", "", "a b", new string('z', 32)];
        return random.Next(3) switch
        {
            0 => Frame(23, Ascii(locations[random.Next(locations.Length)])),
            1 => Frame(23, [(byte)random.Next(256), .. Ascii("loc")]),
            _ => Frame(23, RandomBytes(random, random.Next(0, 10))),
        };
    }

    private static byte[] LflowFrame(Random random) => Frame(33, [(byte)random.Next(0, 6)]);

    private static byte[] MsdpFrame(Random random)
    {
        var body = new List<byte>();
        var vars = random.Next(1, 4);
        for (var i = 0; i < vars; i++)
        {
            body.Add(1);
            body.AddRange(Ascii("V" + random.Next(4)));
            body.Add(2);
            body.AddRange(MsdpValue(random, random.Next(0, 3)));
        }

        return Frame(69, [.. body]);
    }

    private static byte[] MsdpValue(Random random, int depth)
    {
        if (depth <= 0 || random.Next(3) == 0)
        {
            string[] atoms = ["100", "", "x", "a b"];
            return Ascii(atoms[random.Next(atoms.Length)]);
        }

        var buf = new List<byte>();
        if (random.Next(2) == 0)
        {
            buf.Add(3);
            var entries = random.Next(1, 4);
            for (var i = 0; i < entries; i++)
            {
                buf.Add(1);
                buf.AddRange(Ascii("K" + random.Next(3)));
                buf.Add(2);
                buf.AddRange(MsdpValue(random, depth - 1));
            }

            buf.Add(4);
        }
        else
        {
            buf.Add(5);
            var items = random.Next(1, 4);
            for (var i = 0; i < items; i++)
            {
                buf.Add(2);
                buf.AddRange(MsdpValue(random, depth - 1));
            }

            buf.Add(6);
        }

        return [.. buf];
    }

    private static byte[] MsspFrame(Random random)
    {
        var body = new List<byte>();
        var vars = random.Next(1, 5);
        for (var i = 0; i < vars; i++)
        {
            if (random.Next(6) != 0)
            {
                body.Add(1);
                body.AddRange(Ascii("N" + random.Next(3)));
            }

            var vals = random.Next(0, 3);
            for (var v = 0; v < vals; v++)
            {
                body.Add(2);
                body.AddRange(Ascii("v" + random.Next(3)));
            }
        }

        return Frame(70, [.. body]);
    }

    private static byte[] ZmpFrame(Random random)
    {
        string[] commands = ["zmp.ident", "zmp.check", "zmp.ping", "", "no-nuls-here"];
        var parts = new List<string> { commands[random.Next(commands.Length)] };
        string[] argPool = ["mud", "1.0", "", "a b"];
        var args = random.Next(0, 4);
        for (var i = 0; i < args; i++)
        {
            parts.Add(argPool[random.Next(argPool.Length)]);
        }

        var joined = string.Join('\0', parts);
        if (random.Next(4) != 0)
        {
            joined += '\0';
        }

        return Frame(93, Ascii(joined));
    }

    private static byte[] AtcpFrame(Random random) => random.Next(4) switch
    {
        0 => Frame(200, Ascii("Room.Exits ne,nw")),
        1 => Frame(200, Ascii("PackageOnly")),
        2 => Frame(200, []),
        _ => Frame(200, RandomBytes(random, random.Next(0, 12))),
    };

    private static byte[] GmcpTargeted(Random random)
    {
        string[] valid = ["Core.Hello {\"client\":\"t\"}", "Char.Vitals {\"hp\":1}", "Room.Info [1,2]", "P.X true", "P.Y null",
            "Core.Hello {\"client\":\"t\",\"features\":{\"msdp\":true,\"mssp\":false}}",
            "Char.Vitals {\"hp\":100,\"stats\":{\"str\":18,\"dex\":14},\"flags\":[true,false,null]}",
            "Room.Info {\"exits\":[\"n\",\"s\"],\"id\":42}", "P.Esc {\"q\":\"a\\\"b\"}"];
        string[] broken = ["Pkg {oops", "Pkg [1,", "Pkg {\"a\":", "Pkg \x06binary"];
        return random.Next(5) switch
        {
            0 => Frame(201, Ascii(valid[random.Next(valid.Length)])),
            1 => Frame(201, Ascii("PackageOnly")),
            2 => Frame(201, Ascii(broken[random.Next(broken.Length)])),
            3 => Frame(201, []),
            _ => Frame(201, RandomBytes(random, random.Next(0, 24))),
        };
    }

    private static byte[] ComPortFrame(Random random)
    {
        var sub = random.Next(9) switch
        {
            < 8 => (byte)random.Next(0, 8),
            _ => (byte)random.Next(100, 108),
        };
        return Frame(44, [sub, .. RandomBytes(random, random.Next(0, 3))]);
    }

    /// <summary>
    /// Aardwolf (option 102) frames: bare channel, channel plus data,
    /// channel plus trailing bytes, empty, and hostile channel values.
    /// </summary>
    private static byte[] AardwolfFrame(Random random) => random.Next(5) switch
    {
        0 => Frame(102, [(byte)random.Next(256)]),
        1 => Frame(102, [(byte)random.Next(256), (byte)random.Next(256)]),
        2 => Frame(102, [(byte)random.Next(256), .. RandomBytes(random, random.Next(0, 8))]),
        3 => Frame(102, []),
        _ => Frame(102, RandomBytes(random, random.Next(0, 4))),
    };

    /// <summary>
    /// TLS ClientHello shapes: a plausible record with a random session body,
    /// plus truncated and bare-prefix forms that stall the first-byte (0x16)
    /// sniff path at every resume point.
    /// </summary>
    private static byte[] TlsHelloFrame(Random random)
    {
        if (random.Next(4) == 0)
        {
            return random.Next(3) switch
            {
                0 => [0x16],
                1 => [0x16, 0x03],
                _ => [0x16, 0x03, 0x01, 0x00],
            };
        }

        var body = new List<byte> { 0x01, 0x00, 0x00, 0x2A, 0x03, 0x03 };
        var session = new byte[random.Next(0, 33)];
        random.NextBytes(session);
        body.AddRange(session);
        body.AddRange(RandomBytes(random, random.Next(0, 24)));
        var record = new List<byte> { 0x16, 0x03, 0x01, 0x00, (byte)(body.Count % 256) };
        record.AddRange(body);
        if (random.Next(3) == 0 && record.Count > 2)
        {
            // Ragged tail: the hello stalls mid-record.
            var cut = random.Next(1, record.Count);
            record.RemoveRange(cut, record.Count - cut);
        }

        return [.. record];
    }

    private static byte[] TextRun(Random random)
    {
        string[] words = ["hello", "login", "look", "quit", "pass", "~", "#$", " ", ""];
        string[] ends = ["\r\n", "\r\0", "\r", "\n", " "];
        var text = string.Concat(
            System.Linq.Enumerable.Range(0, random.Next(1, 5))
                .Select(_ => words[random.Next(words.Length)] + ends[random.Next(ends.Length)]));
        var buf = new List<byte>(Ascii(text));
        if (random.Next(4) == 0)
        {
            buf.InsertRange(random.Next(buf.Count + 1), [Iac, Iac]);
        }

        return [.. buf];
    }

    private static byte[] RandomBytes(Random random, int count)
    {
        var buf = new byte[count];
        for (var i = 0; i < count; i++)
        {
            buf[i] = random.Next(10) < 6
                ? Interesting[random.Next(Interesting.Length)]
                : (byte)random.Next(256);
        }

        return buf;
    }
}
