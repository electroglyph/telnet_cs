namespace telnet_cs.Fuzz;

/// <summary>
/// Crash-path-only delta debugger: shrinks a crashing input while it still
/// reproduces the same exception type through the same harness. Segment
/// drops first (split-defined ranges of the original), then ddmin-style
/// halving on the byte array. Candidates run split-free and under a short
/// probe budget, so minimization stays proportional to triage value.
/// Conservative throughout: timeouts and benign outcomes never count as
/// reproduction, and a failed minimization simply keeps the original.
/// </summary>
internal static class Minimizer
{
    private const int MaxProbes = 80;
    private const int ProbeTimeoutMs = 3000;

    public static async Task<FuzzInput> MinimizeAsync(
        FuzzInput input,
        Func<FuzzInput, CancellationToken, Task<(long Outbound, long Hash)>> probe,
        Type exceptionType,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(exceptionType);
        var probes = new ProbeCount();
        var current = await DropSegmentsAsync(input, probe, exceptionType, cancellationToken, probes).ConfigureAwait(false);
        var bytes = (byte[])current.Bytes.Clone();
        var n = 2;
        while (bytes.Length > 1 && probes.Used < MaxProbes)
        {
            var chunk = Math.Max(1, bytes.Length / n);
            var reduced = false;
            for (var start = 0; start < bytes.Length && probes.Used < MaxProbes; start += chunk)
            {
                var end = Math.Min(start + chunk, bytes.Length);
                var candidate = bytes[..start].Concat(bytes[end..]).ToArray();
                probes.Used++;
                if (candidate.Length != 0 && await ReproducesAsync(candidate, probe, exceptionType, cancellationToken).ConfigureAwait(false))
                {
                    bytes = candidate;
                    n = Math.Max(2, n - 1);
                    reduced = true;
                    break;
                }
            }

            if (!reduced)
            {
                if (n >= bytes.Length)
                {
                    break;
                }

                n = Math.Min(bytes.Length, n * 2);
            }
        }

        return new FuzzInput(bytes, []);
    }

    private static async Task<FuzzInput> DropSegmentsAsync(
        FuzzInput input,
        Func<FuzzInput, CancellationToken, Task<(long Outbound, long Hash)>> probe,
        Type exceptionType,
        CancellationToken cancellationToken,
        ProbeCount probes)
    {
        List<int> boundaries = [0, .. input.Splits, input.Bytes.Length];
        var keep = new bool[boundaries.Count - 1];
        Array.Fill(keep, true);
        var changed = true;
        while (changed)
        {
            changed = false;
            for (var s = 0; s < keep.Length && probes.Used < MaxProbes; s++)
            {
                if (!keep[s])
                {
                    continue;
                }

                keep[s] = false;
                var candidate = Assemble(input.Bytes, boundaries, keep);
                probes.Used++;
                if (candidate.Length != 0 && await ReproducesAsync(candidate, probe, exceptionType, cancellationToken).ConfigureAwait(false))
                {
                    changed = true;
                }
                else
                {
                    keep[s] = true;
                }
            }
        }

        return new FuzzInput(Assemble(input.Bytes, boundaries, keep), []);
    }

    private static byte[] Assemble(byte[] bytes, List<int> boundaries, bool[] keep)
    {
        var buf = new List<byte>();
        for (var s = 0; s < keep.Length; s++)
        {
            if (keep[s])
            {
                buf.AddRange(bytes.AsSpan(boundaries[s], boundaries[s + 1] - boundaries[s]).ToArray());
            }
        }

        return [.. buf];
    }

    private static async Task<bool> ReproducesAsync(
        byte[] candidate,
        Func<FuzzInput, CancellationToken, Task<(long Outbound, long Hash)>> probe,
        Type exceptionType,
        CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(ProbeTimeoutMs));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            await probe(new FuzzInput(candidate, []), linked.Token).ConfigureAwait(false);
            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex) when (CrashReporter.IsBenign(ex))
        {
            return false;
        }
        catch (Exception ex)
        {
            return exceptionType.IsInstanceOfType(ex);
        }
    }

    private sealed class ProbeCount
    {
        public int Used { get; set; }
    }
}
