namespace telnet_cs.Server;

using telnet_cs.Protocol;

public partial class ServerSession
{
    /// <summary>
    /// One parsed STATUS IS item (RFC 859): a <c>WILL</c>/<c>WONT</c>/
    /// <c>DO</c>/<c>DONT</c> option pair (<c>Data</c> null), or an
    /// <c>SB &lt;opt&gt; &lt;data&gt; SE</c> block (<c>Verb</c> is
    /// <c>Subnegotiation</c>).
    /// </summary>
    /// <param name="Verb">The item verb.</param>
    /// <param name="Option">The option byte.</param>
    /// <param name="Data">The SB block bytes, or null for verb pairs.</param>
    public readonly record struct StatusReportItem(Commands Verb, byte Option, byte[]? Data);
}
