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

    public static string StackKey(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        var trace = ex.StackTrace;
        if (string.IsNullOrEmpty(trace))
        {
            return "nostack";
        }

        var frames = new List<string>();
        foreach (var line in trace.Split('\n'))
        {
            var at = line.IndexOf(" at ", StringComparison.Ordinal);
            if (at < 0)
            {
                continue;
            }

            var frame = line[(at + 4)..].Trim();
            var paren = frame.IndexOf('(');
            if (paren > 0)
            {
                frame = frame[..paren];
            }

            if (frame.Contains("telnet_cs.Fuzz.", StringComparison.Ordinal))
            {
                continue;
            }

            frames.Add(frame);
            if (frames.Count == 3)
            {
                break;
            }
        }

        if (frames.Count == 0)
        {
            return "noframe";
        }

        var hash = unchecked(1469598103934665603UL);
        foreach (var c in string.Join("|", frames))
        {
            hash ^= c;
            hash *= 1099511628211UL;
        }

        return hash.ToString("x16", System.Globalization.CultureInfo.InvariantCulture);
    }

    public static async Task SaveAsync(
        string outDir,
        string target,
        int seed,
        int iteration,
        FuzzInput input,
        Exception ex,
        CancellationToken cancellationToken,
        string? suffix = null,
        byte[]? minimized = null,
        string? extraArgs = null,
        string? replayMode = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outDir);
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ex);
        Directory.CreateDirectory(outDir);
        var stem = Path.Combine(outDir, $"{target}-s{seed}-i{iteration}{suffix ?? string.Empty}");
        await File.WriteAllBytesAsync(stem + ".bin", input.Bytes, cancellationToken).ConfigureAwait(false);
        if (minimized is not null)
        {
            await File.WriteAllBytesAsync(stem + ".min.bin", minimized, cancellationToken).ConfigureAwait(false);
        }

        var report = new StringBuilder()
            .AppendLine($"target: {target}")
            .AppendLine($"seed: {seed}")
            .AppendLine($"iteration: {iteration}")
            .AppendLine($"bytes: {input.Bytes.Length}")
            .AppendLine($"minimized_bytes: {(minimized is null ? "-" : minimized.Length.ToString(System.Globalization.CultureInfo.InvariantCulture))}")
            .AppendLine($"splits: {(input.Splits.Length == 0 ? "-" : string.Join(",", input.Splits))}")
            .AppendLine($"exception: {ex.GetType().FullName}: {ex.Message}")
            .AppendLine("stack:")
            .AppendLine(ex.StackTrace ?? "<none>")
            .AppendLine("hex:")
            .AppendLine(Convert.ToHexString(input.Bytes))
            .AppendLine($"replay: dotnet run --project telnet_cs.Fuzz -c Release -- --seed {seed} --iters {iteration + 1} --mode {replayMode ?? target}{extraArgs ?? string.Empty}")
            .ToString();
        await File.WriteAllTextAsync(stem + ".txt", report, cancellationToken).ConfigureAwait(false);
    }
}
