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
        /// The BSD default SLC rows (telnetlib3's <c>default_slc_tab</c> via
        /// <c>generate_slctab</c>): 16 live functions as (function, level,
        /// value, flush flags). Both the working table and the configured
        /// defaults start here, so a fresh session already negotiates the
        /// reference values; <see cref="SetEntry"/> overrides per row.
        /// </summary>
        private static readonly (byte Function, byte Level, byte Value, byte Flags)[] BsdDefaults =
        [
            (1, 3, 0, 0),
            (2, 3, 0, 0),
            (3, 2, 3, 96),
            (4, 2, 15, 32),
            (5, 2, 20, 0),
            (6, 3, 0, 0),
            (7, 2, 28, 96),
            (8, 2, 4, 0),
            (9, 2, 26, 64),
            (10, 2, 127, 0),
            (11, 2, 21, 0),
            (12, 2, 23, 0),
            (13, 2, 18, 0),
            (14, 2, 22, 0),
            (15, 2, 17, 0),
            (16, 2, 19, 0),
        ];

        internal LinemodeState()
        {
            // Functions without a BSD default start as NOSUPPORT with the
            // disable value (reference SLC_nosupport=(NOSUPPORT, 0xFF)), so a
            // peer proposal for one still counts as a valued row.
            for (byte function = 1; function <= LinemodeProtocol.MaxFunction; function++)
            {
                defaults[function] = new SlcEntry(LinemodeProtocol.LevelNoSupport, 255, 0);
                table[function] = new SlcEntry(LinemodeProtocol.LevelNoSupport, 255, 0);
            }

            foreach (var (function, level, value, flags) in BsdDefaults)
            {
                var entry = new SlcEntry(level, value, flags);
                defaults[function] = entry;
                table[function] = entry;
            }
        }

        /// <summary>
        /// The configured defaults (telnetlib3's <c>default_slc_tab</c>): written
        /// by <see cref="SetEntry"/> alongside the working <see cref="table"/>,
        /// never by negotiation. A peer <c>DEFAULT</c> level restores the row
        /// from here instead of storing <c>DEFAULT</c> verbatim.
        /// </summary>
        private readonly SlcEntry[] defaults = new SlcEntry[LinemodeProtocol.MaxFunction + 1];
        private byte mode;
        private byte[]? forwardMask;
        private bool forwardMaskOffered;
        private bool forwardMaskAccepted;
        private bool slcPublished;

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
        /// Gets the last agreed FORWARDMASK bytes (RFC 1184 §2.3), or null when
        /// no well-formed <c>DO FORWARDMASK</c> has arrived. A defensive copy:
        /// callers never alias the stored buffer.
        /// </summary>
        internal byte[]? ForwardMask
        {
            get
            {
                lock (sync)
                {
                    return forwardMask is null ? null : (byte[])forwardMask.Clone();
                }
            }
        }

        /// <summary>
        /// Gets whether the last FORWARDMASK verb was a <c>DO</c> (local
        /// sub-state: the reference marks <c>local SB+FORWARDMASK</c> on DO,
        /// clears it on DONT).
        /// </summary>
        internal bool ForwardMaskOffered
        {
            get
            {
                lock (sync)
                {
                    return forwardMaskOffered;
                }
            }
        }

        /// <summary>
        /// Gets whether the last FORWARDMASK verb was a <c>WILL</c> (remote
        /// sub-state: the reference marks <c>remote SB+FORWARDMASK</c> on WILL,
        /// clears it on WONT).
        /// </summary>
        internal bool ForwardMaskAccepted
        {
            get
            {
                lock (sync)
                {
                    return forwardMaskAccepted;
                }
            }
        }

        /// <summary>
        /// Applies an inbound <c>DO FORWARDMASK mask…</c> (RFC 1184 §2.3):
        /// records the local sub-state and stores the mask. Only 1–32 mask
        /// bytes are stored (the reference warns on any other length); the
        /// sub-state is still recorded for overlong masks.
        /// </summary>
        /// <param name="mask">The mask bytes following the FORWARDMASK verb.</param>
        /// <returns>Whether the mask was stored.</returns>
        internal bool ApplyForwardMaskOffer(byte[] mask)
        {
            ArgumentNullException.ThrowIfNull(mask);
            lock (sync)
            {
                forwardMaskOffered = true;
                if (mask.Length is < 1 or > 32)
                {
                    return false;
                }

                forwardMask = (byte[])mask.Clone();
                return true;
            }
        }

        /// <summary>
        /// Applies an inbound <c>DONT FORWARDMASK</c>: clears the local
        /// sub-state. Stored bytes are kept (the reference only clears the
        /// sub-state flag, never the stored mask).
        /// </summary>
        internal void ApplyForwardMaskRefusal()
        {
            lock (sync)
            {
                forwardMaskOffered = false;
            }
        }

        /// <summary>
        /// Applies an inbound <c>WILL</c>/<c>WONT FORWARDMASK</c>: records the
        /// remote sub-state.
        /// </summary>
        /// <param name="accepted">Whether the verb was <c>WILL</c>.</param>
        internal void ApplyForwardMaskAnswer(bool accepted)
        {
            lock (sync)
            {
                forwardMaskAccepted = accepted;
            }
        }

        /// <summary>
        /// Applies an inbound MODE mask per the client rules (RFC 1184 §2.2).
        /// A MODE with MODE_ACK set is never answered; an unchanged mask is
        /// ignored; otherwise the reply echoes the requested mask verbatim plus
        /// MODE_ACK.
        /// </summary>
        /// <param name="requested">The received MODE mask byte.</param>
        /// <returns>The reply mask (with MODE_ACK set), or null for no reply.</returns>
        internal byte? ApplyMode(byte requested)
        {
            return ApplyModeCore(requested, union: false);
        }

        /// <summary>
        /// Applies an inbound MODE mask per the server rules (RFC 1184 §2.2):
        /// the reply echoes the requested mask verbatim with MODE_ACK set. An
        /// ACKed mask that differs is adopted silently (the server switches);
        /// anything already agreed is ignored.
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
        /// server (it switches); otherwise the reply echoes the request verbatim
        /// plus MODE_ACK.
        /// </summary>
        /// <param name="requested">The received MODE mask byte.</param>
        /// <param name="union">Whether this is the server path (adopts ACKed masks).</param>
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

                byte basis = mask;
                byte reply = (byte)(basis | LinemodeProtocol.ModeAck);
                mode = (byte)(reply & ~LinemodeProtocol.ModeAck);
                return reply;
            }
        }

        /// <summary>
        /// Applies one inbound SLC triplet per the client rules (RFC 1184 §5.5
        /// rules 1–4): identical settings are ignored; a <c>DEFAULT</c> level
        /// restores the row from the configured defaults (telnetlib3
        /// <c>_slc_change</c>: mask and value come from the default tab, or
        /// NOSUPPORT when the row itself is DEFAULT-level, i.e. unsupported)
        /// and replies the restored row without ACK; same-level ACKed changes
        /// switch silently (the client wins a simultaneous change);
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
            return ApplySlcCore(function, modifier, value, asServer: false);
        }

        /// <summary>
        /// Applies one inbound SLC triplet per the server rules (RFC 1184 §5.5
        /// rules 1–4, with the server column of the same-level+ACK row): all
        /// rules match <see cref="ApplySlc"/> except a same-level ACKed change
        /// with a different value, which the server <em>ignores</em> (no reply,
        /// no state change — it stands still while the client switches) instead
        /// of adopting.
        /// </summary>
        /// <param name="function">The SLC function code.</param>
        /// <param name="modifier">The level plus ACK/FLUSH modifier bits.</param>
        /// <param name="value">The proposed character value.</param>
        /// <returns>The reply (modifier, value) triplet tail, or null for no reply.</returns>
        internal (byte Modifier, byte Value)? ApplySlcAsServer(byte function, byte modifier, byte value)
        {
            return ApplySlcCore(function, modifier, value, asServer: true);
        }

        /// <summary>
        /// Shared SLC-triplet engine (reference <c>_slc_process</c> /
        /// <c>_slc_change</c>); both roles share every row — there is no
        /// client/server split. Identical level+value triplets are ignored
        /// (modifier bits play no part); any ACKed triplet is dropped without
        /// storing or replying; a NOSUPPORT level stores the disable value
        /// with ACK; otherwise a valued row adopts the peer mask+value with
        /// ACK, and a valueless row refuses with its own row (degenerating to
        /// NOSUPPORT on a CANTCHANGE/CANTCHANGE clash).
        /// </summary>
        private (byte Modifier, byte Value)? ApplySlcCore(byte function, byte modifier, byte value, bool asServer)
        {
            lock (sync)
            {
                if (function == 0)
                {
                    // Func 0 is an import request, never a table row: the session
                    // hook answers those, so anything reaching here is dropped
                    // silently (never echo the request back).
                    return null;
                }

                if (function > LinemodeProtocol.MaxFunction)
                {
                    // Out of range: refuse as NOSUPPORT with the disable
                    // value, so the peer turns the function off.
                    return (LinemodeProtocol.LevelNoSupport, 255);
                }

                byte level = (byte)(modifier & LinemodeProtocol.LevelBits);
                byte upper = (byte)(modifier & ~LinemodeProtocol.LevelBits);
                SlcEntry current = table[function];
                if (level == current.Level && value == current.Value)
                {
                    return null;
                }

                if ((modifier & LinemodeProtocol.FlagAck) != 0)
                {
                    return null;
                }

                if (level == LinemodeProtocol.LevelNoSupport)
                {
                    // The peer supports nothing here: store NOSUPPORT with the
                    // disable value and ACK it (the peer value is not echoed).
                    table[function] = new SlcEntry(LinemodeProtocol.LevelNoSupport, 255, LinemodeProtocol.FlagAck);
                    return (LinemodeProtocol.FlagAck, 255);
                }

                if (level == LinemodeProtocol.LevelDefault)
                {
                    // DEFAULT is a directive ("use your default"), not a value
                    // proposal: restore from the configured defaults and reply
                    // the restored row without ACK. The inbound value byte is
                    // ignored. RFC 1184 section 5.5 rule 3 echoes the agreed
                    // flush bits, so the restored flags ride along.
                    SlcEntry configured = defaults[function];
                    table[function] = current.Level == LinemodeProtocol.LevelDefault
                      ? new SlcEntry(LinemodeProtocol.LevelNoSupport, configured.Value, 0)
                      : configured;
                    SlcEntry restored = table[function];
                    return ((byte)(restored.Level | restored.Flags), restored.Value);
                }

                if (current.Value != 0)
                {
                    // Valued row: accept the peer value, storing the full
                    // modifier (level plus reserved bits) and replying it
                    // with ACK set.
                    table[function] = new SlcEntry(level, value, upper);
                    return ((byte)(level | upper | LinemodeProtocol.FlagAck), value);
                }

                if (current.Level == LinemodeProtocol.LevelDefault)
                {
                    // Willing but valueless row: store and ACK whatever was sent.
                    table[function] = new SlcEntry(level, value, upper);
                    return ((byte)(level | upper | LinemodeProtocol.FlagAck), value);
                }

                if (level == LinemodeProtocol.LevelCantChange
                    && current.Level == LinemodeProtocol.LevelCantChange)
                {
                    // Unchangeable on both ends: degenerate to NOSUPPORT.
                    table[function] = new SlcEntry(LinemodeProtocol.LevelNoSupport, current.Value, 0);
                    return (LinemodeProtocol.LevelNoSupport, current.Value);
                }

                // Fixed or valueless row: refuse with our stored level (the
                // stored modifier bits clear), resetting a CANTCHANGE value
                // from the configured defaults. No ACK is set.
                byte refusedValue = current.Level == LinemodeProtocol.LevelCantChange
                  ? defaults[function].Value
                  : current.Value;
                table[function] = new SlcEntry(current.Level, refusedValue, 0);
                return (current.Level, refusedValue);
            }
        }

        /// <summary>
        /// Marks the SLC table as published (the server sends it on the first
        /// MODE). Returns true on the first call only, so follow-up MODEs do
        /// not republish (telnetlib3 <c>_slc_sent</c>).
        /// </summary>
        /// <returns>Whether this call is the first (publish now).</returns>
        internal bool MarkSlcPublished()
        {
            lock (sync)
            {
                if (slcPublished)
                {
                    return false;
                }

                slcPublished = true;
                return true;
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
        /// export would wrongly tell the server to disable everything).
        /// NOSUPPORT rows are always omitted, including in import mode
        /// (reference <c>_slc_send</c>: <c>if slctab.get(...).nosupport:
        /// continue</c>), so the peer may use its own values there.
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
                        // Export carries the agreed flush bits with the level
                        // (RFC 1184 section 5.5 rule 3).
                        triplets.Add((byte)(entry.Level | entry.Flags));
                        triplets.Add(entry.Value);
                    }
                }

                return triplets?.ToArray();
            }
        }
    }
}
