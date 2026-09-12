namespace telnet_cs;

/// <summary>
/// Flow-scoped override for an ambient static setting. Reads on a flow with
/// an active override see the override; every other flow sees the shared
/// default. Lets parallel tests isolate settings that production code reads
/// as statics, without changing single-flow behavior.
/// </summary>
/// <typeparam name="T">The setting type.</typeparam>
internal sealed class FlowLocal<T>
{
    private readonly AsyncLocal<(bool HasValue, T? Value)?> state = new();

    /// <summary>
    /// The override when one is active on this flow, else <paramref name="defaultValue"/>.
    /// </summary>
    public T? CurrentOr(T? defaultValue) =>
        state.Value is { HasValue: true } held ? held.Value : defaultValue;

    /// <summary>
    /// Activates <paramref name="value"/> on the current flow until disposed.
    /// Nesting restores the outer value.
    /// </summary>
    public IDisposable Override(T? value)
    {
        var previous = state.Value;
        state.Value = (true, value);
        return new Restorer(state, previous);
    }

    private sealed class Restorer(AsyncLocal<(bool HasValue, T? Value)?> slot, (bool HasValue, T? Value)? previous) : IDisposable
    {
        private bool disposed;

        public void Dispose()
        {
            if (!disposed)
            {
                disposed = true;
                slot.Value = previous;
            }
        }
    }
}
