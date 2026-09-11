namespace telnet_cs.Tests
{
    using System.Diagnostics.CodeAnalysis;
    using telnet_cs.Client;

    [ExcludeFromCodeCoverage]
    public class DummyTelnetServer : DummyTelnetServerBase
    {
        public DummyTelnetServer()
          : base(Client.LegacyLineFeed)
        { }
    }
}
