namespace telnet_cs.Client
{
  /// <summary>
  /// Line ending for <c>WriteLineAsync</c>: legacy <c>"\n"</c> (the default
  /// everywhere for backward compatibility) or RFC 854 <c>"\r\n"</c>.
  /// Prefer <c>Crlf</c> for spec-compliant peers.
  /// </summary>
  public enum LineEnding
  {
    /// <summary>Legacy <c>"\n"</c> line feed.</summary>
    Lf = 0,

    /// <summary>RFC 854 <c>"\r\n"</c> line feed.</summary>
    Crlf = 1,
  }
}
