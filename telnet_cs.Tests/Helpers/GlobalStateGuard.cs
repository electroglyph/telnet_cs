namespace telnet_cs.Tests
{
    using System;
    using telnet_cs.Client;
    using telnet_cs.IO;

    /// <summary>
    /// Scopes the process-wide static negotiation settings a test touches.
    /// The suite runs serially (see <c>AssemblyInfo</c>), so save/set/restore
    /// per test is sufficient isolation.
    /// </summary>
    internal static class GlobalStateGuard
    {
        internal static Scope<bool> SkipProactive(bool skip) =>
          new(() => Client.SkipProactiveOptionNegotiation, v => Client.SkipProactiveOptionNegotiation = v, skip);

        internal static Scope<string> TerminalType(string terminalType) =>
          new(() => Client.TerminalType, v => Client.TerminalType = v, terminalType);

        internal static Scope<string> TerminalSpeed(string terminalSpeed) =>
          new(() => Client.TerminalSpeed, v => Client.TerminalSpeed = v, terminalSpeed);

        internal static Scope<Action<string>?> Trace(Action<string>? hook) =>
          new(() => Client.Trace, v => Client.Trace = v, hook);

        internal static Scope<Action<string>?> HandlerTrace(Action<string>? hook) =>
          new(() => ByteStreamHandler.Trace, v => ByteStreamHandler.Trace = v, hook);

        /// <summary>
        /// Restores the captured setting when disposed.
        /// </summary>
        /// <typeparam name="T">The setting type.</typeparam>
        internal sealed class Scope<T> : IDisposable
        {
            private readonly Action<T> set;
            private readonly T previous;
            private bool disposed;

            public Scope(Func<T> get, Action<T> set, T value)
            {
                previous = get();
                this.set = set;
                set(value);
            }

            public void Dispose()
            {
                if (!disposed)
                {
                    disposed = true;
                    set(previous);
                }
            }
        }
    }
}
