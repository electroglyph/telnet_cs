namespace telnet_cs.Fuzz;

using telnet_cs.Client;

/// <summary>
/// Unit-level target: drives the client input state machines directly.
/// <see cref="InputFilter"/> (ATASCII/PETSCII keymaps, ESC-delay hold-back)
/// gets the bytes in split-sized chunks plus a flush; a
/// <see cref="LinemodeBuffer"/> gets them as characters with periodic
/// flushes. Synchronous; oracle: no throw.
/// </summary>
internal static class InputHarness
{
    public static Task<(long Outbound, long Hash)> RunAsync(FuzzInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        RunFilter(input, cancellationToken);
        RunBuffer(input, cancellationToken);
        return Task.FromResult((0L, 0L));
    }

    private static void RunFilter(FuzzInput input, CancellationToken cancellationToken)
    {
        // Zero escape delay: the 350 ms default would stall every iteration
        // holding a trailing ESC.
        var filter = input.Bytes.Length % 2 == 0
            ? InputFilter.CreateAtascii(TimeSpan.Zero)
            : InputFilter.CreatePetscii(TimeSpan.Zero);
        foreach (var (start, length) in Feed.Chunks(input))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (length != 0)
            {
                _ = filter.Feed(input.Bytes.AsSpan(start, length));
            }
        }

        _ = filter.Flush();
    }

    private static void RunBuffer(FuzzInput input, CancellationToken cancellationToken)
    {
        var buffer = new LinemodeBuffer(trapSignal: input.Bytes.Length % 2 == 0);
        var fed = 0;
        foreach (var b in input.Bytes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = buffer.Feed((char)b);
            if (++fed % 64 == 0)
            {
                _ = buffer.Flush();
            }
        }

        _ = buffer.Flush();
        buffer.Clear();
    }
}
