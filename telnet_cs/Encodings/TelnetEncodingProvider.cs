namespace telnet_cs.Encodings
{
    using System;
    using System.Collections.Generic;
    using System.Text;

    /// <summary>
    /// <see cref="EncodingProvider"/> exposing the retro-computer codecs by
    /// name: <c>atascii</c> (+ <c>atari8bit</c>, <c>atari_8bit</c>),
    /// <c>petscii</c> (+ <c>cbm</c>, <c>commodore</c>, <c>c64</c>,
    /// <c>c128</c>), <c>atarist</c> (+ <c>atari</c>), <c>big5bbs</c> (+
    /// <c>big5_bbs</c>, <c>big5_pcman</c>, <c>big5_pcmanx</c>,
    /// <c>big5_ptt</c>). Names are matched case-insensitively with hyphens
    /// treated as underscores.
    /// </summary>
    public sealed class TelnetEncodingProvider : EncodingProvider
    {
        private static readonly Dictionary<string, Func<Encoding>> Factories =
            new(StringComparer.Ordinal)
            {
                ["atascii"] = static () => new AtasciiEncoding(),
                ["atari8bit"] = static () => new AtasciiEncoding(),
                ["atari_8bit"] = static () => new AtasciiEncoding(),
                ["petscii"] = static () => new PetsciiEncoding(),
                ["cbm"] = static () => new PetsciiEncoding(),
                ["commodore"] = static () => new PetsciiEncoding(),
                ["c64"] = static () => new PetsciiEncoding(),
                ["c128"] = static () => new PetsciiEncoding(),
                ["atarist"] = static () => new AtaristEncoding(),
                ["atari"] = static () => new AtaristEncoding(),
                ["big5bbs"] = static () => new Big5BbsEncoding(),
                ["big5_bbs"] = static () => new Big5BbsEncoding(),
                ["big5_pcman"] = static () => new Big5BbsEncoding(),
                ["big5_pcmanx"] = static () => new Big5BbsEncoding(),
                ["big5_ptt"] = static () => new Big5BbsEncoding(),
            };

        /// <summary>
        /// Gets the shared provider instance.
        /// </summary>
        public static TelnetEncodingProvider Instance { get; } = new();

        /// <inheritdoc/>
        public override Encoding? GetEncoding(string name)
        {
            ArgumentException.ThrowIfNullOrEmpty(name);
            var normalized = name.ToLowerInvariant().Replace('-', '_');
            return Factories.TryGetValue(normalized, out var factory) ? factory() : null;
        }

        /// <inheritdoc/>
        public override Encoding? GetEncoding(int codepage)
        {
            return null;
        }

        /// <inheritdoc/>
        public override IEnumerable<EncodingInfo> GetEncodings()
        {
            yield return new EncodingInfo(this, 80001, "atascii", "ATASCII (Atari 8-bit)");
            yield return new EncodingInfo(this, 80002, "petscii", "PETSCII (Commodore, shifted mode)");
            yield return new EncodingInfo(this, 80003, "atarist", "Atari ST");
            yield return new EncodingInfo(this, 80004, "big5bbs", "Big5-BBS hybrid (Taiwanese BBS)");
        }
    }
}
