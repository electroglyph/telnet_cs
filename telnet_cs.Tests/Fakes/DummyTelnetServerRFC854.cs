namespace telnet_cs.Tests
{
    using telnet_cs.Client;

    public class DummyTelnetServerRFC854 : DummyTelnetServerBase
    {
        public DummyTelnetServerRFC854()
          : base(Client.Rfc854LineFeed)
        {

        }
    }
}
