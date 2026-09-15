namespace telnet_cs.Fuzz;

/// <summary>
/// Standalone fuzz entry point. Iterates the deterministic input stream,
/// drives the selected harnesses under a per-iteration hang guard, saves a
/// repro artifact per unexpected exception, and exits 1 when any crash was
/// found. Never referenced by the test suite; runs only via
/// <c>dotnet run --project telnet_cs.Fuzz</c>.
/// </summary>
internal static class Program
{
    private static readonly Dictionary<string, int> SeenKinds = new(StringComparer.Ordinal);

    private const int MaxSavedPerKind = 3;

    internal static async Task<int> Main(string[] args)
    {
        if (!FuzzOptions.TryParse(args, out var options, out var error))
        {
            Console.WriteLine(string.IsNullOrEmpty(error) ? FuzzOptions.Usage : error);
            return string.IsNullOrEmpty(error) ? 0 : 2;
        }

        ArgumentNullException.ThrowIfNull(options);
        var crashes = 0;
        for (var i = 0; i < options.Iters; i++)
        {
            var input = Mutator.Generate(options.Seed, i, options.MaxBytes);
            crashes += await RunOneAsync(options, i, input, "parser", RunParserWhenSelected, CancellationToken.None).ConfigureAwait(false);
            crashes += await RunOneAsync(options, i, input, "session", RunSessionWhenSelected, CancellationToken.None).ConfigureAwait(false);
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

        return crashes == 0 ? 0 : 1;
    }

    private static async Task SaveUniqueAsync(
        FuzzOptions options,
        string target,
        int iteration,
        FuzzInput input,
        Exception ex)
    {
        var kind = $"{target}:{ex.GetType().FullName}";
        SeenKinds.TryGetValue(kind, out var count);
        count++;
        SeenKinds[kind] = count;
        if (count <= MaxSavedPerKind)
        {
            await CrashReporter.SaveAsync(
                options.OutDir, target, options.Seed, iteration, input, ex,
                CancellationToken.None, count > 1 ? $"-k{count}" : null).ConfigureAwait(false);
        }
    }

    private static Task RunParserWhenSelected(FuzzInput input, CancellationToken token) =>
        ParserHarness.RunAsync(input, token);

    private static Task RunSessionWhenSelected(FuzzInput input, CancellationToken token) =>
        SessionHarness.RunAsync(input, token);

    private static async Task<int> RunOneAsync(
        FuzzOptions options,
        int iteration,
        FuzzInput input,
        string target,
        Func<FuzzInput, CancellationToken, Task> drive,
        CancellationToken cancellationToken)
    {
        if ((target == "parser" && options.Mode == FuzzMode.Session) ||
            (target == "session" && options.Mode == FuzzMode.Parser))
        {
            return 0;
        }

        using var guard = new CancellationTokenSource(TimeSpan.FromMilliseconds(options.PerIterTimeoutMs));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, guard.Token);
        try
        {
            await drive(input, linked.Token).ConfigureAwait(false);
            return 0;
        }
        catch (Exception ex) when (guard.IsCancellationRequested && ex is not OperationCanceledException)
        {
            var timeout = new TimeoutException($"No result within {options.PerIterTimeoutMs} ms: {ex.GetType().Name}.");
            await SaveUniqueAsync(options, $"{target}-timeout", iteration, input, timeout).ConfigureAwait(false);
            Console.WriteLine($"[iter {iteration}] {target}: TIMEOUT ({ex.GetType().Name})");
            return 1;
        }
        catch (OperationCanceledException) when (guard.IsCancellationRequested)
        {
            var timeout = new TimeoutException($"No result within {options.PerIterTimeoutMs} ms.");
            await SaveUniqueAsync(options, $"{target}-timeout", iteration, input, timeout).ConfigureAwait(false);
            Console.WriteLine($"[iter {iteration}] {target}: TIMEOUT");
            return 1;
        }
        catch (Exception ex) when (CrashReporter.IsBenign(ex))
        {
            return 0;
        }
        catch (Exception ex)
        {
            await SaveUniqueAsync(options, target, iteration, input, ex).ConfigureAwait(false);
            Console.WriteLine($"[iter {iteration}] {target}: {ex.GetType().Name}: {ex.Message.Split('\n')[0]}");
            return 1;
        }
    }
}
