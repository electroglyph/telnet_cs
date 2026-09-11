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
    /// Table-transfer verbs (4-7) are not implemented.
    /// </summary>
    public static class CharsetProtocol
    {
        /// <summary>Request a character set (1).</summary>
        public const byte Request = 1;

        /// <summary>Accept a character set (2).</summary>
        public const byte Accepted = 2;

        /// <summary>Reject a character set (3).</summary>
        public const byte Rejected = 3;

        /// <summary>
        /// Builds a <c>REQUEST</c> payload (verb first, without IAC SB/SE
        /// framing) offering <paramref name="charsets"/> joined by a space.
        /// </summary>
        /// <param name="charsets">The offered character-set names.</param>
        public static byte[] BuildRequest(IEnumerable<string> charsets)
        {
            ArgumentNullException.ThrowIfNull(charsets);
            var joined = string.Join(" ", charsets);
            var body = Encoding.ASCII.GetBytes(" " + joined);
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
            return text.Split(separator);
        }

        /// <summary>
        /// Reads the character-set name from an <c>ACCEPTED</c> payload (verb first).
        /// </summary>
        /// <param name="payload">The received payload, verb first.</param>
        public static string ParseAccepted(IReadOnlyList<byte> payload)
        {
            ArgumentNullException.ThrowIfNull(payload);
            return Encoding.ASCII.GetString([.. payload.Skip(1)]);
        }

        /// <summary>
        /// Selects the first offered character set this runtime can decode, or
        /// null when none is usable (the caller then answers REJECTED).
        /// </summary>
        /// <param name="offered">The offered character-set names.</param>
        public static string? SelectSupported(IEnumerable<string> offered)
        {
            ArgumentNullException.ThrowIfNull(offered);
            foreach (var name in offered)
            {
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                try
                {
                    _ = Encoding.GetEncoding(name.Trim());
                    return name.Trim();
                }
                catch (ArgumentException)
                {
                }
            }

            return null;
        }
    }
}
