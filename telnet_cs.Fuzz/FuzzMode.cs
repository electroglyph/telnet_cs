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
    Write,
    Term,
    Mccp,
    Proto,
    Accept,
    Repl,
    Request,
    TlsSniff,
    Caps,
    Storm,
    Both,
    All,
}
