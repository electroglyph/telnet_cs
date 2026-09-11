// Regression tests for the async-only audit (audit.md P0/P1 + approved features).
// All tests here pin PROPER behavior of the fixed sources; they are async-only.
namespace telnet_cs.CiTests
{
  using System;
  using System.Collections.Generic;
  using System.Collections.ObjectModel;
  using System.IO;
  using System.Linq;
  using System.Net;
  using System.Net.Sockets;
  using System.Text;
  using System.Text.RegularExpressions;
  using System.Threading;
  using System.Threading.Tasks;
  using FakeItEasy;
  using FluentAssertions;
  using Xunit;
  /// <summary>
  /// In-memory scripted stream: Available tracks queued reads, ReadByte
  /// returns -1 when drained, and all writes are recorded for assertion.
  /// </summary>
  public sealed class ScriptedStream : IByteStream
  {
    private readonly Queue<int> reads;

    private readonly List<byte[]> byteWrites = new List<byte[]>();
    private readonly List<string> stringWrites = new List<string>();
    private readonly List<byte> singleByteWrites = new List<byte>();

    public ReadOnlyCollection<byte[]> ByteWrites => byteWrites.AsReadOnly();
    public ReadOnlyCollection<string> StringWrites => stringWrites.AsReadOnly();
    public ReadOnlyCollection<byte> SingleByteWrites => singleByteWrites.AsReadOnly();
    public int LastReceiveTimeout { get; private set; } = -1;

    public ScriptedStream(params int[] reads)
    {
      this.reads = new Queue<int>(reads);
    }

    public ScriptedStream(string text)
      : this(ToInts(text))
    {
    }

    /// <summary>
    /// Queues more inbound bytes, so a test can feed separate
    /// <c>ReadAsync</c> calls from one stream.
    /// </summary>
    public void Enqueue(params int[] more)
    {
      foreach (var b in more)
      {
        reads.Enqueue(b);
      }
    }

    private static int[] ToInts(string text)
    {
      var result = new int[text.Length];
      for (var i = 0; i < text.Length; i++)
      {
        result[i] = text[i];
      }

      return result;
    }

    public int Available => reads.Count;

    public bool Connected { get; set; } = true;

    public int ReceiveTimeout
    {
      get => 0;
      set => LastReceiveTimeout = value;    }

    public void Close()
    {
      reads.Clear();
      Connected = false;
    }

    public void Dispose()
    {
      Close();
    }

    public int ReadByte()
    {
      return reads.Count > 0 ? reads.Dequeue() : -1;
    }

    public Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
      var slice = new byte[count];
      Array.Copy(buffer, offset, slice, 0, count);
      byteWrites.Add(slice);
      return Task.CompletedTask;
    }

    public Task WriteAsync(string value, CancellationToken cancellationToken)
    {
      stringWrites.Add(value);
      return Task.CompletedTask;
    }

