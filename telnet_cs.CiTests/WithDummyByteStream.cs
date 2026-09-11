namespace telnet_cs.CiTests
{
  using FluentAssertions;
  using Xunit;
  using System;
  using System.Text.RegularExpressions;
  using System.Threading;
  using System.Threading.Tasks;

  public class WithDummyByteStream
  {
    private const int timeoutMs = 500;

    [Fact]
    public void ShouldConnect()
    {
      using (var stream = new DummyByteStream())
      {
        using (var client = new Client(stream, new CancellationToken()))
        {
          client.IsConnected.Should().Be(true);
        }
      }
    }

    [Fact(Timeout = 2000)]
    public async Task ShouldTerminateWithAColon()
    {
      using (var stream = new DummyByteStream())
      {
        using (var client = new Client(stream, new CancellationToken()))
        {
          client.IsConnected.Should().Be(true);
          (await client.TerminatedReadAsync(":", TimeSpan.FromMilliseconds(timeoutMs)))
            .Should().EndWith(":");
        }
      }
    }

    [Fact(Timeout = 2000)]
    public async Task ShouldBePromptingForAccount()
    {
      using (var stream = new DummyByteStream())
      {
        using (var client = new Client(stream, new CancellationToken()))
        {
          client.IsConnected.Should().Be(true);
          var s = await client.TerminatedReadAsync("Account:", TimeSpan.FromMilliseconds(timeoutMs));

          s.Should().Contain("Account:");
        }
      }
    }

    [Fact(Timeout = 2000)]
    public async Task ShouldBePromptingForPassword()
    {
      using (var stream = new DummyByteStream())
      {
        using (var client = new Client(stream, new CancellationToken()))
        {
          client.IsConnected.Should().Be(true);
          var s = await client.TerminatedReadAsync("Account:", TimeSpan.FromMilliseconds(timeoutMs));
          s.Should().Contain("Account:");
          await client.WriteLineAsync("username");
          s = await client.TerminatedReadAsync("Password:", TimeSpan.FromMilliseconds(timeoutMs));
        }
      }
    }

    [Fact(Timeout = 3000)]
    public async Task ShouldPromptForInput()
    {
      using (var stream = new DummyByteStream())
      {
        using (var client = new Client(stream, new CancellationToken()))
        {
          client.IsConnected.Should().Be(true);
          await client.TerminatedReadAsync("Account:", TimeSpan.FromMilliseconds(timeoutMs));
          await client.WriteLineAsync("username");
          await client.TerminatedReadAsync("Password:", TimeSpan.FromMilliseconds(timeoutMs));
          await client.WriteLineAsync("password");
          await client.TerminatedReadAsync(">", TimeSpan.FromMilliseconds(timeoutMs));
        }
      }
    }

    [Fact(Timeout = 5000)]
    public async Task ShouldRespondWithWan2Info()
    {
      using (var stream = new DummyByteStream())
      {
        using (var client = new Client(stream, new CancellationToken()))
        {
          client.IsConnected.Should().Be(true);
          (await client.TryLoginAsync("username", "password", timeoutMs)).Should().Be(true);
          await client.WriteLineAsync("show statistic wan2");
          var s = await client.TerminatedReadAsync(">", TimeSpan.FromMilliseconds(timeoutMs));
          s.Should().Contain(">");
          s.Should().Contain("WAN2");
        }
      }
    }

    [Fact(Timeout = 5000)]
    public async Task ShouldRespondWithWan2InfoRfc854()
    {
      using (var stream = new DummyByteStream(Client.Rfc854LineFeed))
      {
        using (var client = new Client(stream, new CancellationToken()))
        {
          client.IsConnected.Should().Be(true);
          (await (client.TryLoginAsync("username", "password", timeoutMs, lineFeed: Client.Rfc854LineFeed))).Should().Be(true);
          await client.WriteLineRfc854Async("show statistic wan2");
          var s = await client.TerminatedReadAsync(">", TimeSpan.FromMilliseconds(timeoutMs));
          s.Should().Contain(">");
          s.Should().Contain("WAN2");
        }
      }
    }

    [Fact(Timeout = 5000)]
    public async Task ShouldLogin()
    {
      using (var stream = new DummyByteStream())
      {
        using (var client = new Client(stream, new CancellationToken()))
        {
          client.IsConnected.Should().Be(true);
          (await client.TryLoginAsync("username", "password", timeoutMs)).Should().Be(true);
        }
      }
    }

    [Fact(Timeout = 5000)]
    public async Task ShouldLoginCrLf()
    {
      using (var stream = new DummyByteStream(Client.Rfc854LineFeed))
      {
        using (var client = new Client(stream, new CancellationToken()))
        {
          client.IsConnected.Should().Be(true);
          (await client.TryLoginAsync("username", "password", timeoutMs, lineFeed: Client.Rfc854LineFeed)).Should().Be(true);
        }
      }
    }


    [Fact]
    public async Task ShouldRespondWithWan2InfoRegexTerminated()
    {
      using (var stream = new DummyByteStream())
      {
        using (var client = new Client(stream, new CancellationToken()))
        {
          client.IsConnected.Should().Be(true);
          (await client.TryLoginAsync("username", "password", 1500)).Should().Be(true);
          await client.WriteLineAsync("show statistic wan2");
          var s = await client.TerminatedReadAsync(new Regex(".*>$"), TimeSpan.FromMilliseconds(timeoutMs));
          s.Should().Contain(">");
          s.Should().Contain("WAN2");
        }
      }
    }
  }
}
