namespace telnet_cs.Encodings;

/// <summary>
/// PETSCII in shifted (lowercase) mode, the Commodore 64/128 encoding used
/// for BBS operation: lowercase at 0x41-0x5A, uppercase at 0xC1-0xDA,
/// graphics approximations from the box/block/geometric blocks. Aliases:
/// <c>cbm</c>, <c>commodore</c>, <c>c64</c>, <c>c128</c>.
/// </summary>
public sealed class PetsciiEncoding : CharmapEncoding
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PetsciiEncoding"/> class.
    /// </summary>
    public PetsciiEncoding()
        : base("petscii", DecodeTable, preferLowRange: false)
    {
    }

    /// <inheritdoc/>
    public override string EncodingName => "PETSCII (Commodore, shifted mode)";

    private static readonly string[] DecodeTable = BuildDecodeTable();

    private static string[] BuildDecodeTable()
    {
        // Control ranges (0x00-0x1F, 0x80-0x9F) decode to the identical
        // code point; the shifted spaces (0xA0, 0xE0) decode to NBSP.
        var table = new string[256];
        for (var i = 0; i < 256; i++)
        {
            table[i] = ((char)i).ToString();
        }

        table[0xE0] = "\u00A0";

        table[0x0A] = "\n";
        table[0x0D] = "\r";
        table[0x8D] = "\r";

        // 0x40-0x5F: @, lowercase a-z, brackets, pound, arrows.
        const string lower = "@abcdefghijklmnopqrstuvwxyz[£]↑←";
        for (var i = 0; i < lower.Length; i++)
        {
            table[0x40 + i] = lower[i].ToString();
        }

        // 0xC1-0xDA: uppercase A-Z.
        for (var i = 0; i < 26; i++)
        {
            table[0xC1 + i] = ((char)('A' + i)).ToString();
        }

        // Graphics blocks: 0x60-0x7F, 0xA1-0xC0, 0xDB-0xDF, mirrors at
        // 0xE1-0xFE, and pi at 0x7E/0xDE/0xFF.
        const string graphics60 =
            "─♠│─▗▖▘▝▙▟▞▕▏▄▀█" +
            "▄▛▃♥▜╭╳○♣▚♦┼│╱π◥";
        for (var i = 0; i < graphics60.Length; i++)
        {
            table[0x60 + i] = graphics60[i].ToString();
        }

        const string graphicsA1 =
            "▄▀───│││╮╰╯╲╱╳•" +
            "◤▌▗└┐▂┌┴┬┤├▆▅▐█╲";
        for (var i = 0; i < graphicsA1.Length; i++)
        {
            table[0xA1 + i] = graphicsA1[i].ToString();
        }

        table[0xC0] = "─";
        const string graphicsDB = "┼│╱π◥";
        for (var i = 0; i < graphicsDB.Length; i++)
        {
            table[0xDB + i] = graphicsDB[i].ToString();
        }

        for (var i = 0; i < 30; i++)
        {
            table[0xE1 + i] = table[0xA1 + i];
        }

        table[0xFF] = "π";
        return table;
    }
}
