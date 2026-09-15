namespace telnet_cs.Fuzz;

/// <summary>
/// Fuzz target selection.
/// </summary>
internal enum FuzzMode
{
    Parser,
    Session,
    Client,
    Auth,
    Codec,
    Encoding,
    Input,
    Both,
    All,
}
