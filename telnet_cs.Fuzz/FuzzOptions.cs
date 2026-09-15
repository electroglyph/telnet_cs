namespace telnet_cs.Fuzz;

/// <summary>
/// Parsed command-line settings for one fuzz run. Immutable after parsing.
/// </summary>
internal sealed record FuzzOptions
{
    required public int Seed { get; init; }

    required public int Iters { get; init; }

    required public int MaxBytes { get; init; }

    required public FuzzMode Mode { get; init; }

    required public string OutDir { get; init; }

    required public int PerIterTimeoutMs { get; init; }

    public static string Usage => """
        telnet_cs.Fuzz — standalone telnet abuse harness (not part of `dotnet test`).

        Usage:
          dotnet run --project telnet_cs.Fuzz -c Release -- [options]

        Options:
          --seed <int>       PRNG seed; re-running with the same seed replays the same inputs (default 1).
          --iters <int>      Iteration count, > 0 (default 5000).
          --max-bytes <int>  Max input bytes per iteration, 1..65536 (default 2048).
          --mode <name>      parser | session | both (default both).
          --out <dir>        Crash artifact directory (default crashes).
          --timeout-ms <int> Per-iteration hang guard, 10..30000 ms (default 2000).
          --help, -h         Print this text.

        Crash artifacts: <out>/<mode>-s<seed>-i<iter>.bin + .txt (input bytes,
        hex dump, split points, exception). Re-run with --seed <s> --iters <i+1>
        --mode <m> to replay the crashing input.
        """;

    public static bool TryParse(string[] args, out FuzzOptions? options, out string error)
    {
        ArgumentNullException.ThrowIfNull(args);
        var seed = 1;
        var iters = 5000;
        var maxBytes = 2048;
        var mode = FuzzMode.Both;
        var outDir = "crashes";
        var timeoutMs = 2000;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] is "--help" or "-h")
            {
                options = null;
                error = string.Empty;
                return false;
            }

            if (!TakeValue(args, ref i, out var value))
            {
                options = null;
                error = $"Missing value for '{args[i]}'. Use --help.";
                return false;
            }

            var flag = args[i - 1];
            switch (flag)
            {
                case "--seed":
                    if (!int.TryParse(value, out seed))
                    {
                        return Fail($"--seed needs an integer, got '{value}'.", out options, out error);
                    }

                    break;
                case "--iters":
                    if (!int.TryParse(value, out iters) || iters <= 0)
                    {
                        return Fail($"--iters needs a positive integer, got '{value}'.", out options, out error);
                    }

                    break;
                case "--max-bytes":
                    if (!int.TryParse(value, out maxBytes) || maxBytes is < 1 or > 65536)
                    {
                        return Fail($"--max-bytes needs 1..65536, got '{value}'.", out options, out error);
                    }

                    break;
                case "--mode":
                    var parsed = value switch
                    {
                        "parser" => (FuzzMode?)FuzzMode.Parser,
                        "session" => (FuzzMode?)FuzzMode.Session,
                        "both" => (FuzzMode?)FuzzMode.Both,
                        _ => null,
                    };
                    if (parsed is null)
                    {
                        return Fail($"--mode needs parser|session|both, got '{value}'.", out options, out error);
                    }

                    mode = parsed.Value;
                    break;
                case "--out":
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        return Fail("--out needs a directory.", out options, out error);
                    }

                    outDir = value;
                    break;
                case "--timeout-ms":
                    if (!int.TryParse(value, out timeoutMs) || timeoutMs is < 10 or > 30000)
                    {
                        return Fail($"--timeout-ms needs 10..30000, got '{value}'.", out options, out error);
                    }

                    break;
                default:
                    return Fail($"Unknown argument '{flag}'. Use --help.", out options, out error);
            }
        }

        options = new FuzzOptions
        {
            Seed = seed,
            Iters = iters,
            MaxBytes = maxBytes,
            Mode = mode,
            OutDir = outDir,
            PerIterTimeoutMs = timeoutMs,
        };
        error = string.Empty;
        return true;
    }

    private static bool Fail(string message, out FuzzOptions? options, out string error)
    {
        options = null;
        error = message;
        return false;
    }

    private static bool TakeValue(string[] args, ref int index, out string value)
    {
        if (index + 1 >= args.Length)
        {
            value = string.Empty;
            return false;
        }

        index++;
        value = args[index];
        return true;
    }
}
