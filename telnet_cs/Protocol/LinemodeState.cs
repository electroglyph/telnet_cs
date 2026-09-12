namespace telnet_cs.Protocol
{
    using telnet_cs.Client;
    using telnet_cs.IO;
    using telnet_cs.Server;

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

        /// <summary>
        /// The configured defaults (telnetlib3's <c>default_slc_tab</c>): written
        /// by <see cref="SetEntry"/> alongside the working <see cref="table"/>,
        /// never by negotiation. A peer <c>DEFAULT</c> level restores the row
        /// from here instead of storing <c>DEFAULT</c> verbatim.
        /// </summary>
        private readonly SlcEntry[] defaults = new SlcEntry[LinemodeProtocol.MaxFunction + 1];
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
            return ApplyModeCore(requested, union: false);
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
            return ApplyModeCore(requested, union: true);
        }

        /// <summary>
        /// Shared MODE-mask engine: unchanged masks are silent; an ACKed mask
        /// that differs is never answered by a client but adopted silently by a
        /// server (it switches); otherwise the reply combines the request with
        /// <see cref="LinemodeProtocol.SupportedModeBits"/> (intersection for a
        /// client, union for a server) plus MODE_ACK.
        /// </summary>
        /// <param name="requested">The received MODE mask byte.</param>
        /// <param name="union">Whether to union (server) rather than intersect
        /// (client) the supported bits.</param>
        /// <returns>The reply mask (with MODE_ACK set), or null for no reply.</returns>
        private byte? ApplyModeCore(byte requested, bool union)
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
                    if (!union)
                    {
                        return null;
                    }

                    mode = mask;
                    return null;
                }

                byte basis = union
                  ? (byte)(mask | LinemodeProtocol.SupportedModeBits)
                  : (byte)(requested & LinemodeProtocol.SupportedModeBits);
                byte reply = (byte)(basis | LinemodeProtocol.ModeAck);
                mode = (byte)(reply & ~LinemodeProtocol.ModeAck);
                return reply;
            }
        }

        /// <summary>
        /// Applies one inbound SLC triplet per RFC 1184 §5.5 rules 1–4:
        /// identical settings are ignored; a <c>DEFAULT</c> level restores the
        /// row from the configured defaults (telnetlib3 <c>_slc_change</c>:
        /// mask and value come from the default tab, or NOSUPPORT when the row
        /// itself is DEFAULT-level, i.e. unsupported) and replies the restored
        /// row without ACK; same-level ACKed changes switch silently;
        /// agreements switch and reply with ACK set; disagreements (only
        /// possible against a CANTCHANGE row) reply our value at our level
        /// without ACK. Func 0 (an import request) is dropped silently — the
        /// session hook answers those, never this path. Unknown functions
        /// are refused as <c>DEFAULT 0</c> so the peer may keep its own value.
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
                    // Func 0 is an import request, never a table row: the session
                    // hook answers those, so anything reaching here is dropped
                    // silently (never echo the request back). Higher codes are
                    // undefined: refuse as DEFAULT 0 so the peer keeps its value.
                    return function == 0
                      ? null
                      : (LinemodeProtocol.LevelDefault, 0);
                }

                byte level = (byte)(modifier & LinemodeProtocol.LevelBits);
                byte flags = (byte)(modifier & (LinemodeProtocol.FlagFlushIn | LinemodeProtocol.FlagFlushOut));
                if (function == LinemodeProtocol.SlcForward2
                  && table[LinemodeProtocol.SlcForward1].Level == LinemodeProtocol.LevelNoSupport)
                {
                    // RFC 1184 §5.5: FORW2 should only be used when FORW1 is already
                    // in use. Refuse a lone FORW2 as NOSUPPORT (normal-exchange rule)
                    // rather than accepting it. Stateless: the row is not stored, so a
                    // repeat gets the same answer. Triplets apply sequentially, so an
                    // in-payload FORW1 listed before FORW2 applies first.
                    return (LinemodeProtocol.LevelNoSupport, 0);
                }

                SlcEntry current = table[function];
                if (level == current.Level && value == current.Value && flags == current.Flags)
                {
                    return null;
                }

                if (level == LinemodeProtocol.LevelDefault)
                {
                    // DEFAULT is a directive ("use your default"), not a value
                    // proposal: restore from the configured defaults and reply
                    // the restored row without ACK. The inbound value byte is
                    // ignored.
                    SlcEntry configured = defaults[function];
                    table[function] = current.Level == LinemodeProtocol.LevelDefault
                      ? new SlcEntry(LinemodeProtocol.LevelNoSupport, configured.Value, 0)
                      : configured;
                    SlcEntry restored = table[function];
                    return (restored.Level, restored.Value);
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
        /// <param name="flags">The FLUSHIN/FLUSHOUT modifier bits (anything else is masked off,
        /// keeping the invariant that flags only ever hold what the wire path stores).</param>
        /// <remarks>Setup defines the defaults: the row lands in both the
        /// default tab and the working table (telnetlib3's
        /// <c>default_slc_tab</c> vs <c>slctab</c> split). Negotiation mutates
        /// only the working table, so a peer <c>DEFAULT</c> or a
        /// <see cref="ResetToDefaults"/> restores what setup configured.</remarks>
        internal void SetEntry(byte function, byte level, byte value, byte flags = 0)
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThan(function, LinemodeProtocol.MaxFunction);
            ArgumentOutOfRangeException.ThrowIfLessThan(function, (byte)1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(level, LinemodeProtocol.LevelBits);
            lock (sync)
            {
                var entry = new SlcEntry(
                  level, value, (byte)(flags & (LinemodeProtocol.FlagFlushIn | LinemodeProtocol.FlagFlushOut)));
                defaults[function] = entry;
                table[function] = entry;
            }
        }

        /// <summary>
        /// Resets the working SLC table to the configured defaults (the answer
        /// to an RFC 1184 §2.4 func 0 with <c>DEFAULT</c> import request, which
        /// also sends the full table — telnetlib3 <c>_slc_process</c>).
        /// </summary>
        internal void ResetToDefaults()
        {
            lock (sync)
            {
                Array.Copy(defaults, table, table.Length);
            }
        }

        /// <summary>
        /// Renders the configured (non-NOSUPPORT) rows as triplet tails for an
        /// SLC export, or null when nothing is configured (an all-NOSUPPORT
        /// export would wrongly tell the server to disable everything). In
        /// import mode (<paramref name="forImport"/>, the RFC 1184 §2.4 answer to
        /// func 0 with DEFAULT), every NOSUPPORT row renders as
        /// <c>[func, DEFAULT, 0]</c> instead of being omitted, so the peer may
        /// use its own values — and the result is never null.
        /// </summary>
        /// <param name="forImport">Whether this answers an import request.</param>
        /// <returns>Flat func/modifier/value bytes, or null.</returns>
        internal byte[]? ExportTriplets(bool forImport = false)
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
                    else if (forImport)
                    {
                        triplets ??= [];
                        triplets.Add(function);
                        triplets.Add(LinemodeProtocol.LevelDefault);
                        triplets.Add(0);
                    }
                }

                return triplets?.ToArray();
            }
        }
    }
}
