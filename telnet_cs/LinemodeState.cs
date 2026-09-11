namespace telnet_cs
{
  /// <summary>
  /// One SLC table row: the agreed level, character value, and flush flags
  /// for a single LINEMODE function (RFC 1184 §2.4). The default
  /// <c>(0, 0, 0)</c> is <c>NOSUPPORT</c>, matching the RFC 1184 §3 default.
  /// </summary>
  /// <param name="Level">The agreement level (0 NOSUPPORT … 3 DEFAULT).</param>
  /// <param name="Value">The character value for the function.</param>
  /// <param name="Flags">The FLUSHIN/FLUSHOUT modifier bits.</param>
  internal readonly record struct SlcEntry(byte Level, byte Value, byte Flags);

  /// <summary>
  /// Client-side LINEMODE state (RFC 1184): the agreed MODE mask plus the
  /// SLC special-character table. One instance is owned by the
  /// <see cref="Client"/> or the <see cref="ServerSession"/> and shared
  /// with each per-read <see cref="ByteStreamHandler"/> (all mutation is
  /// lock-guarded), so negotiation memory survives across reads.
  /// </summary>
  internal sealed class LinemodeState
  {
    private readonly Lock sync = new();
    private readonly SlcEntry[] table = new SlcEntry[LinemodeProtocol.MaxFunction + 1];
    private byte mode;

    /// <summary>Gets the agreed MODE mask, without the MODE_ACK bit.</summary>
    internal byte Mode
    {
      get
      {
        lock (sync)
        {
          return mode;
        }
      }
    }

    /// <summary>
    /// Applies an inbound MODE mask per the client rules (RFC 1184 §2.2).
    /// A MODE with MODE_ACK set is never answered; an unchanged mask is
    /// ignored; otherwise the reply is the requested mask plus MODE_ACK,
    /// reduced to <see cref="LinemodeProtocol.SupportedModeBits"/> (a
    /// spec-legal subset — EDIT/TRAPSIG are never cleared).
    /// </summary>
    /// <param name="requested">The received MODE mask byte.</param>
    /// <returns>The reply mask (with MODE_ACK set), or null for no reply.</returns>
    internal byte? ApplyMode(byte requested)
    {
      lock (sync)
      {
        if ((requested & LinemodeProtocol.ModeAck) != 0)
        {
          return null;
        }

        if ((byte)(requested & ~LinemodeProtocol.ModeAck) == mode)
        {
          return null;
        }

        byte reply = (byte)((requested & LinemodeProtocol.SupportedModeBits) | LinemodeProtocol.ModeAck);
        mode = (byte)(reply & ~LinemodeProtocol.ModeAck);
        return reply;
      }
    }

    /// <summary>
    /// Applies an inbound MODE mask per the server rules (RFC 1184 §2.2):
    /// the server may set EDIT/TRAPSIG and the client may not clear them,
    /// so the reply is the requested mask <em>plus</em>
    /// <see cref="LinemodeProtocol.SupportedModeBits"/> (union, not the
    /// client's intersection), with MODE_ACK set. An ACKed mask that
    /// differs is adopted silently (the server switches); anything already
    /// agreed is ignored.
    /// </summary>
    /// <param name="requested">The received MODE mask byte.</param>
    /// <returns>The reply mask (with MODE_ACK set), or null for no reply.</returns>
    internal byte? ApplyModeAsServer(byte requested)
    {
      lock (sync)
      {
        byte mask = (byte)(requested & ~LinemodeProtocol.ModeAck);
        if (mask == mode)
        {
          return null;
        }

        if ((requested & LinemodeProtocol.ModeAck) != 0)
        {
          mode = mask;
          return null;
        }

        byte reply = (byte)((mask | LinemodeProtocol.SupportedModeBits) | LinemodeProtocol.ModeAck);
        mode = (byte)(reply & ~LinemodeProtocol.ModeAck);
        return reply;
      }
    }

    /// <summary>
    /// Applies one inbound SLC triplet per RFC 1184 §5.5 rules 1–4:
    /// identical settings are ignored; same-level ACKed changes switch
    /// silently; agreements switch and reply with ACK set; disagreements
    /// (only possible against a CANTCHANGE row) reply our value at our
    /// level without ACK. Unknown functions are refused as
    /// <c>DEFAULT 0</c> so the peer may keep its own value.
    /// </summary>
    /// <param name="function">The SLC function code.</param>
    /// <param name="modifier">The level plus ACK/FLUSH modifier bits.</param>
    /// <param name="value">The proposed character value.</param>
    /// <returns>The reply (modifier, value) triplet tail, or null for no reply.</returns>
    internal (byte Modifier, byte Value)? ApplySlc(byte function, byte modifier, byte value)
    {
      lock (sync)
      {
        if (function == 0 || function > LinemodeProtocol.MaxFunction)
        {
          // Func 0 may only be sent by the client; higher codes are
          // undefined. Refuse as DEFAULT 0 so the peer keeps its value.
          return (LinemodeProtocol.LevelDefault, 0);
        }

        byte level = (byte)(modifier & LinemodeProtocol.LevelBits);
        byte flags = (byte)(modifier & (LinemodeProtocol.FlagFlushIn | LinemodeProtocol.FlagFlushOut));
        SlcEntry current = table[function];
        if (level == current.Level && value == current.Value && flags == current.Flags)
        {
          return null;
        }

        if (level == current.Level && (modifier & LinemodeProtocol.FlagAck) != 0)
        {
          table[function] = new SlcEntry(level, value, flags);
          return null;
        }

        if (current.Level == LinemodeProtocol.LevelCantChange)
        {
          return (LinemodeProtocol.LevelCantChange, current.Value);
        }

        table[function] = new SlcEntry(level, value, flags);
        return ((byte)(level | LinemodeProtocol.FlagAck), value);
      }
    }

    /// <summary>Gets one SLC table row (test/setup hook).</summary>
    /// <param name="function">The SLC function code (1–30).</param>
    /// <returns>The stored entry; out-of-range codes yield NOSUPPORT.</returns>
    internal SlcEntry GetEntry(byte function)
    {
      lock (sync)
      {
        return function >= 1 && function <= LinemodeProtocol.MaxFunction ? table[function] : default;
      }
    }

    /// <summary>Sets one SLC table row (test/setup hook).</summary>
    /// <param name="function">The SLC function code (1–30).</param>
    /// <param name="level">The agreement level.</param>
    /// <param name="value">The character value.</param>
    internal void SetEntry(byte function, byte level, byte value)
    {
      ArgumentOutOfRangeException.ThrowIfGreaterThan(function, LinemodeProtocol.MaxFunction);
      ArgumentOutOfRangeException.ThrowIfLessThan(function, (byte)1);
      ArgumentOutOfRangeException.ThrowIfGreaterThan(level, LinemodeProtocol.LevelBits);
      lock (sync)
      {
        table[function] = new SlcEntry(level, value, 0);
      }
    }

    /// <summary>
    /// Renders the configured (non-NOSUPPORT) rows as triplet tails for an
    /// SLC export, or null when nothing is configured (an all-NOSUPPORT
    /// export would wrongly tell the server to disable everything).
    /// </summary>
    /// <returns>Flat func/modifier/value bytes, or null.</returns>
    internal byte[]? ExportTriplets()
    {
      lock (sync)
      {
        List<byte>? triplets = null;
        for (byte function = 1; function <= LinemodeProtocol.MaxFunction; function++)
        {
          SlcEntry entry = table[function];
          if (entry.Level != LinemodeProtocol.LevelNoSupport)
          {
            triplets ??= [];
            triplets.Add(function);
            triplets.Add(entry.Level);
            triplets.Add(entry.Value);
          }
        }

        return triplets?.ToArray();
      }
    }
  }
}