    public Task WriteByteAsync(byte value, CancellationToken cancellationToken)
    {
      singleByteWrites.Add(value);
      return Task.CompletedTask;
    }
  }

  public class AuditHandlerRegressionTests
  {
    private static ByteStreamHandler MakeHandler(ScriptedStream stream, out CancellationTokenSource cts, int readDelayMs = 1)
    {
      cts = new CancellationTokenSource();
      return new ByteStreamHandler(stream, cts, readDelayMs);
    }

    [Fact]
    public void CtorNullStreamThrows()
    {
      Action act = () => new ByteStreamHandler((IByteStream)null!);
      act.Should().Throw<ArgumentNullException>().WithParameterName("byteStream");
    }

    [Fact]
    public void CtorNullTokenSourceThrows()
    {
      using var stream = new ScriptedStream();
      Action act = () => new ByteStreamHandler(stream, (CancellationTokenSource)null!);
      act.Should().Throw<ArgumentNullException>().WithParameterName("internalCancellation");
    }

    [Fact]
    public async Task NawsReplyIsBinaryRfc1073()
    {
      using var stream = new ScriptedStream(255, 253, 31);
      using var handler = MakeHandler(stream, out var cts);
      using (cts)
      {
        handler.WindowWidth = 132;
        handler.WindowHeight = 37;
        (await handler.ReadAsync(TimeSpan.FromMilliseconds(100))).Should().BeEmpty();
      }

      var expected = new byte[] { 255, 250, 31, 0, 0, 132, 0, 37, 255, 240 };
      stream.ByteWrites.Should().HaveCount(2);
      stream.ByteWrites[1].Should().Equal(expected);
    }

    [Fact]
    public async Task DuplicateNegotiationIsSuppressed()
    {
      // RFC 1143-lite: the same (verb, option) twice in a row gets one reply.
      using var stream = new ScriptedStream(255, 253, 3, 255, 253, 3);
      using var handler = MakeHandler(stream, out var cts);
      using (cts)
      {
        (await handler.ReadAsync(TimeSpan.FromMilliseconds(100))).Should().BeEmpty();
      }

      var replies = stream.ByteWrites.Where(b => b.Length == 3 && b[0] == 255 && b[1] == 251 && b[2] == 3).ToList();
      replies.Should().HaveCount(1);
    }

    [Fact]
    public async Task BinaryOptionIsAgreed()
    {
      using var stream = new ScriptedStream(255, 253, 0); // DO TransmitBinary
      using var handler = MakeHandler(stream, out var cts);
      using (cts)
      {
        (await handler.ReadAsync(TimeSpan.FromMilliseconds(100))).Should().BeEmpty();
      }

      stream.ByteWrites.Should().ContainSingle()
        .Which.Should().Equal(new byte[] { 255, 251, 0 });
    }

    [Fact]
    public async Task IacInOptionPositionIsIgnored()
    {
      using var stream = new ScriptedStream(255, 253, 255);
      using var handler = MakeHandler(stream, out var cts);
      using (cts)
      {
        (await handler.ReadAsync(TimeSpan.FromMilliseconds(100))).Should().BeEmpty();
      }

      stream.ByteWrites.Should().BeEmpty();
    }

    [Fact]
    public async Task TruncatedOptionIsIgnored()
    {
      using var stream = new ScriptedStream(255, 253);
      using var handler = MakeHandler(stream, out var cts);
      using (cts)
      {
        (await handler.ReadAsync(TimeSpan.FromMilliseconds(100))).Should().BeEmpty();
      }

      stream.ByteWrites.Should().BeEmpty();
    }

    [Fact]
    public async Task LongSubnegotiationResynchronises()
    {
      // 600-byte payload exceeds the 512 cap: consumed for resync, ignored,
      // and the trailing text still parses.
      var script = new List<int> { 255, 250, 31, 1 };
      for (var i = 0; i < 600; i++)
      {
        script.Add(65);
      }

      script.AddRange(new[] { 255, 240, 72, 73 });
      using var stream = new ScriptedStream(script.ToArray());
      using var handler = MakeHandler(stream, out var cts);
      string result;
      using (cts)
      {
        result = await handler.ReadAsync(TimeSpan.FromMilliseconds(200));
      }

      result.Should().Be("HI");
      stream.ByteWrites.Should().BeEmpty();
    }

    [Fact]
    public async Task EscapedIacInsideSubnegotiation()
    {
      using var stream = new ScriptedStream(255, 250, 24, 1, 255, 255, 255, 240, 79, 75);
      using var handler = MakeHandler(stream, out var cts);
      string result;
      using (cts)
      {
        result = await handler.ReadAsync(TimeSpan.FromMilliseconds(100));
      }

      result.Should().Be("OK");
      var expected = new byte[] { 255, 250, 24, 0 }
        .Concat(Encoding.ASCII.GetBytes("vt100"))
        .Concat(new byte[] { 255, 240 }).ToArray();
      stream.ByteWrites.Should().ContainSingle()
        .Which.Should().Equal(expected);
    }

    [Fact]
    public async Task TruncatedSubnegotiationAbortsSilently()
    {
      using var stream = new ScriptedStream(255, 250, 24, 1);
      using var handler = MakeHandler(stream, out var cts);
      using (cts)
      {
        (await handler.ReadAsync(TimeSpan.FromMilliseconds(100))).Should().BeEmpty();
      }

      stream.ByteWrites.Should().BeEmpty();
    }

    [Fact]
    public async Task MalformedSubnegotiationSendsWont()
    {
      using var stream = new ScriptedStream(255, 250, 24, 0, 255, 240);
      using var handler = MakeHandler(stream, out var cts);
      using (cts)
      {
        (await handler.ReadAsync(TimeSpan.FromMilliseconds(100))).Should().BeEmpty();
      }

      stream.ByteWrites.Should().ContainSingle()
        .Which.Should().Equal(new byte[] { 255, 252, 24 });
    }

    [Fact]
    public async Task BackspaceDeletesPreviousChar()
    {
      using var stream = new ScriptedStream(65, 66, 8, 67);
      using var handler = MakeHandler(stream, out var cts);
      string result;
      using (cts)
      {
        result = await handler.ReadAsync(TimeSpan.FromMilliseconds(100));
      }

      result.Should().Be("AC");
    }

    [Fact]
    public async Task BackspaceOnEmptyIsHarmless()
    {
      using var stream = new ScriptedStream(8, 65);
      using var handler = MakeHandler(stream, out var cts);
      string result;
      using (cts)
      {
        result = await handler.ReadAsync(TimeSpan.FromMilliseconds(100));
      }

      result.Should().Be("A");
    }

    [Fact]
    public async Task CrNulMeansBareCr()
    {
      using var stream = new ScriptedStream(65, 13, 0, 66);
      using var handler = MakeHandler(stream, out var cts);
      string result;
      using (cts)
      {
        result = await handler.ReadAsync(TimeSpan.FromMilliseconds(100));
      }

      result.Should().Be("A\rB");
    }

    [Fact]
    public async Task CrLfPassesThrough()
    {
      using var stream = new ScriptedStream(65, 13, 10, 66);
      using var handler = MakeHandler(stream, out var cts);
      string result;
      using (cts)
      {
        result = await handler.ReadAsync(TimeSpan.FromMilliseconds(100));
      }

      result.Should().Be("A\r\nB");
    }

    [Fact]
    public async Task BellSuppressedWhenDisabled()
    {
      using var stream = new ScriptedStream(7);
      using var handler = MakeHandler(stream, out var cts);
      using (cts)
      {
        handler.EnableBell = false;
        (await handler.ReadAsync(TimeSpan.FromMilliseconds(100))).Should().BeEmpty();
      }
    }

    [Fact]
    public async Task TerminalTypeIsLatin1Encoded()
    {
      // é must go out as single byte 0xE9 (Latin-1), not UTF-8 0xC3 0xA9.
      using var stream = new ScriptedStream(255, 250, 24, 1, 255, 240);
      using var handler = MakeHandler(stream, out var cts);
      using (cts)
      {
        handler.TerminalType = "caf\u00E9";
        (await handler.ReadAsync(TimeSpan.FromMilliseconds(100))).Should().BeEmpty();
      }

      var expected = new byte[] { 255, 250, 24, 0, 99, 97, 102, 233, 255, 240 };
      stream.ByteWrites.Should().ContainSingle()
        .Which.Should().Equal(expected);
    }

    [Fact]
    public async Task LogHookCapturesProtocolNotes()
    {
      using var stream = new ScriptedStream(21);
      var logged = new List<string>();
      using var handler = MakeHandler(stream, out var cts);
      string result;
      using (cts)
      {
        handler.Log = logged.Add;
        result = await handler.ReadAsync(TimeSpan.FromMilliseconds(100));
      }

      result.Should().Contain("NAK");
      logged.Should().Contain(m => m.Contains("NAK"));
    }

    [Fact]
    public async Task ReceiveTimeoutIsClampedToIntMax()
    {
      using var stream = new ScriptedStream(88); // 'X'
      using var cts = new CancellationTokenSource(50);
      using var handler = new ByteStreamHandler(stream, cts, 1);
      var result = await handler.ReadAsync(TimeSpan.FromMilliseconds((double)int.MaxValue + 1000));
      result.Should().Be("X");
      stream.LastReceiveTimeout.Should().Be(int.MaxValue);
    }

    [Fact]
    public async Task CancelledHandlerReturnsEmpty()
    {
      using var stream = new ScriptedStream("AB");
      using var cts = new CancellationTokenSource();
      cts.Cancel();
      using var handler = new ByteStreamHandler(stream, cts, 1);
      (await handler.ReadAsync(TimeSpan.FromMilliseconds(100))).Should().BeEmpty();
    }
  }

  public class AuditClientRegressionTests
  {
    internal sealed class ProbeClient : Client
    {
      public ProbeClient(IByteStream stream)
        : base(stream, new CancellationToken())
      {
      }

      public static bool Locate(string terminator, string s)
      {
        return IsTerminatorLocated(terminator, s);
      }

      public void ExposeSendCancel()
      {
        SendCancel();
      }
    }

    [Fact]
    public void TerminatorMatchingIsOrdinal()
    {
      using var script = new ScriptedStream();
      using var probe = new ProbeClient(script);
      probe.Should().NotBeNull();
      ProbeClient.Locate("AB", "xxAByy").Should().BeTrue();
      // A NUL inside the terminator is matched literally (ordinal), never
      // ignored the way culture-aware comparison ignores it.
      ProbeClient.Locate("a\0b", "xa\0by").Should().BeTrue();
      ProbeClient.Locate("AB", "xxAByy".Replace("AB", "ab", StringComparison.Ordinal)).Should().BeFalse();
    }

    [Fact]
    public async Task WriteAsyncNullStringThrows()
    {
      using var stream = new ScriptedStream();
      using var client = new Client(stream, new CancellationToken());
      Func<Task> act = () => client.WriteAsync((string)null!);
      (await act.Should().ThrowAsync<ArgumentNullException>())
        .WithParameterName("command");
    }

    [Fact]
    public async Task NullTerminatorThrows()
    {
      using var stream = new ScriptedStream();
      using var client = new Client(stream, new CancellationToken());
      Func<Task> act = () => client.TerminatedReadAsync((string)null!, TimeSpan.FromMilliseconds(10));
      (await act.Should().ThrowAsync<ArgumentNullException>())
        .WithParameterName("terminator");
    }

    [Fact]
    public async Task NullRegexThrows()
    {
      using var stream = new ScriptedStream();
      using var client = new Client(stream, new CancellationToken());
      Func<Task> act = () => client.TerminatedReadAsync((Regex)null!, TimeSpan.FromMilliseconds(10));
      (await act.Should().ThrowAsync<ArgumentNullException>())
        .WithParameterName("regex");
    }

    [Fact]
    public async Task NullTerminatorCollectionThrows()
    {
      using var stream = new ScriptedStream();
      using var client = new Client(stream, new CancellationToken());
      Func<Task> act = () => client.TerminatedReadAsync((IEnumerable<string>)null!, TimeSpan.FromMilliseconds(10));
      (await act.Should().ThrowAsync<ArgumentNullException>())
        .WithParameterName("terminators");
    }

    [Fact]
    public async Task MultiTerminatorMatchesAny()
    {
      using var stream = new DummyByteStream();
      using var client = new Client(stream, new CancellationToken());
      var result = await client.TerminatedReadAsync(
        new[] { "Nope>", "Account:" }, TimeSpan.FromSeconds(5), 1);
      result.Should().Be("Account:");
    }

    [Fact]
    public async Task SettingsTerminalTypeOverridesStatic()
    {
      var prior = Client.TerminalType;
      try
      {
        Client.TerminalType = "vt100";
        using var stream = new ScriptedStream(255, 250, 24, 1, 255, 240);
        using var client = new Client(stream, new CancellationToken());
        client.Settings.TerminalType = "xterm";
        await client.ReadAsync(TimeSpan.FromMilliseconds(200));
        stream.ByteWrites.Should().HaveCount(2);
        stream.ByteWrites[1].Should().Equal(
          new byte[] { 255, 250, 24, 0, 120, 116, 101, 114, 109, 255, 240 });
        Client.TerminalType.Should().Be("vt100");
      }
      finally
      {
        Client.TerminalType = prior;
      }
    }

    [Fact]
    public async Task CustomTextEncodingPreEncodesWrites()
    {
      using var stream = new ScriptedStream();
      using var client = new Client(stream, new CancellationToken());
      client.Settings.TextEncoding = Encoding.UTF8;
      await client.WriteAsync("caf\u00E9");
      stream.StringWrites.Should().BeEmpty();
      stream.ByteWrites.Should().HaveCount(2); // ctor negotiation + encoded write
      stream.ByteWrites[1].Should().Equal(new byte[] { 99, 97, 102, 195, 169 });
    }

    [Fact]
    public async Task CancelledClientReadReturnsEmpty()
    {
      using var stream = new ScriptedStream("AB");
      using var cts = new CancellationTokenSource();
      using var client = new Client(stream, cts.Token);
      cts.Cancel();
      (await client.ReadAsync(TimeSpan.FromMilliseconds(100))).Should().BeEmpty();
    }

    [Fact]
    public async Task ConcurrentReadsAreSerialised()
    {
      using var stream = new ScriptedStream("AB");
      using var client = new Client(stream, new CancellationToken());
      var first = client.ReadAsync(TimeSpan.FromMilliseconds(100));
      var second = client.ReadAsync(TimeSpan.FromMilliseconds(100));
      var results = await Task.WhenAll(first, second);
      results.Should().Contain("AB").And.Contain(string.Empty);
    }

    [Fact]
    public void ConnectAsyncNullHostnameThrows()
    {
      Action act = () => Client.ConnectAsync(null!, 23).GetAwaiter().GetResult();
      act.Should().Throw<ArgumentNullException>().WithParameterName("hostname");
    }

    [Fact]
    public async Task ConnectAsyncCancelledThrows()
    {
      Func<Task> act = () => Client.ConnectAsync("127.0.0.1", 1, new CancellationToken(true));
      await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ConnectAsyncClosedPortThrowsSocketException()
    {
      using var listener = new TcpListener(IPAddress.Loopback, 0);
      listener.Start();
      var port = ((IPEndPoint)listener.LocalEndpoint).Port;
      listener.Stop();
      Func<Task> act = () => Client.ConnectAsync("127.0.0.1", port, default, TimeSpan.FromSeconds(5));
      await act.Should().ThrowAsync<SocketException>();
    }

    [Fact]
    public async Task ConnectAsyncConnectsToDummyServer()
    {
      using var server = new DummyTelnetServer();
      using var client = await Client.ConnectAsync(
        server.IPAddress.ToString(), server.Port, default, TimeSpan.FromSeconds(5));
      client.IsConnected.Should().BeTrue();
    }
  }

  public class AuditStreamRegressionTests
  {
    [Fact]
    public void GetStreamReturnsCachedInstance()
    {
      using var server = new DummyTelnetServer();
      using var socket = new telnet_cs.TcpClient(server.IPAddress.ToString(), server.Port);
      socket.GetStream().Should().BeSameAs(socket.GetStream());
    }

    [Fact]
    public void SocketOptionsPassthrough()
    {
      var socket = A.Fake<ISocket>();
      A.CallTo(() => socket.GetStream()).Returns(A.Fake<INetworkStream>());
      using var sut = new TcpByteStream(socket);
      A.CallTo(() => socket.NoDelay).Returns(true);
      A.CallTo(() => socket.KeepAlive).Returns(true);
      A.CallTo(() => socket.SendTimeout).Returns(321);
      sut.NoDelay.Should().BeTrue();
      sut.KeepAlive.Should().BeTrue();
      sut.SendTimeout.Should().Be(321);
      sut.NoDelay = false;
      sut.KeepAlive = false;
      sut.SendTimeout = 654;
      A.CallToSet(() => socket.NoDelay).To(false).MustHaveHappened();
      A.CallToSet(() => socket.KeepAlive).To(false).MustHaveHappened();
      A.CallToSet(() => socket.SendTimeout).To(654).MustHaveHappened();
    }

    [Fact]
    public void ReadByteWhenDisconnectedReturnsMinusOne()
    {
      var socket = A.Fake<ISocket>();
      A.CallTo(() => socket.Connected).Returns(false);
      var stream = A.Fake<INetworkStream>();
      A.CallTo(() => socket.GetStream()).Returns(stream);
      using var sut = new TcpByteStream(socket);
      sut.ReadByte().Should().Be(-1);
      A.CallTo(() => stream.ReadByte()).MustNotHaveHappened();
    }
  }

  public class AuditCoverageTopUpTests
  {
    [Fact]
    public async Task Utf8TextEncodingDecodesMultibyteRead()
    {
      // "héllo" as UTF-8 bytes; the legacy path would return Latin-1 mojibake.
      using var stream = new ScriptedStream(104, 195, 169, 108, 108, 111);
      using var cts = new CancellationTokenSource();
      using var handler = new ByteStreamHandler(stream, cts, 1);
      handler.TextEncoding = Encoding.UTF8;
      var result = await handler.ReadAsync(TimeSpan.FromMilliseconds(100));
      result.Should().Be("h\u00E9llo");
    }

    [Fact]
    public async Task StaticTraceHookCapturesLog()
    {
      var captured = new List<string>();
      ByteStreamHandler.Trace = captured.Add;
      try
      {
        using var stream = new ScriptedStream(3); // ETX -> WriteLog("^C")
        using var cts = new CancellationTokenSource();
        using var handler = new ByteStreamHandler(stream, cts, 1);
        var result = await handler.ReadAsync(TimeSpan.FromMilliseconds(100));
        result.Should().Be("^C");
        captured.Should().Contain("^C");
      }
      finally
      {
        ByteStreamHandler.Trace = null;
      }
    }

    [Fact]
    public async Task MultiRegexMatchesViaDummyLoginFlow()
    {
      using var stream = new DummyByteStream();
      using var client = new Client(stream, new CancellationToken());
      var result = await client.TerminatedReadAsync(
        new[] { new Regex("Nope>"), new Regex("Account") }, TimeSpan.FromSeconds(5), 1);
      result.Should().Be("Account:");
    }

    [Fact]
    public async Task EmptyTerminatorCollectionReadsToTimeout()
    {
      using var stream = new ScriptedStream("AB");
      using var client = new Client(stream, new CancellationToken());
      var result = await client.TerminatedReadAsync(
        new string[0], TimeSpan.FromMilliseconds(50), 1);
      result.Should().Be("AB");
    }

    [Fact]
    public async Task EmptyRegexCollectionReadsToTimeout()
    {
      using var stream = new ScriptedStream("AB");
      using var client = new Client(stream, new CancellationToken());
      var result = await client.TerminatedReadAsync(
        new Regex[0], TimeSpan.FromMilliseconds(50), 1);
      result.Should().Be("AB");
    }

    [Fact]
    public async Task NawsAutoSizeHasRfc1073Shape()
    {
      using var stream = new ScriptedStream(255, 253, 31);
      using var cts = new CancellationTokenSource();
      using var handler = new ByteStreamHandler(stream, cts, 1);
      (await handler.ReadAsync(TimeSpan.FromMilliseconds(100))).Should().BeEmpty();
      stream.ByteWrites.Should().HaveCount(2);
      var naws = stream.ByteWrites[1];
      naws.Should().HaveCount(10);
      naws[0].Should().Be(255);
      naws[1].Should().Be(250);
      naws[2].Should().Be(31);
      naws[8].Should().Be(255);
      naws[9].Should().Be(240);
    }
  }

  public class AuditCoverageTopUp2Tests
  {
    [Fact]
    public async Task IOExceptionDuringSubnegotiationAbortsItSilently()
    {
      // TryReadByte maps IOException to -1: the option byte never arrives,
      // so the truncated SB is ignored without reply or throw.
      var fake = A.Fake<IByteStream>();
      A.CallTo(() => fake.Connected).Returns(true);
      var first = true;
      A.CallTo(() => fake.Available).ReturnsLazily(() =>
      {
        if (first)
        {
          first = false;
          return 1;
        }

        return 0;
      });
      var reads = new Queue<int>(new[] { 255, 250 });
      A.CallTo(() => fake.ReadByte()).ReturnsLazily(() =>
        reads.Count > 0 ? reads.Dequeue() : throw new IOException("boom"));
      using var cts = new CancellationTokenSource();
      using var sut = new ByteStreamHandler(fake, cts, 1);
      (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().BeEmpty();
      A.CallTo(() => fake.WriteAsync(A<byte[]>.Ignored, A<int>.Ignored, A<int>.Ignored, A<CancellationToken>.Ignored)).MustNotHaveHappened();
    }

    [Fact]
    public async Task IacAsSubnegotiationOptionIsIgnored()
    {
      using var stream = new ScriptedStream(255, 250, 255);
      using var cts = new CancellationTokenSource();
      using var sut = new ByteStreamHandler(stream, cts, 1);
      (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().BeEmpty();
      stream.ByteWrites.Should().BeEmpty();
    }

    [Fact]
    public async Task TruncatedSubnegotiationEndingInIacIsIgnored()
    {
      // IAC with no following byte: the scanner cannot frame, so it aborts.
      using var stream = new ScriptedStream(255, 250, 24, 1, 255);
      using var cts = new CancellationTokenSource();
      using var sut = new ByteStreamHandler(stream, cts, 1);
      (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().BeEmpty();
      stream.ByteWrites.Should().BeEmpty();
    }

    [Fact]
    public async Task IacFollowedByDataByteAbortsSubnegotiation()
    {
      // IAC followed by anything but IAC/SE: framing is lost, give up.
      using var stream = new ScriptedStream(255, 250, 24, 1, 255, 65);
      using var cts = new CancellationTokenSource();
      using var sut = new ByteStreamHandler(stream, cts, 1);
      (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().BeEmpty();
      stream.ByteWrites.Should().BeEmpty();
    }

    [Fact]
    public async Task EscapedIacAtPayloadCapMarksOverCap()
    {
      // 512 payload bytes fill the cap; the escaped IAC then trips the
      // over-cap branch while the stream stays in sync through IAC SE.
      var reads = new List<int> { 255, 250, 99 };
      for (var i = 0; i < 512; i++)
      {
        reads.Add(65);
      }

      reads.AddRange(new[] { 255, 255, 255, 240 });
      using var stream = new ScriptedStream(reads.ToArray());
      using var cts = new CancellationTokenSource();
      using var sut = new ByteStreamHandler(stream, cts, 1);
      (await sut.ReadAsync(TimeSpan.FromMilliseconds(100))).Should().BeEmpty();
      stream.ByteWrites.Should().BeEmpty();
    }

    [Fact]
    public async Task NawsEscapesIacBytesInDimensions()
    {
      // A 0xFF dimension byte must be doubled on the wire (RFC 854).
      using var stream = new ScriptedStream(255, 253, 31);
      using var cts = new CancellationTokenSource();
      using var sut = new ByteStreamHandler(stream, cts, 1);
      sut.WindowWidth = 65535;
      sut.WindowHeight = 24;
      (await sut.ReadAsync(TimeSpan.FromMilliseconds(100))).Should().BeEmpty();
      stream.ByteWrites.Should().HaveCount(2);
      stream.ByteWrites[1].Should().Equal(new byte[]
      {
        255, 250, 31, 0, 255, 255, 255, 255, 0, 24, 255, 240,
      });
    }

    [Fact]
    public async Task HandlerIsWriteConsoleWritesRead()
    {
      using var stream = new ScriptedStream("Hi");
      using var cts = new CancellationTokenSource();
      using var sut = new ByteStreamHandler(stream, cts, 1);
      sut.IsWriteConsole = true;
      (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().Be("Hi");
    }

    private sealed class ProbeHandler : ByteStreamHandler
    {
      public ProbeHandler(IByteStream stream, CancellationTokenSource cts)
        : base(stream, cts)
      {
      }

      public void ExposeSendCancel()
      {
        SendCancel();
      }
    }

    [Fact]
    public void HandlerSendCancelAfterDisposeIsSwallowed()
    {
      // Cancel() on a disposed source throws; the handler must swallow it.
      using var stream = new ScriptedStream();
      var cts = new CancellationTokenSource();
      var sut = new ProbeHandler(stream, cts);
      cts.Dispose();
      Action act = () => sut.ExposeSendCancel();
      act.Should().NotThrow();
      sut.Dispose();
    }

    [Fact]
    public async Task SingleStringCollectionOverloadReads()
    {
      using var stream = new ScriptedStream("Account:");
      using var client = new Client(stream, new CancellationToken());
      var result = await client.TerminatedReadAsync(new[] { "zzz", "Account:" });
      result.Should().Be("Account:");
    }

    [Fact]
    public async Task RegexCollectionShortOverloadsRead()
    {
      using var stream = new ScriptedStream("Password:");
      using var client = new Client(stream, new CancellationToken());
      var oneArg = await client.TerminatedReadAsync(new[] { new Regex("zzz"), new Regex("word") });
      oneArg.Should().Be("Password:");
      using var stream2 = new ScriptedStream("Password:");
      using var client2 = new Client(stream2, new CancellationToken());
      var twoArg = await client2.TerminatedReadAsync(
        new[] { new Regex("zzz"), new Regex("word") }, TimeSpan.FromSeconds(2));
      twoArg.Should().Be("Password:");
    }

    [Fact]
    public async Task FailedRegexMatchLogsToBothHooks()
    {
      var logged = new List<string>();
      var traced = new List<string>();
      var priorTrace = Client.Trace;
      Client.Trace = traced.Add;
      try
      {
        using var stream = new ScriptedStream();
        using var client = new Client(stream, new CancellationToken());
        client.Settings.Log = logged.Add;
        var result = await client.TerminatedReadAsync(
          new Regex("ZZZ-never-matches"), TimeSpan.FromMilliseconds(60), 1);
        result.Should().BeEmpty();
        logged.Should().ContainSingle(x => x.Contains("Failed to match"))
          .Which.Should().Contain("ZZZ-never-matches");
        traced.Should().ContainSingle(x => x.Contains("Failed to match"))
          .Which.Should().Contain("ZZZ-never-matches");
      }
      finally
      {
        Client.Trace = priorTrace;
      }
    }

    [Fact]
    public async Task PreCancelledReadReturnsEmpty()
    {
      using var stream = new ScriptedStream("AB");
      using var client = new Client(stream, new CancellationToken());
      var result = await client.ReadAsync(TimeSpan.FromSeconds(1), new CancellationToken(true));
      result.Should().BeEmpty();
    }

    [Fact]
    public void ClientSendCancelAfterDisposeIsSwallowed()
    {
      // Cancel() on the disposed internal source throws; SendCancel swallows.
      using var stream = new ScriptedStream();
      var probe = new AuditClientRegressionTests.ProbeClient(stream);
      probe.Dispose();
      Action act = () => probe.ExposeSendCancel();
      act.Should().NotThrow();
    }

    [Fact]
    public async Task ConnectAsyncTimeoutThrowsInvalidOperation()
    {
      // 192.0.2.1 (TEST-NET-1) never answers: the timeout must fire first.
      Func<Task> act = () => Client.ConnectAsync("192.0.2.1", 1, default, TimeSpan.FromSeconds(1));
      (await act.Should().ThrowAsync<InvalidOperationException>())
        .WithMessage("*192.0.2.1*");
    }

    [Fact]
    public void TcpByteStreamReadByteMapsObjectDisposedToMinusOne()
    {
      var socket = A.Fake<ISocket>();
      A.CallTo(() => socket.Connected).Returns(true);
      var stream = A.Fake<INetworkStream>();
      A.CallTo(() => socket.GetStream()).Returns(stream);
      A.CallTo(() => stream.ReadByte()).Throws(new ObjectDisposedException("s"));
      using var sut = new TcpByteStream(socket);
      sut.ReadByte().Should().Be(-1);
    }

    [Fact]
    public void TcpByteStreamReadByteMapsInvalidOperationToMinusOne()
    {
      var socket = A.Fake<ISocket>();
      A.CallTo(() => socket.Connected).Returns(true);
      var stream = A.Fake<INetworkStream>();
      A.CallTo(() => socket.GetStream()).Returns(stream);
      A.CallTo(() => stream.ReadByte()).Throws(new InvalidOperationException("x"));
      using var sut = new TcpByteStream(socket);
      sut.ReadByte().Should().Be(-1);
    }

    [Fact]
    public void TcpClientSocketOptionsRoundTrip()
    {
      using var server = new DummyTelnetServer();
      using var sut = new telnet_cs.TcpClient(server.IPAddress.ToString(), server.Port);
      sut.SendTimeout = 1234;
      sut.SendTimeout.Should().Be(1234);
      sut.NoDelay = true;
      sut.NoDelay.Should().BeTrue();
      sut.KeepAlive = true;
      sut.KeepAlive.Should().BeTrue();
    }

    [Fact]
    public async Task NetworkStreamWriteByteAsyncRelays()
    {
      using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
      listener.Start();
      try
      {
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        using var client = new System.Net.Sockets.TcpClient();
        await client.ConnectAsync("127.0.0.1", port);
        using var serverSide = await listener.AcceptTcpClientAsync();
        using var sut = new telnet_cs.NetworkStream(serverSide.GetStream());
        await sut.WriteByteAsync(0x41, CancellationToken.None);
        var buf = new byte[1];
        var read = await client.GetStream().ReadAsync(buf, 0, 1);
        read.Should().Be(1);
        buf[0].Should().Be(0x41);
      }
      finally
      {
        listener.Stop();
      }
    }

    [Fact]
    public void ToStringWithEncodingDecodes()
    {
      ByteStringConverter.ToString(new byte[] { 0xC3, 0xA9 }, Encoding.UTF8).Should().Be("é");
    }

    [Fact]
    public void GuardPropertyPassesForNonNull()
    {
      Action act = () => Guard.AgainstNullArgumentProperty("p", "Prop", "v");
      act.Should().NotThrow();
    }
  }

  public class AuditCoverageTopUp3Tests
  {
    [Fact]
    public async Task NawsOutOfRangeDimensionsClampTo80x24()
    {
      // Width/height outside 1..65535 fall back to the 80x24 default.
      using var stream = new ScriptedStream(255, 253, 31);
      using var cts = new CancellationTokenSource();
      using var sut = new ByteStreamHandler(stream, cts, 1);
      sut.WindowWidth = 100000;
      sut.WindowHeight = -3;
      (await sut.ReadAsync(TimeSpan.FromMilliseconds(100))).Should().BeEmpty();
      stream.ByteWrites.Should().HaveCount(2);
      stream.ByteWrites[1].Should().Equal(new byte[]
      {
        255, 250, 31, 0, 0, 80, 0, 24, 255, 240,
      });
    }

    [Fact]
    public async Task InvalidOperationDuringSubnegotiationAbortsItSilently()
    {
      // TryReadByte maps InvalidOperationException to -1, like IOException.
      var fake = A.Fake<IByteStream>();
      A.CallTo(() => fake.Connected).Returns(true);
      var first = true;
      A.CallTo(() => fake.Available).ReturnsLazily(() =>
      {
        if (first)
        {
          first = false;
          return 1;
        }

        return 0;
      });
      var reads = new Queue<int>(new[] { 255, 250 });
      A.CallTo(() => fake.ReadByte()).ReturnsLazily(() =>
        reads.Count > 0 ? reads.Dequeue() : throw new InvalidOperationException("boom"));
      using var cts = new CancellationTokenSource();
      using var sut = new ByteStreamHandler(fake, cts, 1);
      (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().BeEmpty();
      A.CallTo(() => fake.WriteAsync(A<byte[]>.Ignored, A<int>.Ignored, A<int>.Ignored, A<CancellationToken>.Ignored)).MustNotHaveHappened();
    }
  }
}
