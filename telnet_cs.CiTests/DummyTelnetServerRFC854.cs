namespace telnet_cs.CiTests
{
  public class DummyTelnetServerRFC854 : DummyTelnetServerBase
  {
    public DummyTelnetServerRFC854()
      : base(Client.Rfc854LineFeed)
    {

    }
  }
}
