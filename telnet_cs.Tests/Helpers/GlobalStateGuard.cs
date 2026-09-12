namespace telnet_cs.Tests
{
    using System;
    using telnet_cs;
    using telnet_cs.Client;
    using telnet_cs.IO;

    /// <summary>
    /// Scopes the ambient static negotiation settings a test touches. Each
    /// scope applies to the current async flow only (see <see cref="FlowLocal{T}"/>),
    /// so parallel tests stay isolated and no restore ordering is required.
    /// </summary>
    internal static class GlobalStateGuard
    {
        internal static Scope<bool> SkipProactive(bool skip) =>
          new(Client.SkipProactiveOverride, skip);

        internal static Scope<string> TerminalType(string terminalType) =>
          new(Client.TerminalTypeOverride, terminalType);

        internal static Scope<string> TerminalSpeed(string terminalSpeed) =>
          new(Client.TerminalSpeedOverride, terminalSpeed);

        internal static Scope<Action<string>?> Trace(Action<string>? hook) =>
          new(Client.TraceOverride, hook);

        internal static Scope<Action<string>?> HandlerTrace(Action<string>? hook) =>
          new(ByteStreamHandler.TraceOverride, hook);

        /// <summary>
        /// Restores the previous flow value when disposed.
        /// </summary>
        /// <typeparam name="T">The setting type.</typeparam>
        internal sealed class Scope<T> : IDisposable
        {
            private IDisposable? token;

            public Scope(FlowLocal<T> flow, T? value)
            {
                token = flow.Override(value);
            }

            public void Dispose()
            {
                token?.Dispose();
                token = null;
            }
        }
    }
}
