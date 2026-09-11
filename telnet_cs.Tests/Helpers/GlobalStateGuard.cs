namespace telnet_cs.Tests
{
  using System;
  using telnet_cs.Client;
  using telnet_cs.IO;

  /// <summary>
  /// Saves process-wide telnet statics on creation and restores them on
  /// dispose, so tests pinning static-fallback behavior cannot leak settings
  /// into each other while assembly-wide parallelization stays off.
  /// </summary>
  internal sealed class GlobalStateGuard : IDisposable
  {
    private readonly Action restore;

    private GlobalStateGuard(Action restore)
    {
      this.restore = restore;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
      restore();
    }

    /// <summary>
    /// Sets <c>SkipProactiveOptionNegotiation</c> for the guarded block.
    /// </summary>
    /// <param name="value">The value to hold.</param>
    public static GlobalStateGuard SkipProactive(bool value)
    {
      var prior = Client.SkipProactiveOptionNegotiation;
      Client.SkipProactiveOptionNegotiation = value;
      return new GlobalStateGuard(() => Client.SkipProactiveOptionNegotiation = prior);
    }

    /// <summary>
    /// Sets the fallback <c>TerminalType</c> for the guarded block.
    /// </summary>
    /// <param name="value">The value to hold.</param>
    public static GlobalStateGuard TerminalType(string value)
    {
      var prior = Client.TerminalType;
      Client.TerminalType = value;
      return new GlobalStateGuard(() => Client.TerminalType = prior);
    }

    /// <summary>
    /// Sets the fallback <c>TerminalSpeed</c> for the guarded block.
    /// </summary>
    /// <param name="value">The value to hold.</param>
    public static GlobalStateGuard TerminalSpeed(string value)
    {
      var prior = Client.TerminalSpeed;
      Client.TerminalSpeed = value;
      return new GlobalStateGuard(() => Client.TerminalSpeed = prior);
    }

    /// <summary>
    /// Sets the client <c>Trace</c> hook for the guarded block.
    /// </summary>
    /// <param name="value">The value to hold.</param>
    public static GlobalStateGuard Trace(Action<string>? value)
    {
      var prior = Client.Trace;
      Client.Trace = value;
      return new GlobalStateGuard(() => Client.Trace = prior);
    }

    /// <summary>
    /// Sets the handler <c>Trace</c> hook for the guarded block.
    /// </summary>
    /// <param name="value">The value to hold.</param>
    public static GlobalStateGuard HandlerTrace(Action<string>? value)
    {
      var prior = ByteStreamHandler.Trace;
      ByteStreamHandler.Trace = value;
      return new GlobalStateGuard(() => ByteStreamHandler.Trace = prior);
    }
  }
}
