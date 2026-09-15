namespace telnet_cs.Fuzz;

/// <summary>
/// Seed corpus of wire-valid (plus classic malformed) frames. The mutator
/// concatenates and mutates these so generated inputs reach deep dispatch
/// paths — agreed negotiation, per-option subnegotiation, MCCP streams —
/// that pure-random bytes almost never reach intact.
/// </summary>
internal static class Corpus
{
    private const byte Iac = 255;
    private const byte Sb = 250;
    private const byte Se = 240;

    private static byte[] Ascii(string text) => System.Text.Encoding.ASCII.GetBytes(text);

    private static byte[] Frame(byte option, byte[] body)
    {
        var frame = new byte[3 + body.Length + 2];
        frame[0] = Iac;
        frame[1] = Sb;
        frame[2] = option;
        body.CopyTo(frame, 3);
        frame[^2] = Iac;
        frame[^1] = Se;
        return frame;
    }

    private static byte[] Cmd(byte verb, byte option) => [Iac, verb, option];

    public static readonly byte[][] Entries =
    [
        // Negotiation openers and storms.
        Cmd(251, 24), Cmd(251, 31), Cmd(251, 32), Cmd(251, 39), Cmd(251, 42),
        Cmd(253, 1), Cmd(253, 3), Cmd(251, 201), Cmd(251, 69), Cmd(251, 86),
        [Iac, 251, 1, Iac, 252, 3, Iac, 253, 31, Iac, 254, 5, Iac, 251, 201, Iac, 253, 42],
        Cmd(251, 0), Cmd(251, 255), Cmd(252, 255), Cmd(253, 255), Cmd(254, 255),
        // Terminal type / speed / display.
        Frame(24, [0, .. Ascii("xterm-256color")]),
        Frame(24, [0, .. Ascii("ANSI")]),
        Frame(24, [0, .. Ascii("MTTS 137")]),
        Frame(24, [1]),
        Frame(32, [0, .. Ascii("38400,38400")]),
        Frame(32, [1]),
        Frame(35, [0, .. Ascii(":0.0")]),
        Frame(35, [1]),
        // Window size: normal, zero, huge, truncated, overlong.
        Frame(31, [0, 80, 0, 24]),
        Frame(31, [0, 0, 0, 0]),
        Frame(31, [255, 255, 255, 255]),
        Frame(31, [0, 80]),
        Frame(31, [0, 80, 0, 24, 99]),
        // Environment old/new form.
        Frame(36, [0, 0, .. Ascii("TERM"), 1, .. Ascii("xterm")]),
        Frame(39, [0, 0, .. Ascii("TERM"), 1, .. Ascii("xterm"), 3, .. Ascii("FOO"), 1, .. Ascii("bar")]),
        Frame(39, [2]),
        Frame(39, [1]),
        Frame(39, [0, 2, .. Ascii("a"), 1, .. Ascii("b")]),
        // Charset: request/accepted/rejected/table verbs, odd separators.
        Frame(42, [1, .. Ascii(" UTF-8 LATIN-1 US-ASCII")]),
        Frame(42, [1, (byte)';', .. Ascii("UTF-8;LATIN-1")]),
        Frame(42, [2, .. Ascii("UTF-8")]),
        Frame(42, [3]),
        Frame(42, [4]),
        Frame(42, [5]),
        Frame(42, [6]),
        Frame(42, [7]),
        Frame(42, [9]),
        Frame(42, [1]),
        // Linemode: mode masks, SLC tables (valid + ragged), forwardmask, unknown sub.
        Frame(34, [1, 3]),
        Frame(34, [1, 3, 4]),
        Frame(34, [3, 1, 0, 0, 3, 0, 0, 7, 128, 3]),
        Frame(34, [3, 1, 0]),
        Frame(34, [3]),
        Frame(34, [2, 255, 0, 1, 2]),
        Frame(34, [77, 1, 2, 3]),
        // Status / timing-mark / logout.
        Frame(5, [1]),
        Frame(5, [0, 1, 3, 0, 1, 5, 0, 6]),
        Cmd(253, 6), Cmd(251, 6), Cmd(253, 18),
        // Send-location, toggle-flow-control.
        Frame(23, Ascii("room-12")),
        Frame(23, [1]),
        Frame(33, [1]),
        Frame(33, [0]),
        Frame(33, [2]),
        Frame(33, [3]),
        Frame(33, [9]),
        // MUD options: valid shapes.
        Frame(201, Ascii("Core.Hello {\"client\":\"t\",\"version\":\"1\"}")),
        Frame(201, Ascii("Char.Vitals {\"hp\":100,\"maxhp\":100}")),
        Frame(201, Ascii("Room.Info")),
        Frame(201, Ascii("Bad.Package {oops")),
        Frame(69, [1, .. Ascii("HEALTH"), 2, .. Ascii("100")]),
        Frame(69, [3, 1, .. Ascii("A"), 2, .. Ascii("1"), 1, .. Ascii("B"), 2, .. Ascii("2"), 4]),
        Frame(69, [5, 2, .. Ascii("x"), 2, .. Ascii("y"), 6]),
        Frame(70, [1, .. Ascii("NAME"), 2, .. Ascii("TestMUD")]),
        Frame(93, [.. Ascii("zmp.ident"), 0, .. Ascii("mud"), 0, .. Ascii("1.0"), 0]),
        Frame(200, Ascii("Room.Exits ne,nw")),
        Frame(102, [100, 3]),
        Frame(90, []),
        Frame(91, []),
        // MCCP start markers (empty SB).
        Frame(86, []),
        Frame(87, []),
        // In-band commands and text edges.
        [Iac, 241], [Iac, 249], [Iac, 239], [Iac, 247], [Iac, 248],
        [Iac, 244], [Iac, 243], [Iac, 245], [Iac, 246], [Iac, 236],
        [Iac, Iac], Ascii("hello\r\n"), Ascii("login: "),
        [13, 0], [13, 10], [13], [10], [0], [7, 8, 9, 11, 12],
        // Classic malformed framing.
        [Se], [Iac, Se], [Iac, Sb, Iac, Se], [Iac, Sb], [Iac],
        [Iac, Sb, 24, 0, .. Ascii("cut-short")],
        [Iac, 250, 31, 0, 80, Iac, 249],
        [Iac, 240, 240, Iac, 250, 240],
    ];
}
