namespace telnet_cs.Tests
{
  using System;
  using System.Collections.Generic;
  using System.Linq;
  using System.Text;
  using System.Threading;
  using System.Threading.Tasks;
  using FluentAssertions;
  using Xunit;
  using telnet_cs.Client;
  using telnet_cs.IO;

  public class EnvironmentTests
  {
    private static byte[] L(string text) => Encoding.Latin1.GetBytes(text);

    private static byte[] Concat(params byte[][] parts) => [.. parts.SelectMany(static p => p)];

    private static async Task<(string Output, ScriptedStream Stream)> ReadHandlerOnceAsync(
      Action<ByteStreamHandler> configure, params int[] reads)
    {
      var stream = new ScriptedStream(reads);
      using var cts = new CancellationTokenSource();
      using var sut = new ByteStreamHandler(stream, cts, 1);
      configure(sut);
      var output = await sut.ReadAsync(TimeSpan.FromMilliseconds(50));
      return (output, stream);
    }

    private static void ConfigureFull(ByteStreamHandler sut)
    {
      sut.EnvironmentUser = "bob";
      sut.EnvironmentDisplay = "host:0";
      sut.EnvironmentUserVars = new Dictionary<string, string>(StringComparer.Ordinal) { ["ROLE"] = "admin" };
    }

    private static byte[] ExpectedIsFrame(byte verb, params byte[][] entries)
    {
      var payload = new List<byte> { verb };
      foreach (var entry in entries)
      {
        payload.AddRange(entry);
      }

      var frame = new List<byte> { 255, 250, 36 };
      foreach (var b in payload)
      {
        frame.Add(b);
        if (b == 255)
        {
          frame.Add(b);
        }
      }

      frame.AddRange([255, 240]);
      return [.. frame];
    }

    private static byte[] UserEntry() => Concat([0], L("USER"), [1], L("bob"));
    private static byte[] DisplayEntry() => Concat([0], L("DISPLAY"), [1], L("host:0"));
    private static byte[] RoleEntry() => Concat([3], L("ROLE"), [1], L("admin"));

    [Fact]
    public async Task EnvironSend_AllRequested_ReturnsExactIsFrame()
    {
      var (output, stream) = await ReadHandlerOnceAsync(ConfigureFull, 255, 250, 36, 1, 0, 3, 255, 240);
      output.Should().BeEmpty();
      stream.ByteWrites.Should().ContainSingle().Which.Should()
        .Equal(ExpectedIsFrame(0, UserEntry(), DisplayEntry(), RoleEntry()));
    }

    [Fact]
    public async Task EnvironSend_VarRequest_ExcludesUserVars()
    {
      var (output, stream) = await ReadHandlerOnceAsync(ConfigureFull, 255, 250, 36, 1, 0, 255, 240);
      output.Should().BeEmpty();
      var frame = stream.ByteWrites.Should().ContainSingle().Subject;
      frame.Should().Equal(ExpectedIsFrame(0, UserEntry(), DisplayEntry()));
    }

    [Fact]
    public async Task EnvironSend_UserVarOnly_OmitsWellKnown()
    {
      var (output, stream) = await ReadHandlerOnceAsync(ConfigureFull, 255, 250, 36, 1, 3, 255, 240);
      output.Should().BeEmpty();
      stream.ByteWrites.Should().ContainSingle().Which.Should().Equal(ExpectedIsFrame(0, RoleEntry()));
    }

    [Fact]
    public async Task EnvironSend_OrderMirrored_UserVarFirst()
    {
      var (output, stream) = await ReadHandlerOnceAsync(ConfigureFull, 255, 250, 36, 1, 3, 0, 255, 240);
      output.Should().BeEmpty();
      stream.ByteWrites.Should().ContainSingle().Which.Should()
        .Equal(ExpectedIsFrame(0, RoleEntry(), UserEntry(), DisplayEntry()));
    }

    [Fact]
    public async Task EnvironSend_EmptyRequest_ReturnsDefaults()
    {
      var (output, stream) = await ReadHandlerOnceAsync(ConfigureFull, 255, 250, 36, 1, 255, 240);
      output.Should().BeEmpty();
      stream.ByteWrites.Should().ContainSingle().Which.Should()
        .Equal(ExpectedIsFrame(0, UserEntry(), DisplayEntry(), RoleEntry()));
    }

    [Fact]
    public async Task EnvironSend_NothingConfigured_ReturnsBareIs()
    {
      var (output, stream) = await ReadHandlerOnceAsync(static _ => { }, 255, 250, 36, 1, 0, 255, 240);
      output.Should().BeEmpty();
      stream.ByteWrites.Should().ContainSingle().Which.Should().Equal(new byte[] { 255, 250, 36, 0, 255, 240 });
    }

