namespace telnet_cs.Fuzz;

/// <summary>
/// Poor-man's coverage guidance: each wire-harness run reports its outbound
/// signature (byte count plus FNV hash, sunk by <see cref="FuzzStream"/>).
/// Signatures never seen before mark the input novel, and novel inputs are
/// persisted to the corpus directory for later runs to mutate. Content —
/// not chunking — feeds the hash, so pump timing jitter cannot fake novelty.
/// Quota-capped so a pathological run cannot fill the disk.
/// </summary>
internal sealed class CoverageTracker
{
    private const int MaxSavedFiles = 2000;

    private readonly HashSet<long> seen = new();
    private readonly string dir;
    private int saved;

    public CoverageTracker(string dir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dir);
        Directory.CreateDirectory(dir);
        this.dir = dir;
    }

    public int Saved => saved;

    public void Observe(byte[] input, long outbound, long hash)
    {
        ArgumentNullException.ThrowIfNull(input);
        var key = unchecked(outbound * 31 + hash);
        if (!seen.Add(key) || saved >= MaxSavedFiles)
        {
            return;
        }

        File.WriteAllBytes(Path.Combine(dir, $"novel-{saved:000000}.bin"), input);
        saved++;
    }
}
