namespace telnet_cs.Protocol;

/// <summary>
/// One SLC table row: the agreed level, character value, and flush flags
/// for a single LINEMODE function (RFC 1184 §2.4). The default
/// <c>(0, 0, 0)</c> is <c>NOSUPPORT</c>, matching the RFC 1184 §3 default.
/// </summary>
/// <param name="Level">The agreement level (0 NOSUPPORT … 3 DEFAULT).</param>
/// <param name="Value">The character value for the function.</param>
/// <param name="Flags">The FLUSHIN/FLUSHOUT modifier bits.</param>
internal readonly record struct SlcEntry(byte Level, byte Value, byte Flags);
