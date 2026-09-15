namespace telnet_cs.Fuzz;

using System.Text;

/// <summary>
/// Crash oracle and artifact writer. Any exception other than cancellation,
/// disposal, or I/O timeout is unexpected: the input is saved for triage and
/// the run keeps going. Artifacts replay deterministically via seed + index.
/// </summary>
internal static class CrashReporter
{
    public static bool IsBenign(Exception ex) => ex switch
    {
        OperationCanceledException => true,
        ObjectDisposedException => true,
        System.IO.IOException => true,
        _ => false,
    };

    public static async Task SaveAsync(
        string outDir,
        string target,
        int seed,
        int iteration,
        FuzzInput input,
        Exception ex,
        CancellationToken cancellationToken,
        string? suffix = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outDir);
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ex);
        Directory.CreateDirectory(outDir);
        var stem = Path.Combine(outDir, $"{target}-s{seed}-i{iteration}{suffix ?? string.Empty}");
        await File.WriteAllBytesAsync(stem + ".bin", input.Bytes, cancellationToken).ConfigureAwait(false);
        var report = new StringBuilder()
            .AppendLine($"target: {target}")
            .AppendLine($"seed: {seed}")
            .AppendLine($"iteration: {iteration}")
            .AppendLine($"bytes: {input.Bytes.Length}")
            .AppendLine($"splits: {(input.Splits.Length == 0 ? "-" : string.Join(",", input.Splits))}")
            .AppendLine($"exception: {ex.GetType().FullName}: {ex.Message}")
            .AppendLine("stack:")
            .AppendLine(ex.StackTrace ?? "<none>")
            .AppendLine("hex:")
            .AppendLine(Convert.ToHexString(input.Bytes))
            .AppendLine($"replay: dotnet run --project telnet_cs.Fuzz -c Release -- --seed {seed} --iters {iteration + 1} --mode {target}")
            .ToString();
        await File.WriteAllTextAsync(stem + ".txt", report, cancellationToken).ConfigureAwait(false);
    }
}