    [Fact]
    public async Task EnvironSend_StrayIs_GetsWont()
    {
      var (output, stream) = await ReadHandlerOnceAsync(ConfigureFull, 255, 250, 36, 0, 255, 240);
      output.Should().BeEmpty();
      stream.ByteWrites.Should().ContainSingle().Which.Should().Equal(new byte[] { 255, 252, 36 });
    }

    [Fact]
    public async Task EnvironSend_ValueByte_Escaped()
    {
      static void Configure(ByteStreamHandler sut) =>
        sut.EnvironmentUserVars = new Dictionary<string, string>(StringComparer.Ordinal) { ["K"] = "a\u0001b" };
      var (output, stream) = await ReadHandlerOnceAsync(Configure, 255, 250, 36, 1, 3, 255, 240);
      output.Should().BeEmpty();
      var entry = Concat([3], L("K"), [1, (byte)'a', 2, 1, (byte)'b']);
      stream.ByteWrites.Should().ContainSingle().Which.Should().Equal(ExpectedIsFrame(0, entry));
    }

    [Fact]
    public async Task EnvironSend_Latin1Ff_IacDoubled()
    {
      static void Configure(ByteStreamHandler sut) =>
        sut.EnvironmentUserVars = new Dictionary<string, string>(StringComparer.Ordinal) { ["K"] = "ÿ" };
      var (output, stream) = await ReadHandlerOnceAsync(Configure, 255, 250, 36, 1, 3, 255, 240);
      output.Should().BeEmpty();
      var entry = Concat([3], L("K"), [1, 255]);
      stream.ByteWrites.Should().ContainSingle().Which.Should().Equal(ExpectedIsFrame(0, entry));
    }

    [Fact]
    public async Task DoOldEnvironment_GetsWill()
    {
      var (output, stream) = await ReadHandlerOnceAsync(static _ => { }, 255, 253, 36);
      output.Should().BeEmpty();
      stream.ByteWrites.Should().ContainSingle().Which.Should().Equal(new byte[] { 255, 251, 36 });
    }

    [Fact]
    public async Task WillNewEnvironment_GetsDont()
    {
      var (output, stream) = await ReadHandlerOnceAsync(static _ => { }, 255, 251, 39);
      output.Should().BeEmpty();
      stream.ByteWrites.Should().ContainSingle().Which.Should().Equal(new byte[] { 255, 254, 39 });
    }

    [Fact]
    public async Task DoNewEnvironment_GetsWont()
    {
      var (output, stream) = await ReadHandlerOnceAsync(static _ => { }, 255, 253, 39);
      output.Should().BeEmpty();
      stream.ByteWrites.Should().ContainSingle().Which.Should().Equal(new byte[] { 255, 252, 39 });
    }

    private static int CountSubnegotiations(ScriptedStream stream) =>
      stream.ByteWrites.Count(static w => w.Length > 3 && w[0] == 255 && w[1] == 250 && w[2] == 36);

    private static async Task<string> ReadClientOnceAsync(Client client)
    {
      return await client.ReadAsync(TimeSpan.FromMilliseconds(100));
    }

    [Fact]
    public async Task EnvironInfo_SentWhenValuesChangeAfterAgreement()
    {
      using (GlobalStateGuard.SkipProactive(true))
      {
        using var stream = new ScriptedStream();
        using var client = new Client(stream, new CancellationToken());
        stream.Enqueue(255, 253, 36);
        (await ReadClientOnceAsync(client)).Should().BeEmpty();
        client.Settings.EnvironmentUser = "carol";
        (await ReadClientOnceAsync(client)).Should().BeEmpty();
        var expected = ExpectedIsFrame(2, Concat([0], L("USER"), [1], L("carol")));
        stream.ByteWrites.Should().ContainSingle(w => w.Length > 3 && w[1] == 250).Which.Should().Equal(expected);
      }
    }

    [Fact]
    public async Task EnvironInfo_NotSentWhenPeerDisagreed()
    {
      using (GlobalStateGuard.SkipProactive(true))
      {
        using var stream = new ScriptedStream();
        using var client = new Client(stream, new CancellationToken());
        (await ReadClientOnceAsync(client)).Should().BeEmpty();
        client.Settings.EnvironmentUser = "carol";
        (await ReadClientOnceAsync(client)).Should().BeEmpty();
        CountSubnegotiations(stream).Should().Be(0);
      }
    }

    [Fact]
    public async Task EnvironInfo_NotResentWhenUnchanged()
    {
      using (GlobalStateGuard.SkipProactive(true))
      {
        using var stream = new ScriptedStream();
        using var client = new Client(stream, new CancellationToken());
        stream.Enqueue(255, 253, 36);
        (await ReadClientOnceAsync(client)).Should().BeEmpty();
        client.Settings.EnvironmentUser = "carol";
        (await ReadClientOnceAsync(client)).Should().BeEmpty();
        (await ReadClientOnceAsync(client)).Should().BeEmpty();
        CountSubnegotiations(stream).Should().Be(1);
      }
    }
  }
}
