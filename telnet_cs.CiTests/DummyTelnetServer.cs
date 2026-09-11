namespace telnet_cs.CiTests
{
  using System.Diagnostics.CodeAnalysis;

  [ExcludeFromCodeCoverage]
  public class DummyTelnetServer : DummyTelnetServerBase
  {
    public DummyTelnetServer()
      : base(Client.LegacyLineFeed)
    { }
  }
}
