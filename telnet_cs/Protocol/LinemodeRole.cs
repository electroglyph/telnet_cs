namespace telnet_cs.Protocol
{
  /// <summary>
  /// Which side's LINEMODE MODE rules (RFC 1184 §2.2) a handler applies:
  /// a client intersects the mask with the supported bits, a server unions
  /// them (see <see cref="LinemodeState"/>).
  /// </summary>
  internal enum LinemodeRole
  {
    /// <summary>Client rules (the default).</summary>
    Client = 0,

    /// <summary>Server rules.</summary>
    Server = 1,
  }
}
