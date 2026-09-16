using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("telnet_cs.Tests")]

namespace telnet_cs.Fuzz;

/// <summary>
/// Standalone fuzz entry point. Iterates the deterministic input stream,
/// drives the selected harnesses under a per-iteration hang guard, minimizes
/// and saves a repro artifact per unexpected exception (or reply
/// amplification), and exits 1 when any crash was found. Never referenced by
/// the test suite; runs only via <c>dotnet run --project telnet_cs.Fuzz</c>.
/// </summary>
internal static class Program
{
    private static readonly Dictionary<string, int> SeenKinds = new(StringComparer.Ordinal);

    private const int MaxSavedPerKind = 3;
    private const long AmplificationFloor = 65536;
    private const long AmplificationFactor = 128;

    internal static async Task<int> Main(string[] args)
    {
        if (!FuzzOptions.TryParse(args, out var options, out var error))
        {
            Console.WriteLine(string.IsNullOrEmpty(error) ? FuzzOptions.Usage : error);
            return string.IsNullOrEmpty(error) ? 0 : 2;
        }

        ArgumentNullException.ThrowIfNull(options);
        Mutator.LoadExternalCorpus(options.CorpusDir);
        CoverageTracker? coverage = string.IsNullOrEmpty(options.CorpusDir)
            ? null
            : new CoverageTracker(options.CorpusDir);
        var crashes = 0;
        foreach (var target in TargetsFor(options.Mode))
        {
            Console.WriteLine($"Target {target}: {options.Iters} iterations (seed {options.Seed}).");
        }

        for (var i = 0; i < options.Iters; i++)
        {
            foreach (var target in TargetsFor(options.Mode))
            {
                var seq = BuildSequence(target, options, i);
                crashes += await RunOneAsync(options, i, seq, target, coverage, CancellationToken.None).ConfigureAwait(false);
            }

            if ((i + 1) % 500 == 0)
            {
                Console.WriteLine($"[{i + 1}/{options.Iters}] crashes: {crashes}");
            }
        }

        Console.WriteLine(crashes == 0
            ? $"Done: {options.Iters} iterations, no crashes (seed {options.Seed}, mode {options.Mode})."
            : $"Done: {options.Iters} iterations, {crashes} crash(es) saved under '{options.OutDir}/' (seed {options.Seed}).");
        foreach (var (kind, count) in SeenKinds.OrderByDescending(static kv => kv.Value))
        {
            Console.WriteLine($"  {kind}: {count} (first {Math.Min(count, MaxSavedPerKind)} saved)");
        }

        if (coverage is not null)
        {
            Console.WriteLine($"  novel corpus inputs saved: {coverage.Saved}");
        }

        return crashes == 0 ? 0 : 1;
    }

    private static string[] TargetsFor(FuzzMode mode) => mode switch
    {
        FuzzMode.Parser => ["parser"],
        FuzzMode.Session => ["session"],
        FuzzMode.Client => ["client"],
        FuzzMode.Auth => ["auth"],
        FuzzMode.Codec => ["codec"],
        FuzzMode.Encoding => ["encoding"],
        FuzzMode.Input => ["input"],
        FuzzMode.Write => ["write"],
        FuzzMode.Term => ["term"],
        FuzzMode.Mccp => ["mccp"],
        FuzzMode.Proto => ["proto"],
        FuzzMode.Accept => ["accept"],
        FuzzMode.Both => ["parser", "session"],
        _ => ["parser", "session", "client", "auth", "codec", "encoding", "input",
            "write", "term", "mccp", "proto", "accept"],
    };

    private static IReadOnlyList<FuzzInput> BuildSequence(string target, FuzzOptions options, int iteration)
    {
        if (options.Rounds < 2 || (target != "session" && target != "client"))
        {
            return [Mutator.Generate(options.Seed, iteration, options.MaxBytes)];
        }

        var seq = new List<FuzzInput>(options.Rounds);
        for (var r = 0; r < options.Rounds; r++)
        {
            seq.Add(Mutator.Generate(unchecked(options.Seed + iteration), r, options.MaxBytes));
        }

        return seq;
    }

    private static Task<(long Outbound, long Hash)> DriveAsync(string target, IReadOnlyList<FuzzInput> seq, CancellationToken token) => target switch
    {
        "parser" => ParserHarness.RunAsync(seq[0], token),
        "session" => seq.Count == 1 ? SessionHarness.RunAsync(seq[0], token) : SessionHarness.RunSequenceAsync(seq, token),
        "client" => seq.Count == 1 ? ClientHarness.RunAsync(seq[0], token) : ClientHarness.RunSequenceAsync(seq, token),
        "auth" => AuthHarness.RunAsync(seq[0], token),
        "codec" => CodecHarness.RunAsync(seq[0], token),
        "encoding" => EncodingHarness.RunAsync(seq[0], token),
        "input" => InputHarness.RunAsync(seq[0], token),
        "write" => WriteHarness.RunAsync(seq[0], token),
        "term" => TermHarness.RunAsync(seq[0], token),
        "mccp" => MccpHarness.RunAsync(seq[0], token),
        "proto" => ProtoHarness.RunAsync(seq[0], token),
        "accept" => AcceptHarness.RunAsync(seq[0], token),
        _ => throw new ArgumentOutOfRangeException(nameof(target)),
    };

