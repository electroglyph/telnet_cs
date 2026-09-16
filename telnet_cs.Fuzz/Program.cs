using System.Runtime.CompilerServices;
using System.Text.Json;

[assembly: InternalsVisibleTo("telnet_cs.Tests")]

namespace telnet_cs.Fuzz;

/// <summary>
/// Standalone fuzz entry point. Iterates the deterministic input stream,
/// drives the selected harnesses under a per-iteration hang guard, minimizes
/// and saves a repro artifact per unexpected exception (or reply
/// amplification/blowup), and exits 1 when any crash was found. Never referenced by
/// the test suite; runs only via <c>dotnet run --project telnet_cs.Fuzz</c>.
/// <c>--jobs</c> parallelizes iterations (content stays seed+index
/// deterministic); <c>--seconds</c> caps the run by wall clock;
/// <c>--input</c> replays one file through each selected harness without
/// saving artifacts; a <c>summary.json</c> lands next to the artifacts.
/// </summary>
internal static class Program
{
    private static readonly Dictionary<string, int> SeenKinds = new(StringComparer.Ordinal);
    private static readonly Lock SeenGate = new();

    private const int MaxSavedPerKind = 3;
    private const long AmplificationFloor = 65536;
    private const long AmplificationFactor = 128;

    /// <summary>
    /// Absolute per-iteration outbound cap (allocation oracle). The ratio
    /// check below catches proportional blowups; this catches reply bombs on
    /// tiny inputs where even the floor would look generous. Fuzz inputs are
    /// at most 64 KiB, so no legitimate harness reply approaches this bound.
    /// </summary>
    private const long AbsoluteOutboundCap = 16 * 1024 * 1024;

    internal static async Task<int> Main(string[] args)
    {
        if (!FuzzOptions.TryParse(args, out var options, out var error))
        {
            Console.WriteLine(string.IsNullOrEmpty(error) ? FuzzOptions.Usage : error);
            return string.IsNullOrEmpty(error) ? 0 : 2;
        }

        ArgumentNullException.ThrowIfNull(options);
        if (options.ListModes)
        {
            foreach (var mode in TargetsFor(FuzzMode.All))
            {
                Console.WriteLine(mode);
            }

            return 0;
        }

        if (options.InputFile.Length != 0)
        {
            return await ReplayAsync(options).ConfigureAwait(false);
        }

        Mutator.LoadExternalCorpus(options.CorpusDir);
        CoverageTracker? coverage = string.IsNullOrEmpty(options.CorpusDir)
            ? null
            : new CoverageTracker(options.CorpusDir);
        var crashes = 0;
        var completed = 0;
        foreach (var target in TargetsFor(options.Mode))
        {
            Console.WriteLine($"Target {target}: {options.Iters} iterations (seed {options.Seed}).");
        }

        // A time budget stops *starting* new iterations; in-flight ones run
        // to their own hang guard so a budget never masks a slow crash.
        var deadline = options.Seconds > 0
            ? DateTime.UtcNow.AddSeconds(options.Seconds)
            : DateTime.MaxValue;
        async Task RunIndexAsync(int i)
        {
            foreach (var target in TargetsFor(options.Mode))
            {
                var seq = BuildSequence(target, options, i);
                var found = await RunOneAsync(options, i, seq, target, coverage, CancellationToken.None).ConfigureAwait(false);
                Interlocked.Add(ref crashes, found);
            }

            var done = Interlocked.Increment(ref completed);
            if (done % 500 == 0)
            {
                Console.WriteLine($"[{done}/{options.Iters}] crashes: {Volatile.Read(ref crashes)}");
            }
        }

        if (options.Jobs < 2)
        {
            for (var i = 0; i < options.Iters && DateTime.UtcNow < deadline; i++)
            {
                await RunIndexAsync(i).ConfigureAwait(false);
            }
        }
        else
        {
            var work = Enumerable.Range(0, options.Iters).TakeWhile(_ => DateTime.UtcNow < deadline);
            await Parallel.ForEachAsync(
                work,
                new ParallelOptions { MaxDegreeOfParallelism = options.Jobs },
                async (i, _) => await RunIndexAsync(i).ConfigureAwait(false)).ConfigureAwait(false);
        }

        Console.WriteLine(crashes == 0
            ? $"Done: {completed} iterations, no crashes (seed {options.Seed}, mode {options.Mode})."
            : $"Done: {completed} iterations, {crashes} crash(es) saved under '{options.OutDir}/' (seed {options.Seed}).");
        Dictionary<string, int> kinds;
        lock (SeenGate)
        {
            kinds = new Dictionary<string, int>(SeenKinds, StringComparer.Ordinal);
        }

        foreach (var (kind, count) in kinds.OrderByDescending(static kv => kv.Value))
        {
            Console.WriteLine($"  {kind}: {count} (first {Math.Min(count, MaxSavedPerKind)} saved)");
        }

        if (coverage is not null)
        {
            Console.WriteLine($"  novel corpus inputs saved: {coverage.Saved}");
        }

        await WriteSummaryAsync(options, completed, crashes, coverage?.Saved ?? 0, kinds).ConfigureAwait(false);
        return crashes == 0 ? 0 : 1;
    }

