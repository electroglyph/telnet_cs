namespace telnet_cs.Fuzz;

/// <summary>
/// Ambient one-shot transport-fault switch for <c>--faults</c> runs. The
/// iteration driver arms a read or write fault for every 8th iteration; the
/// next <see cref="FuzzStream"/> constructed on that async flow picks it up
/// and throws a single <see cref="IOException"/> from the matching direction.
/// <see cref="AsyncLocal{T}"/> keeps arming deterministic per (seed, index)
/// even with <c>--jobs</c> parallelism, and keeps minimizer probes fault-free
/// (probes run on a fresh flow, so faults never explain a repro).
/// <see cref="CrashReporter.IsBenign"/> treats the injected
/// <see cref="IOException"/> itself as uninteresting: only exceptions the
/// fault handling itself provokes count as findings.
/// </summary>
internal static class FuzzFaults
{
    private static readonly AsyncLocal<bool> ReadArmed = new();
    private static readonly AsyncLocal<bool> WriteArmed = new();

    /// <summary>
    /// Arms a fault for this iteration when <paramref name="enabled"/>:
    /// every 8th iteration faults, alternating read and write. Deterministic
    /// in the iteration index, so replays reproduce the same fault pattern.
    /// </summary>
    public static void ArmForIteration(bool enabled, int iteration)
    {
        ReadArmed.Value = enabled && iteration % 8 == 7 && (iteration / 8) % 2 == 0;
        WriteArmed.Value = enabled && iteration % 8 == 7 && (iteration / 8) % 2 == 1;
    }

    internal static bool TakeRead()
    {
        var armed = ReadArmed.Value;
        ReadArmed.Value = false;
        return armed;
    }

    internal static bool TakeWrite()
    {
        var armed = WriteArmed.Value;
        WriteArmed.Value = false;
        return armed;
    }
}
