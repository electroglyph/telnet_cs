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
/// The alphabet is biased toward IAC framing, subnegotiation boundaries, and
/// the option bytes the server dispatches on (MCCP, MUD, charset, linemode),
/// with the remainder drawn from raw binary and printable text.
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

    /// <summary>
    /// Generates the input for one iteration.
    /// </summary>
    public static FuzzInput Generate(int seed, int iteration, int maxBytes)
    {
        var random = new Random(unchecked(seed * 31 + iteration));
        var length = random.Next(1, maxBytes + 1);
        var bytes = new byte[length];
        var strategy = random.Next(4);
        switch (strategy)
        {
            case 0:
                FillBiased(random, bytes);
                break;
            case 1:
                FillFrames(random, bytes);
                break;
            case 2:
                FillMccpLike(random, bytes);
                break;
            default:
                random.NextBytes(bytes);
                SpliceCommands(random, bytes);
                break;
        }

        return new FuzzInput(bytes, PickSplits(random, length));
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

        var chunks = random.Next(2, Math.Min(5, length + 1));
        var points = new SortedSet<int>();
        while (points.Count < chunks - 1)
        {
            points.Add(random.Next(1, length));
        }

        return [.. points];
    }
}
