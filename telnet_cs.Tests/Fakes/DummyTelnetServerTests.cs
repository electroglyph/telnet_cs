namespace telnet_cs.Tests
{
  using Xunit;
  using FluentAssertions;

  public class DummyTelnetServerTests
  {
    [Fact]
    public void TelnetServerShouldTerminateAndReleaseDebuggingContext()
    {
      DummyTelnetServer server;
      using (server = new DummyTelnetServer())
      {
      }
      server.IsListening.Should().BeFalse();
    }
  }
}
