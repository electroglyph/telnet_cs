namespace telnet_cs
{
  using System;
  using System.Collections.Generic;
  using System.Text;

  /// <summary>
  /// Per-instance client settings. Every member is optional: null (or zero
  /// for the window size) falls back to the corresponding static on
  /// <see cref="Client"/> (or the documented default), so existing code that
  /// only touches the statics behaves exactly as before.
  /// </summary>
  public class TelnetClientOptions
  {
    /// <summary>
    /// Gets or sets the terminal type to negotiate. Null follows <see cref="Client.TerminalType"/>.
    /// </summary>
    public string? TerminalType { get; set; }

    /// <summary>
    /// Ordered terminal-type list (most to least specific) for RFC 1091
    /// cycling: successive SENDs walk this list instead of repeating
    /// <see cref="TerminalType"/>. Empty (the default) disables cycling.
    /// </summary>
    public IList<string> TerminalTypes { get; } = [];

    /// <summary>
    /// Gets or sets the terminal speed to negotiate. Null follows <see cref="Client.TerminalSpeed"/>.
    /// </summary>
    public string? TerminalSpeed { get; set; }

    /// <summary>
    /// Gets or sets whether text read is echoed to the console. Null follows <see cref="Client.IsWriteConsole"/>.
    /// </summary>
    public bool? IsWriteConsole { get; set; }

    /// <summary>
    /// Opts in to performing remote echo (RFC 857): a server <c>DO ECHO</c>
    /// is answered with <c>WILL</c> and received data is echoed back, unless
    /// the peer is already echoing (the infinite-bounce hazard). False (the
    /// default) answers <c>DO ECHO</c> with <c>WONT</c>.
    /// </summary>
    public bool AllowRemoteEcho { get; set; }

    /// <summary>
    /// Gets or sets whether a received BEL rings the console bell. Null means enabled.
    /// </summary>
    public bool? EnableBell { get; set; }

    /// <summary>
    /// Gets or sets the encoding for outbound strings and inbound decoding.
    /// Null keeps the legacy Latin-1 mapping.
    /// </summary>
    public Encoding? TextEncoding { get; set; }

    /// <summary>
    /// Gets or sets the terminal width reported via NAWS. Zero means auto-detect.
    /// </summary>
    public int WindowWidth { get; set; }

    /// <summary>
    /// Gets or sets the terminal height reported via NAWS. Zero means auto-detect.
    /// </summary>
    public int WindowHeight { get; set; }

    /// <summary>
    /// Optional handler invoked with protocol log messages. Null (the default) disables logging.
    /// </summary>
    public Action<string>? Log { get; set; }

    /// <summary>
    /// Value reported for the well-known <c>USER</c> variable in RFC 1408 ENVIRON responses.
    /// Null (the default) omits <c>USER</c> unless explicitly requested.
    /// </summary>
    public string? EnvironmentUser { get; set; }

    /// <summary>
    /// Value reported for the well-known <c>DISPLAY</c> variable in RFC 1408 ENVIRON responses.
    /// Null (the default) omits <c>DISPLAY</c> unless explicitly requested.
    /// </summary>
    public string? EnvironmentDisplay { get; set; }

    /// <summary>
    /// User-defined variables reported as <c>USERVAR</c> entries in RFC 1408 ENVIRON responses.
    /// Empty by default.
    /// </summary>
    public Dictionary<string, string> EnvironmentUserVars { get; } = new(StringComparer.Ordinal);
  }
}
