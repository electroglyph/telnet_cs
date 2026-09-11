namespace telnet_cs.Tests
{
  using System;
  using System.Linq;
  using System.Threading;
  using System.Threading.Tasks;
  using FluentAssertions;
  using Xunit;
  using telnet_cs.Client;
  using telnet_cs.IO;
  using telnet_cs.Protocol;

  public class ClientNegotiationTests
  {
    private static int CountWrites(ScriptedStream stream, byte verb, byte option)
    {
      return stream.ByteWrites.Count(b =>
        b.Length == 3 && b[0] == 255 && b[1] == verb && b[2] == option);
    }

    private static async Task<string> ReadOnceAsync(Client client)
    {
      return await client.ReadAsync(TimeSpan.FromMilliseconds(100));
    }

    private static async Task<string> ReadHandlerOnceAsync(params int[] reads)
    {
      using var stream = new ScriptedStream(reads);
      using var cts = new CancellationTokenSource();
      using var sut = new ByteStreamHandler(stream, cts, 1);
      return await sut.ReadAsync(TimeSpan.FromMilliseconds(50));
    }

    [Fact]
    public async Task RepeatedDoAcrossReads_RepliesOnce()
    {
      using (GlobalStateGuard.SkipProactive(true))
      {
        using var stream = new ScriptedStream();
        using var client = new Client(stream, new CancellationToken());
        stream.Enqueue(255, 253, 3);
        (await ReadOnceAsync(client)).Should().BeEmpty();
        stream.Enqueue(255, 253, 3);
        (await ReadOnceAsync(client)).Should().BeEmpty();
        CountWrites(stream, 251, 3).Should().Be(1);
      }
    }

    [Fact]
    public async Task RepeatedWillAcrossReads_RepliesOnce()
    {
      using (GlobalStateGuard.SkipProactive(true))
      {
        using var stream = new ScriptedStream();
        using var client = new Client(stream, new CancellationToken());
        stream.Enqueue(255, 251, 3);
        (await ReadOnceAsync(client)).Should().BeEmpty();
        stream.Enqueue(255, 251, 3);
        (await ReadOnceAsync(client)).Should().BeEmpty();
        CountWrites(stream, 253, 3).Should().Be(1);
      }
    }

    [Fact]
    public async Task WontAfterWill_AcksDisableThenHonoursNewStimulus()
    {
      using (GlobalStateGuard.SkipProactive(true))
      {
        using var stream = new ScriptedStream();
        using var client = new Client(stream, new CancellationToken());
        stream.Enqueue(255, 251, 24);
        (await ReadOnceAsync(client)).Should().BeEmpty();
        stream.Enqueue(255, 252, 24);
        (await ReadOnceAsync(client)).Should().BeEmpty();
        stream.Enqueue(255, 251, 24);
        (await ReadOnceAsync(client)).Should().BeEmpty();
        stream.Enqueue(255, 251, 24);
        (await ReadOnceAsync(client)).Should().BeEmpty();
        CountWrites(stream, 253, 24).Should().Be(2);
        CountWrites(stream, 254, 24).Should().Be(1);
      }
    }

    [Fact]
    public async Task WontDontOptionBytes_AreConsumedNotData()
    {
      // The option byte belongs to the command: it must not leak into
      // output (3 would surface as "^C", 1 as SOH mapping).
      (await ReadHandlerOnceAsync(255, 252, 3)).Should().BeEmpty();
      (await ReadHandlerOnceAsync(255, 254, 1)).Should().BeEmpty();
    }

    [Fact]
    public async Task Refusal_AllowsExplicitReRequest()
    {
      using (GlobalStateGuard.SkipProactive(true))
      {
        using var stream = new ScriptedStream();
        using var client = new Client(stream, TimeSpan.FromMilliseconds(10), default,
          new[] { (Commands.Do, Options.Echo) });
        CountWrites(stream, 253, 1).Should().Be(1);
        stream.Enqueue(255, 252, 1);
        (await ReadOnceAsync(client)).Should().BeEmpty();
        stream.ByteWrites.Should().HaveCount(1);
        client.Negotiation.WasRefusedByPeer(1).Should().BeTrue();
        await client.RequestEnableAsync(Options.Echo);
        CountWrites(stream, 253, 1).Should().Be(2);
        client.Negotiation.WasRefusedByPeer(1).Should().BeFalse();
      }
    }

    [Fact]
    public async Task QueuedDisable_DrainsOnCompletion()
    {
      using (GlobalStateGuard.SkipProactive(true))
      {
        using var stream = new ScriptedStream();
        using var client = new Client(stream, new CancellationToken());
        await client.RequestEnableAsync(Options.SuppressGoAhead);
        CountWrites(stream, 253, 3).Should().Be(1);
        await client.RequestDisableAsync(Options.SuppressGoAhead);
        stream.ByteWrites.Should().HaveCount(1);
        stream.Enqueue(255, 251, 3);
        (await ReadOnceAsync(client)).Should().BeEmpty();
        CountWrites(stream, 254, 3).Should().Be(1);
        client.Negotiation.IsEnabledByPeer(3).Should().BeFalse();
        stream.Enqueue(255, 252, 3);
        (await ReadOnceAsync(client)).Should().BeEmpty();
        stream.ByteWrites.Should().HaveCount(2);
      }
    }

    [Fact]
    public async Task OutstandingEnable_SuppressesSecondRequest()
    {
      using (GlobalStateGuard.SkipProactive(true))
      {
        using var stream = new ScriptedStream();
        using var client = new Client(stream, new CancellationToken());
        await client.RequestEnableAsync(Options.TerminalType);
        await client.RequestEnableAsync(Options.TerminalType);
        CountWrites(stream, 253, 24).Should().Be(1);
      }
    }
  }
}
