namespace telnet_cs.Encodings
{
    /// <summary>
    /// Atari ST character encoding (ATARIST.TXT): ASCII below 0x80, accented
    /// Latin, Hebrew, Greek and math symbols above. Alias: <c>atari</c>.
    /// </summary>
    public sealed class AtaristEncoding : CharmapEncoding
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="AtaristEncoding"/> class.
        /// </summary>
        public AtaristEncoding()
            : base("atarist", DecodeTable, preferLowRange: false)
        {
        }

        /// <inheritdoc/>
        public override string EncodingName => "Atari ST";

        private static readonly string[] DecodeTable = BuildDecodeTable();

        private static string[] BuildDecodeTable()
        {
            var table = new string[256];
            for (var i = 0; i < 128; i++)
            {
                table[i] = ((char)i).ToString();
            }

            table[0x80] = ((char)0x00C7).ToString();
            table[0x81] = ((char)0x00FC).ToString();
            table[0x82] = ((char)0x00E9).ToString();
            table[0x83] = ((char)0x00E2).ToString();
            table[0x84] = ((char)0x00E4).ToString();
            table[0x85] = ((char)0x00E0).ToString();
            table[0x86] = ((char)0x00E5).ToString();
            table[0x87] = ((char)0x00E7).ToString();
            table[0x88] = ((char)0x00EA).ToString();
            table[0x89] = ((char)0x00EB).ToString();
            table[0x8A] = ((char)0x00E8).ToString();
            table[0x8B] = ((char)0x00EF).ToString();
            table[0x8C] = ((char)0x00EE).ToString();
            table[0x8D] = ((char)0x00EC).ToString();
            table[0x8E] = ((char)0x00C4).ToString();
            table[0x8F] = ((char)0x00C5).ToString();
            table[0x90] = ((char)0x00C9).ToString();
            table[0x91] = ((char)0x00E6).ToString();
            table[0x92] = ((char)0x00C6).ToString();
            table[0x93] = ((char)0x00F4).ToString();
            table[0x94] = ((char)0x00F6).ToString();
            table[0x95] = ((char)0x00F2).ToString();
            table[0x96] = ((char)0x00FB).ToString();
            table[0x97] = ((char)0x00F9).ToString();
            table[0x98] = ((char)0x00FF).ToString();
            table[0x99] = ((char)0x00D6).ToString();
            table[0x9A] = ((char)0x00DC).ToString();
            table[0x9B] = ((char)0x00A2).ToString();
            table[0x9C] = ((char)0x00A3).ToString();
            table[0x9D] = ((char)0x00A5).ToString();
            table[0x9E] = ((char)0x00DF).ToString();
            table[0x9F] = ((char)0x0192).ToString();
            table[0xA0] = ((char)0x00E1).ToString();
            table[0xA1] = ((char)0x00ED).ToString();
            table[0xA2] = ((char)0x00F3).ToString();
            table[0xA3] = ((char)0x00FA).ToString();
            table[0xA4] = ((char)0x00F1).ToString();
            table[0xA5] = ((char)0x00D1).ToString();
            table[0xA6] = ((char)0x00AA).ToString();
            table[0xA7] = ((char)0x00BA).ToString();
            table[0xA8] = ((char)0x00BF).ToString();
            table[0xA9] = ((char)0x2310).ToString();
            table[0xAA] = ((char)0x00AC).ToString();
            table[0xAB] = ((char)0x00BD).ToString();
            table[0xAC] = ((char)0x00BC).ToString();
            table[0xAD] = ((char)0x00A1).ToString();
            table[0xAE] = ((char)0x00AB).ToString();
            table[0xAF] = ((char)0x00BB).ToString();
            table[0xB0] = ((char)0x00E3).ToString();
            table[0xB1] = ((char)0x00F5).ToString();
            table[0xB2] = ((char)0x00D8).ToString();
            table[0xB3] = ((char)0x00F8).ToString();
            table[0xB4] = ((char)0x0153).ToString();
            table[0xB5] = ((char)0x0152).ToString();
            table[0xB6] = ((char)0x00C0).ToString();
            table[0xB7] = ((char)0x00C3).ToString();
            table[0xB8] = ((char)0x00D5).ToString();
            table[0xB9] = ((char)0x00A8).ToString();
            table[0xBA] = ((char)0x00B4).ToString();
            table[0xBB] = ((char)0x2020).ToString();
            table[0xBC] = ((char)0x00B6).ToString();
            table[0xBD] = ((char)0x00A9).ToString();
            table[0xBE] = ((char)0x00AE).ToString();
            table[0xBF] = ((char)0x2122).ToString();
            table[0xC0] = ((char)0x0133).ToString();
            table[0xC1] = ((char)0x0132).ToString();
            table[0xC2] = ((char)0x05D0).ToString();
            table[0xC3] = ((char)0x05D1).ToString();
            table[0xC4] = ((char)0x05D2).ToString();
            table[0xC5] = ((char)0x05D3).ToString();
            table[0xC6] = ((char)0x05D4).ToString();
            table[0xC7] = ((char)0x05D5).ToString();
            table[0xC8] = ((char)0x05D6).ToString();
            table[0xC9] = ((char)0x05D7).ToString();
            table[0xCA] = ((char)0x05D8).ToString();
            table[0xCB] = ((char)0x05D9).ToString();
            table[0xCC] = ((char)0x05DB).ToString();
            table[0xCD] = ((char)0x05DC).ToString();
            table[0xCE] = ((char)0x05DE).ToString();
            table[0xCF] = ((char)0x05E0).ToString();
            table[0xD0] = ((char)0x05E1).ToString();
            table[0xD1] = ((char)0x05E2).ToString();
            table[0xD2] = ((char)0x05E4).ToString();
            table[0xD3] = ((char)0x05E6).ToString();
            table[0xD4] = ((char)0x05E7).ToString();
            table[0xD5] = ((char)0x05E8).ToString();
            table[0xD6] = ((char)0x05E9).ToString();
            table[0xD7] = ((char)0x05EA).ToString();
            table[0xD8] = ((char)0x05DF).ToString();
            table[0xD9] = ((char)0x05DA).ToString();
            table[0xDA] = ((char)0x05DD).ToString();
            table[0xDB] = ((char)0x05E3).ToString();
            table[0xDC] = ((char)0x05E5).ToString();
            table[0xDD] = ((char)0x00A7).ToString();
            table[0xDE] = ((char)0x2227).ToString();
            table[0xDF] = ((char)0x221E).ToString();
            table[0xE0] = ((char)0x03B1).ToString();
            table[0xE1] = ((char)0x03B2).ToString();
            table[0xE2] = ((char)0x0393).ToString();
            table[0xE3] = ((char)0x03C0).ToString();
            table[0xE4] = ((char)0x03A3).ToString();
            table[0xE5] = ((char)0x03C3).ToString();
            table[0xE6] = ((char)0x00B5).ToString();
            table[0xE7] = ((char)0x03C4).ToString();
            table[0xE8] = ((char)0x03A6).ToString();
            table[0xE9] = ((char)0x0398).ToString();
            table[0xEA] = ((char)0x03A9).ToString();
            table[0xEB] = ((char)0x03B4).ToString();
            table[0xEC] = ((char)0x222E).ToString();
            table[0xED] = ((char)0x03C6).ToString();
            table[0xEE] = ((char)0x2208).ToString();
            table[0xEF] = ((char)0x2229).ToString();
            table[0xF0] = ((char)0x2261).ToString();
            table[0xF1] = ((char)0x00B1).ToString();
            table[0xF2] = ((char)0x2265).ToString();
            table[0xF3] = ((char)0x2264).ToString();
            table[0xF4] = ((char)0x2320).ToString();
            table[0xF5] = ((char)0x2321).ToString();
            table[0xF6] = ((char)0x00F7).ToString();
            table[0xF7] = ((char)0x2248).ToString();
            table[0xF8] = ((char)0x00B0).ToString();
            table[0xF9] = ((char)0x2219).ToString();
            table[0xFA] = ((char)0x00B7).ToString();
            table[0xFB] = ((char)0x221A).ToString();
            table[0xFC] = ((char)0x207F).ToString();
            table[0xFD] = ((char)0x00B2).ToString();
            table[0xFE] = ((char)0x00B3).ToString();
            table[0xFF] = ((char)0x00AF).ToString();
            return table;
        }
    }
}
