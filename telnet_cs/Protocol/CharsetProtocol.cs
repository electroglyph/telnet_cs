namespace telnet_cs.Protocol
{
    using System;
    using System.Collections.Generic;
    using System.Text;

    /// <summary>
    /// Character-set negotiation (CHARSET, option 42; RFC 2066) subnegotiation
    /// verbs and framing helpers. Either side may send
    /// <c>IAC SB CHARSET REQUEST &lt;sep&gt;&lt;sep-joined-list&gt; IAC SE</c>;
    /// the peer answers <c>ACCEPTED &lt;charset&gt;</c> or <c>REJECTED</c>.
    /// Table transfer (verbs 4-7) is unimplemented: inbound table verbs
    /// are logged and ignored with no reply, since answering would invite
    /// a transfer the stack cannot consume.
    /// </summary>
    public static class CharsetProtocol
    {
        /// <summary>Request a character set (1).</summary>
        public const byte Request = 1;

        /// <summary>Accept a character set (2).</summary>
        public const byte Accepted = 2;

        /// <summary>Reject a character set (3).</summary>
        public const byte Rejected = 3;

        /// <summary>Offer a translation table (4). Logged and ignored.</summary>
        public const byte TTableIs = 4;

        /// <summary>Reject a translation table (5). Leaves the charset unchanged.</summary>
        public const byte TTableRejected = 5;

        /// <summary>Acknowledge a translation table (6). Logged and ignored.</summary>
        public const byte TTableAck = 6;

        /// <summary>Negative-acknowledge a translation table (7). Logged and ignored.</summary>
        public const byte TTableNak = 7;

        /// <summary>
        /// Builds a <c>REQUEST</c> payload (verb first, without IAC SB/SE
        /// framing) offering <paramref name="charsets"/>. The separator byte
        /// (RFC 2066 section 2, chosen by the sender and absent from every
        /// name) defaults to space and falls back to the first of
        /// <c>';'</c>, <c>','</c>, <c>'/'</c> absent from all names, so offers
        /// containing spaces still round-trip.
        /// </summary>
        /// <param name="charsets">The offered character-set names.</param>
        public static byte[] BuildRequest(IEnumerable<string> charsets)
        {
            ArgumentNullException.ThrowIfNull(charsets);
            var offers = charsets as IReadOnlyList<string> ?? [.. charsets];
            var separator = ' ';
            if (offers.Any(static name => name.Contains(' ')))
            {
                foreach (var candidate in new[] { ';', ',', '/' })
                {
                    if (offers.All(name => !name.Contains(candidate)))
                    {
                        separator = candidate;
                        break;
                    }
                }
            }

            var joined = string.Join(separator, offers);
            var body = Encoding.ASCII.GetBytes(separator + joined);
            var payload = new byte[1 + body.Length];
            payload[0] = Request;
            body.CopyTo(payload, 1);
            return payload;
        }

        /// <summary>
        /// Builds an <c>ACCEPTED &lt;charset&gt;</c> payload (verb first).
        /// </summary>
        /// <param name="charset">The accepted character-set name.</param>
        public static byte[] BuildAccepted(string charset)
        {
            ArgumentNullException.ThrowIfNull(charset);
            var body = Encoding.ASCII.GetBytes(charset);
            var payload = new byte[1 + body.Length];
            payload[0] = Accepted;
            body.CopyTo(payload, 1);
            return payload;
        }

        /// <summary>
        /// Parses a <c>REQUEST</c> payload (verb first) into the offered names,
        /// splitting on the embedded separator byte (conventionally space).
        /// When the separator split yields no resolvable name but a
        /// space split does (a sender that omitted the separator byte and sent
        /// a bare space-joined list), the space split wins — the documented
        /// separator-superset leniency; well-formed requests are unaffected.
        /// </summary>
        /// <param name="payload">The received payload, verb first.</param>
        public static IReadOnlyList<string> ParseRequest(IReadOnlyList<byte> payload)
        {
            ArgumentNullException.ThrowIfNull(payload);
            if (payload.Count < 2)
            {
                return [];
            }

            var separator = (char)payload[1];
            var text = Encoding.ASCII.GetString([.. payload.Skip(2)]);
            var offers = SplitOffers(text, separator);
            if (offers.Count > 0 &&
                offers.All(static offer => CanonicalName(offer) is null))
            {
                // Separator-superset leniency: the sender may have omitted the
                // separator byte and sent a bare space-joined list, in which
                // case payload[1] is content, not a separator. Re-read from
                // payload[1] and split on space; keep it only when something
                // resolves (otherwise the strict parse stands).
                var unseparated = Encoding.ASCII.GetString([.. payload.Skip(1)]);
                var spaced = SplitOffers(unseparated, ' ');
                if (spaced.Any(static offer => CanonicalName(offer) is not null))
                {
                    return spaced;
                }
            }

            return offers;
        }

        private static IReadOnlyList<string> SplitOffers(string text, char separator)
        {
            if (text.Length == 0)
            {
                return [];
            }

            return text.Split(separator)
                .Where(static piece => !string.IsNullOrWhiteSpace(piece))
                .ToArray();
        }

        /// <summary>
        /// Reads the character-set name from an <c>ACCEPTED</c> payload (verb first).
        /// An empty name is tolerated (the peer named nothing): it yields an
        /// empty string rather than throwing, so the read loop survives a
        /// malformed ACCEPTED and the caller takes the rejection path.
        /// </summary>
        /// <param name="payload">The received payload, verb first.</param>
        public static string ParseAccepted(IReadOnlyList<byte> payload)
        {
            ArgumentNullException.ThrowIfNull(payload);
            if (payload.Count < 2)
            {
                return string.Empty;
            }

            return Encoding.ASCII.GetString([.. payload.Skip(1)]);
        }

        /// <summary>
        /// Builds a <c>TTABLE-REJECTED</c> payload (verb only).
        /// </summary>
        public static byte[] BuildTTableRejected() => [TTableRejected];

        /// <summary>
        /// Selects the first offered character set this runtime can decode, or
        /// null when none is usable (the caller then answers REJECTED).
        /// Unresolvable ("illegal") offers are skipped, never selected.
        /// </summary>
        /// <param name="offered">The offered character-set names.</param>
        public static string? SelectSupported(IEnumerable<string> offered) =>
          SelectSupported(offered, preferredEncodingName: null);

        /// <summary>
        /// Selects from <paramref name="offered"/> using the reference selection
        /// policy: an offer matching <paramref name="preferredEncodingName"/>
        /// (canonical name) wins; a null or weak-default (Latin-1 family)
        /// preference takes the first viable offer; any other explicit
        /// preference that is not offered is rejected (null) so the caller
        /// keeps its own encoding. Unresolvable offers are skipped.
        /// </summary>
        /// <param name="offered">The offered character-set names.</param>
        /// <param name="preferredEncodingName">The local encoding preference, or null for none.</param>
        public static string? SelectSupported(IEnumerable<string> offered, string? preferredEncodingName)
        {
            ArgumentNullException.ThrowIfNull(offered);
            var preferredCanonical = CanonicalName(preferredEncodingName);
            string? firstViable = null;
            foreach (var name in offered)
            {
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var canonical = CanonicalName(name.Trim());
                if (canonical is null)
                {
                    continue;
                }

                firstViable ??= name.Trim();
                if (preferredCanonical is not null &&
                    string.Equals(canonical, preferredCanonical, StringComparison.OrdinalIgnoreCase))
                {
                    return name.Trim();
                }
            }

            if (preferredCanonical is null || IsWeakDefault(preferredCanonical))
            {
                return firstViable;
            }

            return null;
        }

        /// <summary>
        /// Resolves a charset name to its canonical <see cref="Encoding.WebName"/>,
        /// trying progressively simpler variants (spaces to hyphens, leading
        /// zeros stripped from numeric segments, hyphens removed, hyphens
        /// removed from all but the first segment), or null when no variant
        /// resolves. Well-known aliases .NET does not know (e.g.
        /// <c>LATIN1</c>) map to their canonical spellings first.
        /// </summary>
        /// <param name="name">The charset name to resolve.</param>
        internal static string? CanonicalName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            var @base = name.Trim().Replace(' ', '-');
            var noLeadingZeros = StripLeadingZeros(@base);
            var parts = noLeadingZeros.Split('-');
            var partial = parts.Length > 2 ? parts[0] + "-" + string.Concat(parts[1..]) : noLeadingZeros;
            foreach (var candidate in new[] { @base, noLeadingZeros, @base.Replace("-", string.Empty, StringComparison.Ordinal), partial })
            {
                try
                {
                    return Encoding.GetEncoding(AliasToCanonical(candidate) ?? candidate).WebName;
                }
                catch (ArgumentException)
                {
                }

                // The codec registry knows "cp1252" but not "cp1250" (nor the
                // CJK "CP936"/"CP932"/"CP949"/"CP950" the reference server
                // offers): a "cp" prefix with trailing digits names a Windows
                // code page directly, so resolve it by number. Keeps matching
                // consistent — both sides canonicalize through here.
                var byNumber = TryGetEncodingByCodePageNumber(candidate);
                if (byNumber is not null)
                {
                    return byNumber;
                }
            }

            return null;
        }

        /// <summary>
        /// Resolves a "cpNNNN" spelling (any casing, e.g. <c>CP936</c>) to its
        /// canonical <see cref="Encoding.WebName"/> via the numeric code page,
        /// or null when the spelling is not a numeric code page or the runtime
        /// has no such page.
        /// </summary>
        /// <param name="candidate">The charset name variant to try.</param>
        private static string? TryGetEncodingByCodePageNumber(string candidate)
        {
            if (!candidate.StartsWith("cp", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var digits = candidate.AsSpan(2);
            if (digits.IsEmpty)
            {
                return null;
            }

            foreach (var ch in digits)
            {
                if (!char.IsAsciiDigit(ch))
                {
                    return null;
                }
            }

            if (!int.TryParse(digits, out var codePage))
            {
                return null;
            }

            try
            {
                return Encoding.GetEncoding(codePage).WebName;
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        /// <summary>
        /// Maps well-known charset aliases the runtime codec registry does not
        /// recognise to canonical spellings it does, or null when the name
        /// needs no mapping.
        /// </summary>
        /// <param name="name">The hyphenated charset name.</param>
        private static string? AliasToCanonical(string name)
        {
            if (string.Equals(name, "LATIN1", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "LATIN-1", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "ISO8859-1", StringComparison.OrdinalIgnoreCase))
            {
                return "iso-8859-1";
            }

            if (string.Equals(name, "UTF8", StringComparison.OrdinalIgnoreCase))
            {
                return "utf-8";
            }

            if (string.Equals(name, "USASCII", StringComparison.OrdinalIgnoreCase))
            {
                return "us-ascii";
            }

            return null;
        }

        /// <summary>
        /// Strips leading zeros from all-digit hyphen segments
        /// (<c>iso-8859-02</c> to <c>iso-8859-2</c>); other segments pass through.
        /// </summary>
        /// <param name="name">The hyphenated charset name.</param>
        private static string StripLeadingZeros(string name)
        {
            var parts = name.Split('-');
            for (var i = 0; i < parts.Length; i++)
            {
                var part = parts[i];
                var digits = part.TrimStart('0');
                if (digits.Length != 0 && digits.Length != part.Length && digits.All(char.IsAsciiDigit))
                {
                    parts[i] = digits;
                }
            }

            return string.Join("-", parts);
        }

        /// <summary>
        /// Reports whether a canonical encoding name is the weak default whose
        /// absence from an offer list still accepts the first viable offer
        /// (the Latin-1 family, matching the reference client policy).
        /// </summary>
        /// <param name="canonicalName">A canonical <see cref="Encoding.WebName"/>.</param>
        internal static bool IsWeakDefault(string canonicalName) =>
          string.Equals(canonicalName, "iso-8859-1", StringComparison.OrdinalIgnoreCase);
    }
}