    private static async Task SaveUniqueAsync(
        FuzzOptions options,
        string target,
        int iteration,
        FuzzInput input,
        Exception ex,
        byte[]? minimized,
        string? replayMode = null)
    {
        var kind = $"{target}:{ex.GetType().FullName}:{CrashReporter.StackKey(ex)}";
        SeenKinds.TryGetValue(kind, out var count);
        count++;
        SeenKinds[kind] = count;
        if (count <= MaxSavedPerKind)
        {
            var extra = (options.Rounds > 1 ? $" --rounds {options.Rounds}" : string.Empty)
                + (options.CorpusDir.Length != 0 ? $" --corpus-dir {options.CorpusDir}" : string.Empty);
            await CrashReporter.SaveAsync(
                options.OutDir, target, options.Seed, iteration, input, ex,
                CancellationToken.None, count > 1 ? $"-k{count}" : null,
                minimized, extra.Length == 0 ? null : extra, replayMode).ConfigureAwait(false);
        }
    }

    private static async Task<int> RunOneAsync(
        FuzzOptions options,
        int iteration,
        IReadOnlyList<FuzzInput> seq,
        string target,
        CoverageTracker? coverage,
        CancellationToken cancellationToken)
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromMilliseconds(options.PerIterTimeoutMs));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, guard.Token);
        try
        {
            var (outbound, hash) = await DriveAsync(target, seq, linked.Token).ConfigureAwait(false);
            var inbound = seq.Sum(static s => (long)s.Bytes.Length);
            if (outbound > Math.Max(AmplificationFloor, AmplificationFactor * inbound))
            {
                var amplification = new InvalidDataException(
                    $"Reply amplification: {inbound} inbound bytes produced {outbound} outbound bytes.");
                await SaveUniqueAsync(options, $"{target}-amplified", iteration, seq[0], amplification, null, target).ConfigureAwait(false);
                Console.WriteLine($"[iter {iteration}] {target}: AMPLIFIED {inbound}->{outbound}");
                return 1;
            }

            if (coverage is not null && seq.Count == 1 && IsWireTarget(target))
            {
                coverage.Observe(seq[0].Bytes, outbound, hash);
            }

            return 0;
        }
        catch (Exception ex) when (guard.IsCancellationRequested && ex is not OperationCanceledException)
        {
            var timeout = new TimeoutException($"No result within {options.PerIterTimeoutMs} ms: {ex.GetType().Name}.");
            await SaveUniqueAsync(options, $"{target}-timeout", iteration, seq[0], timeout, null, target).ConfigureAwait(false);
            Console.WriteLine($"[iter {iteration}] {target}: TIMEOUT ({ex.GetType().Name})");
            return 1;
        }
        catch (OperationCanceledException) when (guard.IsCancellationRequested)
        {
            var timeout = new TimeoutException($"No result within {options.PerIterTimeoutMs} ms.");
            await SaveUniqueAsync(options, $"{target}-timeout", iteration, seq[0], timeout, null, target).ConfigureAwait(false);
            Console.WriteLine($"[iter {iteration}] {target}: TIMEOUT");
            return 1;
        }
        catch (OutOfMemoryException ex)
        {
            // Hang-class finding (e.g. a non-terminating decoder loop that
            // only stops by exhausting memory): the real stack pinpoints the
            // loop, unlike the synthesized timeout above. Minimization is
            // skipped — reprobing would re-spend the blowup.
            await SaveUniqueAsync(options, $"{target}-hang", iteration, seq[0], ex, null, target).ConfigureAwait(false);
            Console.WriteLine($"[iter {iteration}] {target}: HANG ({ex.GetType().Name}, {seq[0].Bytes.Length} bytes)");
            return 1;
        }
        catch (Exception ex) when (CrashReporter.IsBenign(ex))
        {
            return 0;
        }
        catch (Exception ex)
        {
            byte[]? minimized = null;
            if (seq.Count == 1)
            {
                var min = await Minimizer.MinimizeAsync(
                    seq[0],
                    (probe, token) => DriveAsync(target, [probe], token),
                    ex.GetType(),
                    CancellationToken.None).ConfigureAwait(false);
                minimized = min.Bytes;
            }

            await SaveUniqueAsync(options, target, iteration, seq[0], ex, minimized).ConfigureAwait(false);
            var sizes = minimized is null ? $"{seq[0].Bytes.Length}" : $"{seq[0].Bytes.Length}->{minimized.Length}";
            Console.WriteLine($"[iter {iteration}] {target}: {ex.GetType().Name}: {ex.Message.Split('\n')[0]} [{sizes} bytes]");
            return 1;
        }
    }

    private static bool IsWireTarget(string target) => target is "parser" or "session" or "client" or "auth";
}