    private static async Task WriteSummaryAsync(
        FuzzOptions options,
        int completed,
        int crashes,
        int novelSaved,
        Dictionary<string, int> kinds)
    {
        Directory.CreateDirectory(options.OutDir);
        var summary = new
        {
            mode = options.Mode.ToString(),
            seed = options.Seed,
            iters = options.Iters,
            completed,
            crashes,
            novelSaved,
            jobs = options.Jobs,
            seconds = options.Seconds,
            kinds,
        };
        await File.WriteAllTextAsync(
            Path.Combine(options.OutDir, "summary.json"),
            JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true })).ConfigureAwait(false);
        Console.WriteLine($"  summary: {Path.Combine(options.OutDir, "summary.json")}");
    }

    /// <summary>
    /// Replays one input file through every selected harness exactly once
    /// (single round, no splits): prints the outcome per harness, minimizes
    /// unexpected exceptions for display only, and saves nothing. Exit 1 when
    /// any harness raised an unexpected exception.
    /// </summary>
    private static async Task<int> ReplayAsync(FuzzOptions options)
    {
        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(options.InputFile).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Cannot read --input '{options.InputFile}': {ex.Message}");
            return 2;
        }

        if (bytes.Length == 0)
        {
            Console.WriteLine($"--input '{options.InputFile}' is empty.");
            return 2;
        }

        var crashes = 0;
        foreach (var target in TargetsFor(options.Mode))
        {
            var input = new FuzzInput(bytes, []);
            using var guard = new CancellationTokenSource(TimeSpan.FromMilliseconds(options.PerIterTimeoutMs));
            try
            {
                var (outbound, hash) = await DriveAsync(target, [input], guard.Token).ConfigureAwait(false);
                Console.WriteLine($"{target}: ok (in {bytes.Length}, out {outbound}, sig {hash:x16})");
            }
            catch (OperationCanceledException) when (guard.IsCancellationRequested)
            {
                Console.WriteLine($"{target}: TIMEOUT after {options.PerIterTimeoutMs} ms");
                crashes++;
            }
            catch (Exception ex) when (CrashReporter.IsBenign(ex))
            {
                Console.WriteLine($"{target}: benign ({ex.GetType().Name})");
            }
            catch (Exception ex)
            {
                var note = string.Empty;
                if (!options.NoMinimize)
                {
                    var min = await Minimizer.MinimizeAsync(
                        input,
                        (probe, token) => DriveAsync(target, [probe], token),
                        ex.GetType(),
                        CancellationToken.None).ConfigureAwait(false);
                    note = $" [minimized {bytes.Length}->{min.Bytes.Length}]";
                }

                Console.WriteLine($"{target}: {ex.GetType().FullName}: {ex.Message.Split('\n')[0]}{note}");
                crashes++;
            }
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
        FuzzMode.Repl => ["repl"],
        FuzzMode.Request => ["request"],
        FuzzMode.TlsSniff => ["tlssniff"],
        FuzzMode.Caps => ["caps"],
        FuzzMode.Storm => ["storm"],
        FuzzMode.Both => ["parser", "session"],
        _ => ["parser", "session", "client", "auth", "codec", "encoding", "input",
            "write", "term", "mccp", "proto", "accept", "repl", "request",
            "tlssniff", "caps", "storm"],
    };

    private static IReadOnlyList<FuzzInput> BuildSequence(string target, FuzzOptions options, int iteration)
    {
        if (options.Rounds < 2 || (target != "session" && target != "client"))
        {
            return [Mutator.GenerateForTarget(target, options.Seed, iteration, options.MaxBytes)];
        }

        var seq = new List<FuzzInput>(options.Rounds);
        for (var r = 0; r < options.Rounds; r++)
        {
            seq.Add(Mutator.GenerateForTarget(target, unchecked(options.Seed + iteration), r, options.MaxBytes));
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
        "repl" => ReplHarness.RunAsync(seq[0], token),
        "request" => RequestHarness.RunAsync(seq[0], token),
        "tlssniff" => TlsSniffHarness.RunAsync(seq[0], token),
        "caps" => CapsHarness.RunAsync(seq[0], token),
        "storm" => StormHarness.RunAsync(seq[0], token),
        _ => throw new ArgumentOutOfRangeException(nameof(target)),
    };

    private static int CountKind(string kind)
    {
        lock (SeenGate)
        {
            SeenKinds.TryGetValue(kind, out var count);
            count++;
            SeenKinds[kind] = count;
            return count;
        }
    }

    private static async Task SaveUniqueAsync(
        FuzzOptions options,
        string target,
        int iteration,
        FuzzInput input,
        Exception ex,
        byte[]? minimized,
        string? replayMode = null)
    {
        // Kind counting happens under a lock, but the file write stays
        // outside it: two racing workers may share a -k suffix and the last
        // write wins, which is harmless (same crash kind, same stem).
        var kind = $"{target}:{ex.GetType().FullName}:{CrashReporter.StackKey(ex)}";
        var count = CountKind(kind);
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

    /// <summary>
    /// Multi-round counterpart of <see cref="SaveUniqueAsync"/>: a minimized
    /// sequence no longer regenerates from seed + index, so every surviving
    /// round is persisted (<c>.bin</c> per round) alongside a report whose
    /// replay line pins the round count.
    /// </summary>
    private static async Task SaveSequenceAsync(
        FuzzOptions options,
        string target,
        int iteration,
        IReadOnlyList<FuzzInput> sequence,
        Exception ex)
    {
        var kind = $"{target}:{ex.GetType().FullName}:{CrashReporter.StackKey(ex)}";
        var count = CountKind(kind);
        if (count > MaxSavedPerKind)
        {
            return;
        }

        Directory.CreateDirectory(options.OutDir);
        var suffix = count > 1 ? $"-k{count}" : string.Empty;
        var stem = Path.Combine(options.OutDir, $"{target}-s{options.Seed}-i{iteration}{suffix}");
        for (var r = 0; r < sequence.Count; r++)
        {
            await File.WriteAllBytesAsync($"{stem}-r{r}.bin", sequence[r].Bytes).ConfigureAwait(false);
        }

        var sizes = string.Join(",", sequence.Select(static s => s.Bytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        var extra = $" --rounds {sequence.Count}"
            + (options.CorpusDir.Length != 0 ? $" --corpus-dir {options.CorpusDir}" : string.Empty);
        var report = new System.Text.StringBuilder()
            .AppendLine($"target: {target}")
            .AppendLine($"seed: {options.Seed}")
            .AppendLine($"iteration: {iteration}")
            .AppendLine($"rounds: {sequence.Count} (minimized; per-round bytes: {sizes})")
            .AppendLine($"exception: {ex.GetType().FullName}: {ex.Message}")
            .AppendLine("stack:")
            .AppendLine(ex.StackTrace ?? "<none>")
            .AppendLine($"replay: dotnet run --project telnet_cs.Fuzz -c Release -- --seed {options.Seed} --iters {iteration + 1} --mode {target}{extra}")
            .ToString();
        await File.WriteAllTextAsync($"{stem}.txt", report).ConfigureAwait(false);
    }

    private static async Task<int> RunOneAsync(
        FuzzOptions options,
        int iteration,
        IReadOnlyList<FuzzInput> seq,
        string target,
        CoverageTracker? coverage,
        CancellationToken cancellationToken)
    {
        FuzzFaults.ArmForIteration(options.Faults, iteration);
        using var guard = new CancellationTokenSource(TimeSpan.FromMilliseconds(options.PerIterTimeoutMs));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, guard.Token);
        try
        {
            var (outbound, hash) = await DriveAsync(target, seq, linked.Token).ConfigureAwait(false);
            var inbound = seq.Sum(static s => (long)s.Bytes.Length);
            if (outbound > AbsoluteOutboundCap)
            {
                var blowup = new InvalidDataException(
                    $"Reply blowup: {inbound} inbound bytes produced {outbound} outbound bytes (cap {AbsoluteOutboundCap}).");
                await SaveUniqueAsync(options, $"{target}-blowup", iteration, seq[0], blowup, null, target).ConfigureAwait(false);
                Console.WriteLine($"[iter {iteration}] {target}: BLOWUP {inbound}->{outbound}");
                return 1;
            }

            if (outbound > Math.Max(AmplificationFloor, AmplificationFactor * inbound))
            {
                var amplification = new InvalidDataException(
                    $"Reply amplification: {inbound} inbound bytes produced {outbound} outbound bytes.");
                await SaveUniqueAsync(options, $"{target}-amplified", iteration, seq[0], amplification, null, target).ConfigureAwait(false);
                Console.WriteLine($"[iter {iteration}] {target}: AMPLIFIED {inbound}->{outbound}");
                return 1;
            }

            // Every harness now reports a behavior-derived signature (wire
            // bytes for the I/O targets, decoded shapes and payload lengths
            // for the pure targets), so novelty tracking covers all
            // single-input targets, not just the wire ones.
            if (coverage is not null && seq.Count == 1)
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
            if (seq.Count > 1)
            {
                if (options.NoMinimize)
                {
                    // Seed + index + --rounds regenerates the raw sequence,
                    // so saving the first round plus the replay line suffices.
                    await SaveUniqueAsync(options, target, iteration, seq[0], ex, null).ConfigureAwait(false);
                    Console.WriteLine($"[iter {iteration}] {target}: {ex.GetType().Name}: {ex.Message.Split('\n')[0]} [{seq.Count} rounds, unminimized]");
                    return 1;
                }

                var minSeq = await Minimizer.MinimizeSequenceAsync(
                    seq,
                    (probes, token) => DriveAsync(target, probes, token),
                    ex.GetType(),
                    CancellationToken.None).ConfigureAwait(false);
                await SaveSequenceAsync(options, target, iteration, minSeq, ex).ConfigureAwait(false);
                var before = seq.Sum(static s => s.Bytes.Length);
                var after = minSeq.Sum(static s => s.Bytes.Length);
                Console.WriteLine($"[iter {iteration}] {target}: {ex.GetType().Name}: {ex.Message.Split('\n')[0]} [{seq.Count}->{minSeq.Count} rounds, {before}->{after} bytes]");
                return 1;
            }

            byte[]? minimized = null;
            if (!options.NoMinimize)
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
}
