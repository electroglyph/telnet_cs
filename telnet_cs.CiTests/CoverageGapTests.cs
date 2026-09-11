// Coverage-gap tests for audit test.md P0-P2 (no real sockets, no hardcoded ports).
// These pin PROPER (post-fix) behavior. Tests covering unfixed source bugs FAIL
// until the source is fixed -- that is intentional (per directive: proper
// behavior tested even while red). Real-socket integration tests live in
// RealSocketIntegrationTests.cs and are expected to be green.
//
// NOTE: test parallelization is disabled assembly-wide because these tests (and the
// pre-existing suite) share mutable static Client state (SkipProactiveOptionNegotiation,
// TerminalType, TerminalSpeed) and timing-sensitive fakes.
[assembly: Xunit.CollectionBehavior(Xunit.CollectionBehavior.CollectionPerAssembly, DisableTestParallelization = true)]

namespace telnet_cs.CiTests
{
  using System;
  using System.Collections.Generic;
  using System.Diagnostics;
  using System.IO;
  using System.Linq;
  using System.Text;
  using System.Text.RegularExpressions;
  using System.Threading;
  using System.Threading.Tasks;
  using FakeItEasy;
  using FluentAssertions;
  using Xunit;
  using System.Collections.ObjectModel;

  public class ByteStringConverterGapTests
  {
    [Fact]
    public void ConvertAsciiStringToBytes()
    {
      ByteStringConverter.ConvertStringToByteArray("ABC")
        .Should().Equal(new byte[] { 65, 66, 67 });
    }

    [Fact]
    public void ConvertEmptyStringToEmptyArray()
    {
      ByteStringConverter.ConvertStringToByteArray(string.Empty)
        .Should().BeEmpty();
    }

    [Fact]
    public void ToStringRoundTripsAscii()
    {
      var bytes = new byte[] { 65, 66, 67 };
      ByteStringConverter.ToString(bytes).Should().Be("ABC");
      ByteStringConverter.ToString(bytes, 1, 2).Should().Be("BC");
    }

    [Fact]
    public void RealIacIsEscapedByDoubling()
    {
      // PROPER (test.md §3.3): a real IAC byte (char)255 in the input must be
      // escaped by doubling it. Currently fails: the code replaces the literal
      // 4-char sequence "\0xFF" instead, and ASCII maps 255 -> '?' (63).
      var iac = ((char)255).ToString();
      var result = ByteStringConverter.ConvertStringToByteArray("a" + iac + "b");
      result.Should().Equal(new byte[] { (byte)'a', 255, 255, (byte)'b' });
    }

    [Fact]
    public void NulPlusXffSequencePassesThroughUnchanged()
    {
      // PROPER: the literal chars NUL + "xFF" are ordinary data, not an IAC
      // escape target. Currently fails: InvariantCulture Replace matches the
      // "xFF" part (NUL is ignorable in culture comparison) and splices in
      // the 8-char replacement (1+1+8+1=11 bytes).
      var needle = "\0xFF"; // 4 chars: \0, x, F, F
      needle.Should().HaveLength(4);
      var result = ByteStringConverter.ConvertStringToByteArray("a" + needle + "b");
      result.Should().Equal(new byte[]
      {
        (byte)'a', 0, (byte)'x', (byte)'F', (byte)'F', (byte)'b',
      });
    }

    [Fact]
    public void BareXffPassesThroughUnchanged()
    {
      // PROPER: a bare "xFF" (no NUL, no IAC byte) is ordinary text.
      // Currently fails on net6+: InvariantCulture comparison ignores the NUL
      // in the search value, so even the bare sequence is replaced.
      ByteStringConverter.ConvertStringToByteArray("axFFb").Should().Equal(
        new byte[] { (byte)'a', (byte)'x', (byte)'F', (byte)'F', (byte)'b' });
    }

    [Fact]
    public void ToStringPreservesFfBytes()
    {
      // PROPER (test.md §3.3): decoding must round-trip 0xFF (e.g. Latin-1)
      // and must not strip anything. Currently fails: ASCII decodes 0xFF to
      // '?' (63), so the .Trim((char)255) is a no-op over already-lost data.
      var s = ByteStringConverter.ToString(new byte[] { 255, 65, 255 });
      s.Should().Be("\u00FFA\u00FF");
    }
  }

  public class GuardGapTests
  {
    [Fact]
    public void AgainstNullArgumentThrowsWithParamName()
    {
      Action act = () => Guard.AgainstNullArgument<string>("myParam", null!);
      act.Should().Throw<ArgumentNullException>().WithParameterName("myParam");
    }

    [Fact]
    public void AgainstNullArgumentPassesForNonNull()
    {
      Action act = () => Guard.AgainstNullArgument("p", "value");
      act.Should().NotThrow();
    }

    [Fact]
    public void AgainstNullArgumentIfNullableThrowsForNullReference()
    {
      Action act = () => Guard.AgainstNullArgumentIfNullable<string>("p", null!);
      act.Should().Throw<ArgumentNullException>().WithParameterName("p");
    }

    [Fact]
    public void AgainstNullArgumentIfNullableIgnoresNonNullableValueType()
    {
      Action act = () => Guard.AgainstNullArgumentIfNullable<int>("p", 5);
      act.Should().NotThrow();
    }

    [Fact]
    public void AgainstNullArgumentIfNullableThrowsForNullNullableValueType()
    {
      Action act = () => Guard.AgainstNullArgumentIfNullable<int?>("p", null);
      act.Should().Throw<ArgumentNullException>().WithParameterName("p");
    }

    [Fact]
    public void AgainstNullArgumentPropertyThrowsWithPropertyInMessage()
    {
      Action act = () => Guard.AgainstNullArgumentProperty<string>("param", "Prop", null!);
      var ex = act.Should().Throw<ArgumentException>().Which;
      ex.ParamName.Should().Be("param");
      ex.Message.Should().Contain("Prop");
    }

    [Fact]
    public void AgainstNullArgumentPropertyIfNullableThrowsForNullReference()
    {
      Action act = () => Guard.AgainstNullArgumentPropertyIfNullable<string>("param", "Prop", null!);
      act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void AgainstNullArgumentPropertyIfNullableIgnoresValueType()
    {
      Action act = () => Guard.AgainstNullArgumentPropertyIfNullable<int>("param", "Prop", 7);
      act.Should().NotThrow();
    }
  }

  public class ByteStreamHandlerProtocolTests
  {
    private static ByteStreamHandler MakeHandler(IByteStream stream, out CancellationTokenSource cts, int readDelayMs = 1)
    {
      cts = new CancellationTokenSource();
      return new ByteStreamHandler(stream, cts, readDelayMs);
    }

    private static IByteStream FakeStreamOnce(int[] reads)
    {
      var fake = A.Fake<IByteStream>();
      var first = true;
      A.CallTo(() => fake.Connected).Returns(true);
      A.CallTo(() => fake.Available).ReturnsLazily(() =>
      {
        if (first) { first = false; return 1; }
        return 0;
      });
      A.CallTo(() => fake.ReadByte()).ReturnsNextFromSequence(reads);
      return fake;
    }

    private static async Task<string> ReadOnceAsync(int[] reads)
    {
      var fake = FakeStreamOnce(reads);
      using var cts = new CancellationTokenSource();
      using var sut = new ByteStreamHandler(fake, cts, 1);
      return await sut.ReadAsync(TimeSpan.FromMilliseconds(50));
    }

    private static async Task<string> ReadScriptedAsync(params int[] reads)
    {
      using var stream = new ScriptedStream(reads);
      using var cts = new CancellationTokenSource();
      using var sut = new ByteStreamHandler(stream, cts, 1);
      return await sut.ReadAsync(TimeSpan.FromMilliseconds(50));
    }

    [Theory]
    [InlineData(1, "\n \n")]   // SOH
    [InlineData(2, "\t")]     // STX
    [InlineData(3, "^C")]      // ETX
    [InlineData(4, "^D")]      // EOT
    [InlineData(6, "")]        // ACK ignored
    [InlineData(8, "")]        // BS swallowed
    [InlineData(11, null)]     // VT -> NewLine (checked separately)
    [InlineData(12, null)]     // FF -> NewLine
    [InlineData(31, ",")]      // US
    [InlineData(65, "A")]      // default passthrough
    [InlineData(66, "B")]
    public async Task ControlCharsMapAsDocumented(int input, string? expected)
    {
      var want = expected ?? Environment.NewLine;
      (await ReadOnceAsync(new[] { input })).Should().Be(want);
    }

    [Fact]
    public async Task NakAppendsRetransmitMessage()
    {
      (await ReadOnceAsync(new[] { 21 }))
        .Should().Be("NAK: Retransmit last message.");
    }

    [Fact]
    public async Task BellIsSwallowedWithoutThrow()
    {
      // Console.Beep() is a no-op on this Linux env; on headless/unsupported
      // platforms it may throw (test.md §3.7). Characterizes current behavior.
      var act = async () => await ReadOnceAsync(new[] { 7 });
      (await act()).Should().BeEmpty();
    }

    [Fact]
    public async Task StreamEndReturnsEmpty()
    {
      (await ReadOnceAsync(new[] { -1 })).Should().BeEmpty();
    }

    [Fact]
    public async Task EscapedIacYieldsSingle255Char()
    {
      // P0.1: IAC IAC decodes to one (char)255, not the decimal string "255".
      (await ReadOnceAsync(new[] { 255, 255 })).Should().Be("\u00ff");
    }

    [Fact]
    public async Task EscapedIacEmbeddedInTextDecodesInline()
    {
      var result = await ReadScriptedAsync(65, 255, 255, 66);
      result.Should().Be("A\u00ffB");
    }

    [Fact]
    public async Task ConsecutiveEscapedIacsYieldOneCharEach()
    {
      var result = await ReadScriptedAsync(255, 255, 255, 255);
      result.Should().Be("\u00ff\u00ff");
    }

    [Fact]
    public async Task EscapedIacRecordsSingleRawByteUnderLatin1()
    {
      // Exercises the rawBytes path: the buggy code recorded ASCII("255")
      // (3 bytes -> "255"); the fix records a single 0xFF (-> "ÿ").
      var fake = FakeStreamOnce(new[] { 255, 255 });
      using var cts = new CancellationTokenSource();
      using var sut = new ByteStreamHandler(fake, cts, 1) { TextEncoding = Encoding.Latin1 };
      var result = await sut.ReadAsync(TimeSpan.FromMilliseconds(50));
      result.Should().Be("\u00ff");
    }

    [Fact]
    public async Task EscapedIacFollowedByCommandDecodesCharThenConsumesCommand()
    {
      // IAC IAC -> ÿ, then IAC NOP is consumed silently.
      var result = await ReadScriptedAsync(255, 255, 255, 241);
      result.Should().Be("\u00ff");
    }

    [Fact]
    public async Task TruncatedIacYieldsEmpty()
    {
      (await ReadOnceAsync(new[] { 255, -1 })).Should().BeEmpty();
    }

    [Fact]
    public async Task EnquirySendsAck()
    {
      var fake = FakeStreamOnce(new[] { 5 });
      using var cts = new CancellationTokenSource();
      using var sut = new ByteStreamHandler(fake, cts, 1);
      (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().BeEmpty();
      A.CallTo(() => fake.WriteByteAsync((byte)6, A<CancellationToken>.Ignored)).MustHaveHappened();
    }

    [Theory]
    // verb, option, expected reply verb
    [InlineData(253, 3, 251)]   // DO SGA -> WILL SGA
    [InlineData(251, 3, 253)]   // WILL SGA -> DO SGA
    [InlineData(253, 24, 251)]  // DO TT -> WILL
    [InlineData(251, 24, 253)]  // WILL TT -> DO
    [InlineData(253, 32, 251)]  // DO TS -> WILL
    [InlineData(251, 32, 253)]  // WILL TS -> DO
    [InlineData(253, 1, 252)]   // DO Echo without opt-in -> WONT (see EchoTests)
    [InlineData(251, 1, 253)]   // WILL Echo -> DO; local echo suppressed instead (see EchoTests)
    [InlineData(253, 99, 252)]  // DO unknown -> WONT
    [InlineData(251, 99, 254)]  // WILL unknown -> DONT
    public async Task ReplyToCommandTable(int verb, int option, int expectedReply)
    {
      var fake = FakeStreamOnce(new[] { 255, verb, option });
      using var cts = new CancellationTokenSource();
      using var sut = new ByteStreamHandler(fake, cts, 1);
      (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().BeEmpty();
      A.CallTo(() => fake.WriteAsync(A<byte[]>.Ignored, 0, 3, A<CancellationToken>.Ignored))
        .WhenArgumentsMatch(o => o[0] is byte[] b && b[0] == 255 && b[1] == (byte)expectedReply && b[2] == (byte)option)
        .MustHaveHappened();
    }

    [Fact]
    public async Task DoWindowSizeRepliesWillPlusNawsFollowUp()
    {
      var fake = FakeStreamOnce(new[] { 255, 253, 31 });
      using var cts = new CancellationTokenSource();
      using var sut = new ByteStreamHandler(fake, cts, 1);
      sut.WindowWidth = 80;
      sut.WindowHeight = 24;
      (await sut.ReadAsync(TimeSpan.FromMilliseconds(100))).Should().BeEmpty();
      // First reply: IAC WILL WS
      A.CallTo(() => fake.WriteAsync(A<byte[]>.Ignored, 0, 3, A<CancellationToken>.Ignored))
        .WhenArgumentsMatch(o => o[0] is byte[] b && b[0] == 255 && b[1] == 251 && b[2] == 31)
        .MustHaveHappened();
      // NAWS follow-up (RFC 1073): IAC SB WS <width-hi> <width-lo> <height-hi> <height-lo> IAC SE
      var expectedNaws = new byte[] { 255, 250, 31, 0, 0, 80, 0, 24, 255, 240 };
      A.CallTo(() => fake.WriteAsync(A<byte[]>.Ignored, 0, expectedNaws.Length, A<CancellationToken>.Ignored))
        .WhenArgumentsMatch(o => o[0] is byte[] b && b.SequenceEqual(expectedNaws))
        .MustHaveHappenedOnceExactly();
    }

    [Theory]
    [InlineData(254, 1)]  // DONT Echo ignored
    [InlineData(254, 3)]  // DONT SGA ignored
    [InlineData(252, 3)]  // WONT SGA ignored
    [InlineData(252, 1)]  // WONT Echo ignored
    public async Task DontWontAreIgnoredWithoutReply(int verb, int option)
    {
      var fake = FakeStreamOnce(new[] { 255, verb, option });
      using var cts = new CancellationTokenSource();
      using var sut = new ByteStreamHandler(fake, cts, 1);
      (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().BeEmpty();
      A.CallTo(() => fake.WriteAsync(A<byte[]>.Ignored, A<int>.Ignored, A<int>.Ignored, A<CancellationToken>.Ignored)).MustNotHaveHappened();
      A.CallTo(() => fake.WriteByteAsync(A<byte>.Ignored, A<CancellationToken>.Ignored)).MustNotHaveHappened();
    }

    [Fact]
    public async Task UnknownVerbIsIgnoredWithoutReply()
    {
      var fake = FakeStreamOnce(new[] { 255, 241 }); // IAC NOP
      using var cts = new CancellationTokenSource();
      using var sut = new ByteStreamHandler(fake, cts, 1);
      (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().BeEmpty();
      A.CallTo(() => fake.WriteAsync(A<byte[]>.Ignored, A<int>.Ignored, A<int>.Ignored, A<CancellationToken>.Ignored)).MustNotHaveHappened();
    }

    [Fact]
    public async Task TruncatedOptionAfterDoIsIgnoredWithoutReply()
    {
      var fake = FakeStreamOnce(new[] { 255, 253, -1 });
      using var cts = new CancellationTokenSource();
      using var sut = new ByteStreamHandler(fake, cts, 1);
      (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().BeEmpty();
      A.CallTo(() => fake.WriteAsync(A<byte[]>.Ignored, A<int>.Ignored, A<int>.Ignored, A<CancellationToken>.Ignored)).MustNotHaveHappened();
    }

    [Fact]
    public async Task InterruptProcessCancelsPendingRead()
    {
      var fake = FakeStreamOnce(new[] { 255, 244 }); // IAC IP -> SendCancel()
      using var cts = new CancellationTokenSource();
      using var sut = new ByteStreamHandler(fake, cts, 1);
      var sw = Stopwatch.StartNew();
      var result = await sut.ReadAsync(TimeSpan.FromMilliseconds(2000));
      sw.Stop();
      result.Should().BeEmpty();
      sw.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(1000));
    }

    [Fact]
    public async Task SubnegotiationTerminalTypeSendRepliesWithVt100()
    {
      var priorType = Client.TerminalType;
      try
      {
        Client.TerminalType = "vt100";
        var fake = FakeStreamOnce(new[] { 255, 250, 24, 1, 255, 240 });
        using var cts = new CancellationTokenSource();
        using var sut = new ByteStreamHandler(fake, cts, 1);
        (await sut.ReadAsync(TimeSpan.FromMilliseconds(100))).Should().BeEmpty();
        var expected = new byte[] { 255, 250, 24, 0 }
          .Concat(Encoding.ASCII.GetBytes("vt100"))
          .Concat(new byte[] { 255, 240 }).ToArray();
        A.CallTo(() => fake.WriteAsync(A<byte[]>.Ignored, 0, expected.Length, A<CancellationToken>.Ignored))
          .WhenArgumentsMatch(o => o[0] is byte[] b && b.SequenceEqual(expected))
          .MustHaveHappened();
      }
      finally { Client.TerminalType = priorType; }
    }

    [Fact]
    public async Task SubnegotiationTerminalSpeedSendRepliesWithSpeed()
    {
      var prior = Client.TerminalSpeed;
      try
      {
        Client.TerminalSpeed = "19200,19200";
        var fake = FakeStreamOnce(new[] { 255, 250, 32, 1, 255, 240 });
        using var cts = new CancellationTokenSource();
        using var sut = new ByteStreamHandler(fake, cts, 1);
        (await sut.ReadAsync(TimeSpan.FromMilliseconds(100))).Should().BeEmpty();
        var expected = new byte[] { 255, 250, 32, 0 }
          .Concat(Encoding.ASCII.GetBytes("19200,19200"))
          .Concat(new byte[] { 255, 240 }).ToArray();
        A.CallTo(() => fake.WriteAsync(A<byte[]>.Ignored, 0, expected.Length, A<CancellationToken>.Ignored))
          .WhenArgumentsMatch(o => o[0] is byte[] b && b.SequenceEqual(expected))
          .MustHaveHappened();
      }
      finally { Client.TerminalSpeed = prior; }
    }

    [Fact]
    public async Task SubnegotiationUnknownOptionSendsNoReply()
    {
      var fake = FakeStreamOnce(new[] { 255, 250, 99, 1, 255, 240 });
      using var cts = new CancellationTokenSource();
      using var sut = new ByteStreamHandler(fake, cts, 1);
      (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().BeEmpty();
      A.CallTo(() => fake.WriteAsync(A<byte[]>.Ignored, A<int>.Ignored, A<int>.Ignored, A<CancellationToken>.Ignored)).MustNotHaveHappened();
    }

    [Fact]
    public async Task SubnegotiationMalformedSendsWont()
    {
      // SEND missing (0 instead of 1) -> fallback IAC WONT opt
      var fake = FakeStreamOnce(new[] { 255, 250, 24, 0, 255, 240 });
      using var cts = new CancellationTokenSource();
      using var sut = new ByteStreamHandler(fake, cts, 1);
      (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().BeEmpty();
      A.CallTo(() => fake.WriteAsync(A<byte[]>.Ignored, 0, 3, A<CancellationToken>.Ignored))
        .WhenArgumentsMatch(o => o[0] is byte[] b && b[0] == 255 && b[1] == 252 && b[2] == 24)
        .MustHaveHappened();
    }

    [Fact]
    public async Task HandlerDisposeDoesNotDisposeExternalStream()
    {
      // PROPER (test.md §3.5): the handler must not dispose a stream it does
      // not own. Currently fails: Dispose() disposes the stream, which forces
      // Client to leak the handler (CA2000) as a workaround.
      var fake = A.Fake<IByteStream>();
      A.CallTo(() => fake.Connected).Returns(true);
      using var cts = new CancellationTokenSource();
      var sut = new ByteStreamHandler(fake, cts, 1);
      sut.Dispose();
      A.CallTo(() => fake.Dispose()).MustNotHaveHappened();
      await Task.CompletedTask;
    }

    [Fact]
    public async Task ReadSetsReceiveTimeout()
    {
      var fake = FakeStreamOnce(new[] { 65 });
      using var cts = new CancellationTokenSource();
      using var sut = new ByteStreamHandler(fake, cts, 1);
      await sut.ReadAsync(TimeSpan.FromMilliseconds(50));
      A.CallToSet(() => fake.ReceiveTimeout).To(50).MustHaveHappened();
    }
  }

  public class ClientEdgeTests
  {
    private static IByteStream ConnectedFake()
    {
      var fake = A.Fake<IByteStream>();
      A.CallTo(() => fake.Connected).Returns(true);
      return fake;
    }

    [Fact]
    public void CtorNullStreamThrowsArgumentNull()
    {
      Action act = () => new Client((IByteStream)null!, new CancellationToken());
      act.Should().Throw<ArgumentNullException>().WithParameterName("byteStream");
    }

    [Fact]
    public void CtorNullOptionsThrowsArgumentNull()
    {
      // PROPER (test.md §3.6): options must be null-checked. Currently fails:
      // the foreach over options throws NullReferenceException.
      var fake = ConnectedFake();
      Action act = () => new Client(fake, TimeSpan.FromMilliseconds(10), default, null!);
      act.Should().Throw<ArgumentNullException>().WithParameterName("options");
    }

    [Fact]
    public void CtorStreamFailureThrowsIOExceptionDirectly()
    {
      // PROPER: a negotiation write failure must surface as its own exception,
      // not wrapped in AggregateException. Currently fails: the ctor's
      // Task.Run(...).Wait() wraps the IOException in an AggregateException.
      // NOTE: FluentAssertions' Throw<T> unwraps single-inner AggregateExceptions,
      // so the raw exception must be captured to pin this (see test.md §8).
      var fake = ConnectedFake();
      A.CallTo(() => fake.WriteAsync(A<byte[]>.Ignored, A<int>.Ignored, A<int>.Ignored, A<CancellationToken>.Ignored))
        .Throws(new IOException("boom"));
      var ex = Record.Exception(() => new Client(fake, TimeSpan.FromMilliseconds(10), default));
      ex.Should().BeOfType<IOException>().Which.Message.Should().Be("boom");
    }

    [Fact]
    public void DisposeCompletesWithoutArtificialDelay()
    {
      // PROPER: Dispose must not sleep ~100ms on an AutoResetEvent.
      // Currently fails: BaseClientCancellable.Dispose always waits 100ms.
      var fake = ConnectedFake();
      var sut = new Client(fake, TimeSpan.FromMilliseconds(10), default);
      var sw = Stopwatch.StartNew();
      sut.Dispose();
      sw.Stop();
      sw.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(50));
    }

    [Fact]
    public void CtorUnconnectedThrowsQuickly()
    {
      var fake = A.Fake<IByteStream>();
      A.CallTo(() => fake.Connected).Returns(false);
      var sw = Stopwatch.StartNew();
      Action act = () => new Client(fake, TimeSpan.FromMilliseconds(20), default);
      act.Should().Throw<InvalidOperationException>().WithMessage("Unable to connect to the host.");
      sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task CtorDefaultSendsSuppressGoAhead()
    {
      var prior = Client.SkipProactiveOptionNegotiation;
      try
      {
        Client.SkipProactiveOptionNegotiation = false;
        var fake = ConnectedFake();
        using var _ = new Client(fake, TimeSpan.FromMilliseconds(10), default);
        A.CallTo(() => fake.WriteAsync(A<byte[]>.Ignored, 0, 3, A<CancellationToken>.Ignored))
          .WhenArgumentsMatch(o => o[0] is byte[] b && b.SequenceEqual(Client.SuppressGoAheadBuffer))
          .MustHaveHappened();
      }
      finally { Client.SkipProactiveOptionNegotiation = prior; }
      await Task.CompletedTask;
    }

    [Fact]
    public async Task CtorSkipNegotiationSendsNothing()
    {
      var prior = Client.SkipProactiveOptionNegotiation;
      try
      {
        Client.SkipProactiveOptionNegotiation = true;
        var fake = ConnectedFake();
        using var _ = new Client(fake, TimeSpan.FromMilliseconds(10), default);
        A.CallTo(() => fake.WriteAsync(A<byte[]>.Ignored, A<int>.Ignored, A<int>.Ignored, A<CancellationToken>.Ignored)).MustNotHaveHappened();
      }
      finally { Client.SkipProactiveOptionNegotiation = prior; }
      await Task.CompletedTask;
    }

    [Fact]
    public async Task CtorCustomOptionsSendExactTriplesInOrder()
    {
      var prior = Client.SkipProactiveOptionNegotiation;
      try
      {
        Client.SkipProactiveOptionNegotiation = true;
        var fake = ConnectedFake();
        var writes = new List<byte[]>();
        A.CallTo(() => fake.WriteAsync(A<byte[]>.Ignored, A<int>.Ignored, A<int>.Ignored, A<CancellationToken>.Ignored))
          .Invokes(call => writes.Add(((byte[])call.Arguments[0]!).ToArray()));
        using var _ = new Client(fake, TimeSpan.FromMilliseconds(10), default,
          new[] { (Commands.Do, Options.Echo), (Commands.Will, Options.WindowSize) });
        writes.Should().HaveCount(2);
        writes[0].Should().Equal(new byte[] { 255, 253, 1 });
        writes[1].Should().Equal(new byte[] { 255, 251, 31 });
      }
      finally { Client.SkipProactiveOptionNegotiation = prior; }
      await Task.CompletedTask;
    }

    [Fact]
    public async Task WriteLineAppendsLegacyFeed()
    {
      var fake = ConnectedFake();
      using var sut = new Client(fake, TimeSpan.FromMilliseconds(10), default);
      A.CallTo(() => fake.WriteAsync(A<byte[]>.Ignored, A<int>.Ignored, A<int>.Ignored, A<CancellationToken>.Ignored));
      await sut.WriteLineAsync("cmd");
      A.CallTo(() => fake.WriteAsync("cmd\n", A<CancellationToken>.Ignored)).MustHaveHappened();
    }

    [Fact]
    public async Task WriteLineRfc854AppendsCrLf()
    {
      var fake = ConnectedFake();
      using var sut = new Client(fake, TimeSpan.FromMilliseconds(10), default);
      await sut.WriteLineRfc854Async("cmd");
      A.CallTo(() => fake.WriteAsync("cmd\r\n", A<CancellationToken>.Ignored)).MustHaveHappened();
    }

    [Fact]
    public async Task WriteByteArrayRelaysOffsetCount()
    {
      var fake = ConnectedFake();
      using var sut = new Client(fake, TimeSpan.FromMilliseconds(10), default);
      var data = new byte[] { 1, 2, 3 };
      await sut.WriteAsync(data);
      A.CallTo(() => fake.WriteAsync(data, 0, 3, A<CancellationToken>.Ignored)).MustHaveHappened();
    }

    [Fact]
    public async Task WriteNullByteArrayThrowsArgumentNull()
    {
      // PROPER (test.md §3.4): null data must throw ArgumentNullException.
      // Currently fails: data.Length throws NullReferenceException.
      var fake = ConnectedFake();
      using var sut = new Client(fake, TimeSpan.FromMilliseconds(10), default);
      Func<Task> act = () => sut.WriteAsync((byte[])null!);
      await act.Should().ThrowAsync<ArgumentNullException>().WithParameterName("data");
    }

    [Fact]
    public async Task WriteWhenDisconnectedIsNoop()
    {
      var connected = true;
      var fake = A.Fake<IByteStream>();
      A.CallTo(() => fake.Connected).ReturnsLazily(() => connected);
      using var sut = new Client(fake, TimeSpan.FromMilliseconds(10), default);
      connected = false;
      await sut.WriteAsync("hi");
      await sut.WriteAsync(new byte[] { 1 });
      // Proactive SGA happens in ctor while connected; post-disconnect writes must be no-ops.
      A.CallTo(() => fake.WriteAsync(A<string>.Ignored, A<CancellationToken>.Ignored)).MustNotHaveHappened();
      A.CallTo(() => fake.WriteAsync(A<byte[]>.Ignored, A<int>.Ignored, A<int>.Ignored, A<CancellationToken>.Ignored)).MustHaveHappenedOnceExactly();
      // ctor SGA did happen exactly once before disconnect
      A.CallTo(() => fake.WriteAsync(A<byte[]>.Ignored, 0, 3, A<CancellationToken>.Ignored)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task WriteAfterExternalCancelIsNoop()
    {
      var fake = ConnectedFake();
      using var cts = new CancellationTokenSource();
      using var sut = new Client(fake, TimeSpan.FromMilliseconds(10), cts.Token);
      cts.Cancel();
      await Task.Delay(20); // let Register(SendCancel) propagate
      Fake.ClearRecordedCalls(fake);
      await sut.WriteAsync("hi");
      A.CallTo(() => fake.WriteAsync(A<string>.Ignored, A<CancellationToken>.Ignored)).MustNotHaveHappened();
    }

    [Fact]
    public async Task WritePropagatesStreamException()
    {
      // Propagation holds before and after the semaphore fix; the no-hang
      // half of test.md §3.4 is pinned by SecondWriteAfterFailureCompletesPromptly.
      var fake = ConnectedFake();
      A.CallTo(() => fake.WriteAsync(A<string>.Ignored, A<CancellationToken>.Ignored))
        .ThrowsAsync(new IOException("boom"));
      using var sut = new Client(fake, TimeSpan.FromMilliseconds(10), default);
      Func<Task> act = () => sut.WriteAsync("hi");
      await act.Should().ThrowAsync<IOException>().WithMessage("boom");
    }

    [Fact]
    public async Task SecondWriteAfterFailureCompletesPromptly()
    {
      // PROPER (test.md §3.4): WriteAsync must release the send semaphore in a
      // finally, so a failed write cannot hang the next one. Currently fails:
      // the semaphore stays taken and the second write never completes (the
      // Task.WhenAny guard keeps this red-instead-of-hung).
      var fake = ConnectedFake();
      A.CallTo(() => fake.WriteAsync(A<string>.Ignored, A<CancellationToken>.Ignored))
        .ThrowsAsync(new IOException("boom"));
      using var sut = new Client(fake, TimeSpan.FromMilliseconds(10), default);
      Func<Task> first = () => sut.WriteAsync("hi");
      await first.Should().ThrowAsync<IOException>();
      var second = sut.WriteAsync("again");
      var winner = await Task.WhenAny(second, Task.Delay(TimeSpan.FromSeconds(2)));
      winner.Should().BeSameAs(second, "second write hung: the send semaphore was leaked by the failed write");
      try
      {
        await second;
      }
      catch (IOException)
      {
      }
    }

    [Fact]
    public async Task TerminatedReadDefaultTimeoutOverloadReadsAccountPrompt()
    {
      using var stream = new DummyByteStream();
      using var sut = new Client(stream, new CancellationToken());
      (await sut.TerminatedReadAsync(":")).Should().EndWith(":");
    }

    [Fact]
    public async Task ReadDefaultOverloadReadsAccountPrompt()
    {
      using var stream = new DummyByteStream();
      using var sut = new Client(stream, new CancellationToken());
      (await sut.ReadAsync()).Should().Contain("Account:");
    }

    [Fact]
    public async Task TerminatedReadEmptyTerminatorReturnsAfterFirstRead()
    {
      using var stream = new DummyByteStream();
      using var sut = new Client(stream, new CancellationToken());
      var s = await sut.TerminatedReadAsync(string.Empty, TimeSpan.FromMilliseconds(500), 1);
      s.Should().NotBeNull();
    }

    [Fact]
    public async Task TerminatedReadNullRegexThrowsArgumentNull()
    {
      // PROPER: a null regex is a caller contract violation. Currently fails:
      // IsRegexLocated(null, s) returns false so the read loop runs, then
      // Client.cs:107 calls regex.ToString() for the debug message ->
      // NullReferenceException.
      var fake = A.Fake<IByteStream>();
      A.CallTo(() => fake.Connected).Returns(true);
      using var sut = new Client(fake, TimeSpan.FromMilliseconds(1), default) { MillisecondReadDelay = 1 };
      Func<Task> act = () => sut.TerminatedReadAsync((Regex)null!, TimeSpan.FromMilliseconds(60), 1);
      await act.Should().ThrowAsync<ArgumentNullException>().WithParameterName("regex");
    }

    [Fact]
    public async Task TryLoginFailsFastWhenNoTerminator()
    {
      var fake = A.Fake<IByteStream>();
      A.CallTo(() => fake.Connected).Returns(true);
      using var sut = new Client(fake, TimeSpan.FromMilliseconds(1), default) { MillisecondReadDelay = 1 };
      (await sut.TryLoginAsync("u", "p", 60)).Should().BeFalse();
    }

    [Fact]
    public async Task ReadDoesNotCloseStream()
    {
      // Holds both before and after the handler-ownership fix (§3.5): today
      // Client.ReadAsync intentionally leaks the ByteStreamHandler (CA2000) so
      // the shared stream stays open; after the fix the handler is disposed
      // but must not dispose the stream it does not own.
      var fake = A.Fake<IByteStream>();
      A.CallTo(() => fake.Connected).Returns(true);
      using var sut = new Client(fake, TimeSpan.FromMilliseconds(1), default) { MillisecondReadDelay = 1 };
      await sut.ReadAsync(TimeSpan.FromMilliseconds(20));
      A.CallTo(() => fake.Dispose()).MustNotHaveHappened();
      A.CallTo(() => fake.Close()).MustNotHaveHappened();
    }

    [Fact]
    public void DisposeClosesStreamAndDoubleDisposeIsSafe()
    {
      var fake = ConnectedFake();
      using var sut = new Client(fake, TimeSpan.FromMilliseconds(10), default);
      Action act = () => { sut.Dispose(); sut.Dispose(); };
      act.Should().NotThrow();
      A.CallTo(() => fake.Close()).MustHaveHappened();
    }

    [Fact]
    public void IsConnectedReflectsStream()
    {
      var fake = A.Fake<IByteStream>();
      A.CallTo(() => fake.Connected).Returns(true);
      using var sut = new Client(fake, TimeSpan.FromMilliseconds(10), default);
      sut.IsConnected.Should().BeTrue();
      A.CallTo(() => fake.Connected).Returns(false);
      sut.IsConnected.Should().BeFalse();
    }
  }

  public class TcpByteStreamPassthroughTests
  {
    private static (TcpByteStream sut, ISocket socket, INetworkStream stream) Make()
    {
      var socket = A.Fake<ISocket>();
      var stream = A.Fake<INetworkStream>();
      A.CallTo(() => socket.GetStream()).Returns(stream);
      A.CallTo(() => socket.Connected).Returns(true);
      return (new TcpByteStream(socket), socket, stream);
    }

    [Fact]
    public void AvailableAndConnectedPassthrough()
    {
      var (sut, socket, _) = Make();
      using (sut)
      {
        A.CallTo(() => socket.Available).Returns(7);
        A.CallTo(() => socket.Connected).Returns(true);
        sut.Available.Should().Be(7);
        sut.Connected.Should().BeTrue();
      }
    }

    [Fact]
    public void ReceiveTimeoutGetSetPassthrough()
    {
      var (sut, socket, _) = Make();
      using (sut)
      {
        A.CallTo(() => socket.ReceiveTimeout).Returns(123);
        sut.ReceiveTimeout.Should().Be(123);
        sut.ReceiveTimeout = 456;
        A.CallToSet(() => socket.ReceiveTimeout).To(456).MustHaveHappened();
      }
    }

    [Fact]
    public void ReadByteRelaysToStream()
    {
      var (sut, _, stream) = Make();
      using (sut)
      {
        A.CallTo(() => stream.ReadByte()).Returns(65);
        sut.ReadByte().Should().Be(65);
      }
    }

    [Fact]
    public async Task WriteBufferRelaysToStream()
    {
      var (sut, _, stream) = Make();
      using (sut)
      {
        var buf = new byte[] { 1, 2, 3 };
        var ct = new CancellationToken();
        await sut.WriteAsync(buf, 0, 3, ct);
        A.CallTo(() => stream.WriteAsync(buf, 0, 3, ct)).MustHaveHappened();
      }
    }

    [Fact]
    public async Task WriteByteRelaysToStream()
    {
      var (sut, _, stream) = Make();
      using (sut)
      {
        var ct = new CancellationToken();
        await sut.WriteByteAsync(9, ct);
        A.CallTo(() => stream.WriteByteAsync(9, ct)).MustHaveHappened();
      }
    }

    [Fact]
    public void CloseRelaysAndDisposeDoesNotDisposeUnownedSocket()
    {
      var (sut, socket, _) = Make();
      sut.Close();
      A.CallTo(() => socket.Close()).MustHaveHappened();
      sut.Dispose();
      A.CallTo(() => socket.Close()).MustHaveHappenedTwiceOrMore();
      A.CallTo(() => socket.Dispose()).MustNotHaveHappened();
    }
  }

  public class NegotiationStateTests
  {
    [Fact]
    public void ReceivedWill_Disagree_RepliesDontAndStaysDisabled()
    {
      var state = new NegotiationState();
      state.ReceivedWill(1, agree: false).Should().Be(Commands.Dont);
      state.IsEnabledByPeer(1).Should().BeFalse();
    }

    [Fact]
    public void ReceivedWill_Agree_RepliesDoAndEnables()
    {
      var state = new NegotiationState();
      state.ReceivedWill(3, agree: true).Should().Be(Commands.Do);
      state.IsEnabledByPeer(3).Should().BeTrue();
    }

    [Fact]
    public void ReceivedWill_WhenEnabled_Silent()
    {
      var state = new NegotiationState();
      state.ReceivedWill(3, agree: true).Should().Be(Commands.Do);
      state.ReceivedWill(3, agree: true).Should().BeNull();
      state.IsEnabledByPeer(3).Should().BeTrue();
    }

    [Fact]
    public void ReceivedWont_WhenNo_Silent()
    {
      var state = new NegotiationState();
      state.ReceivedWont(3).Should().BeNull();
      state.IsEnabledByPeer(3).Should().BeFalse();
    }

    [Fact]
    public void ReceivedWont_WhenEnabled_RepliesDontAndDisables()
    {
      var state = new NegotiationState();
      state.ReceivedWill(3, agree: true).Should().Be(Commands.Do);
      state.ReceivedWont(3).Should().Be(Commands.Dont);
      state.IsEnabledByPeer(3).Should().BeFalse();
    }

    [Fact]
    public void ReceivedDo_Agree_RepliesWillAndEnablesUs()
    {
      var state = new NegotiationState();
      state.ReceivedDo(3, agree: true).Should().Be(Commands.Will);
      state.IsEnabledByUs(3).Should().BeTrue();
    }

    [Fact]
    public void ReceivedDo_Disagree_RepliesWont()
    {
      var state = new NegotiationState();
      state.ReceivedDo(1, agree: false).Should().Be(Commands.Wont);
      state.IsEnabledByUs(1).Should().BeFalse();
    }

    [Fact]
    public void ReceivedDo_WhenUsEnabled_Silent()
    {
      var state = new NegotiationState();
      state.ReceivedDo(3, agree: true).Should().Be(Commands.Will);
      state.ReceivedDo(3, agree: true).Should().BeNull();
    }

    [Fact]
    public void ReceivedDont_WhenUsEnabled_RepliesWontAndDisables()
    {
      var state = new NegotiationState();
      state.ReceivedDo(3, agree: true).Should().Be(Commands.Will);
      state.ReceivedDont(3).Should().Be(Commands.Wont);
      state.IsEnabledByUs(3).Should().BeFalse();
    }

    [Fact]
    public void ReceivedDont_WhenUsNo_Silent()
    {
      var state = new NegotiationState();
      state.ReceivedDont(3).Should().BeNull();
    }

    [Fact]
    public void RequestEnable_Fresh_SendsDoThenSuppressesWhileOutstanding()
    {
      var state = new NegotiationState();
      state.RequestEnable(3).Should().Be(Commands.Do);
      state.RequestEnable(3).Should().BeNull();
      state.ReceivedWill(3, agree: true).Should().BeNull();
      state.IsEnabledByPeer(3).Should().BeTrue();
    }

    [Fact]
    public void Refusal_RememberedUntilExplicitStimulus()
    {
      var state = new NegotiationState();
      state.RequestEnable(1).Should().Be(Commands.Do);
      state.ReceivedWont(1).Should().BeNull();
      state.WasRefusedByPeer(1).Should().BeTrue();
      state.IsEnabledByPeer(1).Should().BeFalse();
      // Explicit re-request is new stimulus: goes out, clears the flag.
      state.RequestEnable(1).Should().Be(Commands.Do);
      state.WasRefusedByPeer(1).Should().BeFalse();
    }

    [Fact]
    public void Refusal_ClearedByPeerChangingMind()
    {
      var state = new NegotiationState();
      state.RequestEnable(1).Should().Be(Commands.Do);
      state.ReceivedWont(1).Should().BeNull();
      state.WasRefusedByPeer(1).Should().BeTrue();
      state.ReceivedWill(1, agree: true).Should().Be(Commands.Do);
      state.WasRefusedByPeer(1).Should().BeFalse();
      state.IsEnabledByPeer(1).Should().BeTrue();
    }

    [Fact]
    public void QueuedDisable_DrainsWhenEnableCompletes()
    {
      var state = new NegotiationState();
      state.RequestEnable(3).Should().Be(Commands.Do);
      state.RequestDisable(3).Should().BeNull();
      state.ReceivedWill(3, agree: true).Should().Be(Commands.Dont);
      state.IsEnabledByPeer(3).Should().BeFalse();
      state.ReceivedWont(3).Should().BeNull();
    }

    [Fact]
    public void QueuedDisable_DroppedWhenRefusedInto()
    {
      var state = new NegotiationState();
      state.RequestEnable(3).Should().Be(Commands.Do);
      state.RequestDisable(3).Should().BeNull();
      state.ReceivedWont(3).Should().BeNull();
      state.IsEnabledByPeer(3).Should().BeFalse();
      state.WasRefusedByPeer(3).Should().BeTrue();
    }

    [Fact]
    public void QueuedEnable_DrainsWhenDisableCompletes()
    {
      var state = new NegotiationState();
      state.ReceivedWill(3, agree: true).Should().Be(Commands.Do);
      state.RequestDisable(3).Should().Be(Commands.Dont);
      state.RequestEnable(3).Should().BeNull();
      // Disable completes (WONT): the queued enable drains as a fresh DO.
      state.ReceivedWont(3).Should().Be(Commands.Do);
      state.IsEnabledByPeer(3).Should().BeFalse();
      state.ReceivedWill(3, agree: true).Should().BeNull();
      state.IsEnabledByPeer(3).Should().BeTrue();
    }

    [Fact]
    public void UsSide_QueueMirrorsHimSide()
    {
      var state = new NegotiationState();
      state.OfferEnable(3).Should().Be(Commands.Will);
      state.OfferDisable(3).Should().BeNull();
      state.ReceivedDo(3, agree: true).Should().Be(Commands.Wont);
      state.IsEnabledByUs(3).Should().BeFalse();
    }

    [Fact]
    public void UsSide_RefusalRemembered()
    {
      var state = new NegotiationState();
      state.OfferEnable(3).Should().Be(Commands.Will);
      state.ReceivedDont(3).Should().BeNull();
      state.WasRefusedByUs(3).Should().BeTrue();
      state.OfferEnable(3).Should().Be(Commands.Will);
      state.WasRefusedByUs(3).Should().BeFalse();
    }

    [Fact]
    public void InvalidOption_ThrowsOutOfRange()
    {
      var state = new NegotiationState();
      foreach (var bad in new[] { -1, 256 })
      {
        Assert.Throws<ArgumentOutOfRangeException>(() => state.ReceivedWill(bad, true));
        Assert.Throws<ArgumentOutOfRangeException>(() => state.ReceivedWont(bad));
        Assert.Throws<ArgumentOutOfRangeException>(() => state.ReceivedDo(bad, true));
        Assert.Throws<ArgumentOutOfRangeException>(() => state.ReceivedDont(bad));
        Assert.Throws<ArgumentOutOfRangeException>(() => state.RequestEnable(bad));
        Assert.Throws<ArgumentOutOfRangeException>(() => state.RequestDisable(bad));
        Assert.Throws<ArgumentOutOfRangeException>(() => state.OfferEnable(bad));
        Assert.Throws<ArgumentOutOfRangeException>(() => state.OfferDisable(bad));
        Assert.Throws<ArgumentOutOfRangeException>(() => state.IsEnabledByPeer(bad));
        Assert.Throws<ArgumentOutOfRangeException>(() => state.IsEnabledByUs(bad));
        Assert.Throws<ArgumentOutOfRangeException>(() => state.WasRefusedByPeer(bad));
        Assert.Throws<ArgumentOutOfRangeException>(() => state.WasRefusedByUs(bad));
      }
    }
  }

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
      var prior = Client.SkipProactiveOptionNegotiation;
      try
      {
        Client.SkipProactiveOptionNegotiation = true;
        using var stream = new ScriptedStream();
        using var client = new Client(stream, new CancellationToken());
        stream.Enqueue(255, 253, 3);
        (await ReadOnceAsync(client)).Should().BeEmpty();
        stream.Enqueue(255, 253, 3);
        (await ReadOnceAsync(client)).Should().BeEmpty();
        CountWrites(stream, 251, 3).Should().Be(1);
      }
      finally { Client.SkipProactiveOptionNegotiation = prior; }
    }

    [Fact]
    public async Task RepeatedWillAcrossReads_RepliesOnce()
    {
      var prior = Client.SkipProactiveOptionNegotiation;
      try
      {
        Client.SkipProactiveOptionNegotiation = true;
        using var stream = new ScriptedStream();
        using var client = new Client(stream, new CancellationToken());
        stream.Enqueue(255, 251, 3);
        (await ReadOnceAsync(client)).Should().BeEmpty();
        stream.Enqueue(255, 251, 3);
        (await ReadOnceAsync(client)).Should().BeEmpty();
        CountWrites(stream, 253, 3).Should().Be(1);
      }
      finally { Client.SkipProactiveOptionNegotiation = prior; }
    }

    [Fact]
    public async Task WontAfterWill_AcksDisableThenHonoursNewStimulus()
    {
      var prior = Client.SkipProactiveOptionNegotiation;
      try
      {
        Client.SkipProactiveOptionNegotiation = true;
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
      finally { Client.SkipProactiveOptionNegotiation = prior; }
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
      var prior = Client.SkipProactiveOptionNegotiation;
      try
      {
        Client.SkipProactiveOptionNegotiation = true;
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
      finally { Client.SkipProactiveOptionNegotiation = prior; }
    }

    [Fact]
    public async Task QueuedDisable_DrainsOnCompletion()
    {
      var prior = Client.SkipProactiveOptionNegotiation;
      try
      {
        Client.SkipProactiveOptionNegotiation = true;
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
      finally { Client.SkipProactiveOptionNegotiation = prior; }
    }

    [Fact]
    public async Task OutstandingEnable_SuppressesSecondRequest()
    {
      var prior = Client.SkipProactiveOptionNegotiation;
      try
      {
        Client.SkipProactiveOptionNegotiation = true;
        using var stream = new ScriptedStream();
        using var client = new Client(stream, new CancellationToken());
        await client.RequestEnableAsync(Options.TerminalType);
        await client.RequestEnableAsync(Options.TerminalType);
        CountWrites(stream, 253, 24).Should().Be(1);
      }
      finally { Client.SkipProactiveOptionNegotiation = prior; }
    }
  }

  public class ControlSignalTests
  {
    private static Client MakeClient(ScriptedStream stream)
    {
      return new Client(stream, new CancellationToken());
    }

    private static async Task<(string Output, ScriptedStream Stream)> ReadWithStreamAsync(params int[] reads)
    {
      var stream = new ScriptedStream(reads);
      using var cts = new CancellationTokenSource();
      using var sut = new ByteStreamHandler(stream, cts, 1);
      var output = await sut.ReadAsync(TimeSpan.FromMilliseconds(50));
      return (output, stream);
    }

    public static TheoryData<Commands, byte> SendableCommands => new()
    {
      { Commands.NoOperation, 241 },
      { Commands.Break, 243 },
      { Commands.InterruptProcess, 244 },
      { Commands.AbortOutput, 245 },
      { Commands.AreYouThere, 246 },
      { Commands.EraseCharacter, 247 },
      { Commands.EraseLine, 248 },
      { Commands.GoAhead, 249 },
    };

    [Theory]
    [MemberData(nameof(SendableCommands))]
    public async Task SendCommand_WritesIacFramedPair(Commands command, byte code)
    {
      using var stream = new ScriptedStream();
      using var sut = MakeClient(stream);
      await sut.SendCommand(command);
      stream.ByteWrites.Should().ContainSingle(w => w.SequenceEqual(new byte[] { 255, code }));
    }

    public static TheoryData<Commands> RejectedCommands => new()
    {
      Commands.Do,
      Commands.Dont,
      Commands.Will,
      Commands.Wont,
      Commands.Subnegotiation,
      Commands.SubnegotiationEnd,
      Commands.InterpretAsCommand,
      Commands.Data, // DM travels out-of-band via SendSynchAsync, never in-band.
      (Commands)99,
    };

    [Theory]
    [MemberData(nameof(RejectedCommands))]
    public async Task SendCommand_RejectsNegotiationVerbs(Commands command)
    {
      using var stream = new ScriptedStream();
      using var sut = MakeClient(stream);
      var before = stream.ByteWrites.Count;
      Func<Task> act = () => sut.SendCommand(command);
      await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
      stream.ByteWrites.Should().HaveCount(before);
    }

    [Fact]
    public async Task Ayt_GetsProofAliveReply()
    {
      var (output, stream) = await ReadWithStreamAsync(255, 246);
      output.Should().BeEmpty();
      stream.ByteWrites.Should().ContainSingle().Which.Should().Equal(Encoding.ASCII.GetBytes("[AYT received]\r\n"));
    }

    [Fact]
    public async Task Ao_IsConsumedSilently()
    {
      var (output, stream) = await ReadWithStreamAsync(255, 245);
      output.Should().BeEmpty();
      stream.ByteWrites.Should().BeEmpty();
    }

    [Fact]
    public async Task Ec_ErasesLastChar()
    {
      var (output, _) = await ReadWithStreamAsync(65, 66, 255, 247);
      output.Should().Be("A");
    }

    [Fact]
    public async Task Ec_OnEmptyBuffer_IsNoop()
    {
      var (output, _) = await ReadWithStreamAsync(255, 247);
      output.Should().BeEmpty();
    }

    [Fact]
    public async Task El_ErasesToLastNewline()
    {
      var (output, _) = await ReadWithStreamAsync(65, 66, 13, 10, 67, 68, 255, 248);
      output.Should().Be("AB\r\n");
    }

    [Fact]
    public async Task El_WithNoNewline_ClearsAll()
    {
      var (output, _) = await ReadWithStreamAsync(65, 66, 255, 248);
      output.Should().BeEmpty();
    }

    [Fact]
    public async Task El_OnEmptyBuffer_IsNoop()
    {
      var (output, _) = await ReadWithStreamAsync(255, 248);
      output.Should().BeEmpty();
    }

    private static async Task<string> ReadWithEncodingAsync(Encoding encoding, params int[] reads)
    {
      using var stream = new ScriptedStream(reads);
      using var cts = new CancellationTokenSource();
      using var sut = new ByteStreamHandler(stream, cts, 1) { TextEncoding = encoding };
      return await sut.ReadAsync(TimeSpan.FromMilliseconds(50));
    }

    [Fact]
    public async Task Ec_KeepsRawBytesInSync()
    {
      // Decoded from rawBytes (not sb): stale bytes would surface here.
      (await ReadWithEncodingAsync(Encoding.Latin1, 65, 66, 67, 255, 247)).Should().Be("AB");
    }

    [Fact]
    public async Task El_KeepsRawBytesInSync()
    {
      (await ReadWithEncodingAsync(Encoding.Latin1, 65, 66, 13, 10, 67, 255, 248)).Should().Be("AB\r\n");
    }

    [Fact]
    public async Task Brk_SurfacesMarker()
    {
      var (output, _) = await ReadWithStreamAsync(255, 243);
      output.Should().Be("[BRK]");
    }

    [Theory]
    [InlineData(240)] // stray SE
    [InlineData(241)] // NOP
    [InlineData(242)] // DM in normal mode stays a NOP
    [InlineData(249)] // GA (a NOP while Suppress-GA holds)
    public async Task SwallowedCommands_StaySilent(int verb)
    {
      var (output, stream) = await ReadWithStreamAsync(255, verb);
      output.Should().BeEmpty();
      stream.ByteWrites.Should().BeEmpty();
    }

    [Fact]
    public async Task SendSynch_RequiresTcpStream()
    {
      using var stream = new ScriptedStream();
      using var sut = MakeClient(stream);
      Func<Task> act = () => sut.SendSynchAsync();
      await act.Should().ThrowAsync<NotSupportedException>();
    }
  }

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
      var prior = Client.SkipProactiveOptionNegotiation;
      try
      {
        Client.SkipProactiveOptionNegotiation = true;
        using var stream = new ScriptedStream();
        using var client = new Client(stream, new CancellationToken());
        stream.Enqueue(255, 253, 36);
        (await ReadClientOnceAsync(client)).Should().BeEmpty();
        client.Settings.EnvironmentUser = "carol";
        (await ReadClientOnceAsync(client)).Should().BeEmpty();
        var expected = ExpectedIsFrame(2, Concat([0], L("USER"), [1], L("carol")));
        stream.ByteWrites.Should().ContainSingle(w => w.Length > 3 && w[1] == 250).Which.Should().Equal(expected);
      }
      finally { Client.SkipProactiveOptionNegotiation = prior; }
    }

    [Fact]
    public async Task EnvironInfo_NotSentWhenPeerDisagreed()
    {
      var prior = Client.SkipProactiveOptionNegotiation;
      try
      {
        Client.SkipProactiveOptionNegotiation = true;
        using var stream = new ScriptedStream();
        using var client = new Client(stream, new CancellationToken());
        (await ReadClientOnceAsync(client)).Should().BeEmpty();
        client.Settings.EnvironmentUser = "carol";
        (await ReadClientOnceAsync(client)).Should().BeEmpty();
        CountSubnegotiations(stream).Should().Be(0);
      }
      finally { Client.SkipProactiveOptionNegotiation = prior; }
    }

    [Fact]
    public async Task EnvironInfo_NotResentWhenUnchanged()
    {
      var prior = Client.SkipProactiveOptionNegotiation;
      try
      {
        Client.SkipProactiveOptionNegotiation = true;
        using var stream = new ScriptedStream();
        using var client = new Client(stream, new CancellationToken());
        stream.Enqueue(255, 253, 36);
        (await ReadClientOnceAsync(client)).Should().BeEmpty();
        client.Settings.EnvironmentUser = "carol";
        (await ReadClientOnceAsync(client)).Should().BeEmpty();
        (await ReadClientOnceAsync(client)).Should().BeEmpty();
        CountSubnegotiations(stream).Should().Be(1);
      }
      finally { Client.SkipProactiveOptionNegotiation = prior; }
    }
  }

  public class StatusTimingMarkTests
  {
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

    private static async Task<string> ReadClientOnceAsync(Client client)
    {
      return await client.ReadAsync(TimeSpan.FromMilliseconds(100));
    }

    [Fact]
    public async Task StatusSend_NoAgreements_ReturnsBareIs()
    {
      var (output, stream) = await ReadHandlerOnceAsync(static _ => { }, 255, 250, 5, 1, 255, 240);
      output.Should().BeEmpty();
      stream.ByteWrites.Should().ContainSingle().Which.Should().Equal(new byte[] { 255, 250, 5, 0, 240 });
    }

    [Fact]
    public async Task StatusSend_AfterDoSga_ReportsWillSga()
    {
      var (output, stream) = await ReadHandlerOnceAsync(static _ => { }, 255, 253, 3, 255, 250, 5, 1, 255, 240);
      output.Should().BeEmpty();
      stream.ByteWrites.Should().HaveCount(2);
      stream.ByteWrites[0].Should().Equal(new byte[] { 255, 251, 3 });
      stream.ByteWrites[1].Should().Equal(new byte[] { 255, 250, 5, 0, 251, 3, 240 });
    }

    [Fact]
    public async Task StatusSend_AfterWillSga_ReportsDoSga()
    {
      var (output, stream) = await ReadHandlerOnceAsync(static _ => { }, 255, 251, 3, 255, 250, 5, 1, 255, 240);
      output.Should().BeEmpty();
      stream.ByteWrites.Should().HaveCount(2);
      stream.ByteWrites[0].Should().Equal(new byte[] { 255, 253, 3 });
      stream.ByteWrites[1].Should().Equal(new byte[] { 255, 250, 5, 0, 253, 3, 240 });
    }

    [Fact]
    public async Task StatusSend_RefusedOption_Omitted()
    {
      // NEW-ENVIRON (39) stays refused (P3 scope pin); ECHO is agreed since P9.
      var (output, stream) = await ReadHandlerOnceAsync(static _ => { }, 255, 251, 39, 255, 250, 5, 1, 255, 240);
      output.Should().BeEmpty();
      stream.ByteWrites.Should().HaveCount(2);
      stream.ByteWrites[0].Should().Equal(new byte[] { 255, 254, 39 });
      stream.ByteWrites[1].Should().Equal(new byte[] { 255, 250, 5, 0, 240 });
    }

    [Fact]
    public async Task StatusSend_AfterRevoke_OmitsAgain()
    {
      var (output, stream) = await ReadHandlerOnceAsync(static _ => { }, 255, 253, 3, 255, 254, 3, 255, 250, 5, 1, 255, 240);
      output.Should().BeEmpty();
      stream.ByteWrites.Should().HaveCount(3);
      stream.ByteWrites[0].Should().Equal(new byte[] { 255, 251, 3 });
      stream.ByteWrites[1].Should().Equal(new byte[] { 255, 252, 3 });
      stream.ByteWrites[2].Should().Equal(new byte[] { 255, 250, 5, 0, 240 });
    }

    [Fact]
    public async Task StatusSend_BothSides_ReportsBoth()
    {
      var (output, stream) = await ReadHandlerOnceAsync(static _ => { }, 255, 253, 3, 255, 251, 3, 255, 250, 5, 1, 255, 240);
      output.Should().BeEmpty();
      stream.ByteWrites.Should().HaveCount(3);
      stream.ByteWrites[0].Should().Equal(new byte[] { 255, 251, 3 });
      stream.ByteWrites[1].Should().Equal(new byte[] { 255, 253, 3 });
      stream.ByteWrites[2].Should().Equal(new byte[] { 255, 250, 5, 0, 251, 3, 253, 3, 240 });
    }

    [Fact]
    public async Task StatusSend_StrayIs_GetsWont()
    {
      var (output, stream) = await ReadHandlerOnceAsync(static _ => { }, 255, 250, 5, 0, 255, 240);
      output.Should().BeEmpty();
      stream.ByteWrites.Should().ContainSingle().Which.Should().Equal(new byte[] { 255, 252, 5 });
    }

    [Fact]
    public async Task DoTimingMark_GetsWill()
    {
      var (output, stream) = await ReadHandlerOnceAsync(static _ => { }, 255, 253, 6);
      output.Should().BeEmpty();
      stream.ByteWrites.Should().ContainSingle().Which.Should().Equal(new byte[] { 255, 251, 6 });
    }

    [Fact]
    public async Task DoTimingMark_AfterData_DataDeliveredWithMarkAnswered()
    {
      var (output, stream) = await ReadHandlerOnceAsync(static _ => { }, 104, 105, 255, 253, 6);
      output.Should().Be("hi");
      stream.ByteWrites.Should().ContainSingle().Which.Should().Equal(new byte[] { 255, 251, 6 });
    }

    [Fact]
    public async Task WillTimingMark_GetsDo()
    {
      var (output, stream) = await ReadHandlerOnceAsync(static _ => { }, 255, 251, 6);
      output.Should().BeEmpty();
      stream.ByteWrites.Should().ContainSingle().Which.Should().Equal(new byte[] { 255, 253, 6 });
    }

    [Fact]
    public async Task SendTimingMarkAsync_SendsDo()
    {
      var prior = Client.SkipProactiveOptionNegotiation;
      try
      {
        Client.SkipProactiveOptionNegotiation = true;
        using var stream = new ScriptedStream();
        using var client = new Client(stream, new CancellationToken());
        await client.SendTimingMarkAsync();
        stream.ByteWrites.Should().ContainSingle().Which.Should().Equal(new byte[] { 255, 253, 6 });
      }
      finally { Client.SkipProactiveOptionNegotiation = prior; }
    }

    [Fact]
    public async Task SendTimingMarkAsync_TwiceWhileOutstanding_SendsOnce()
    {
      var prior = Client.SkipProactiveOptionNegotiation;
      try
      {
        Client.SkipProactiveOptionNegotiation = true;
        using var stream = new ScriptedStream();
        using var client = new Client(stream, new CancellationToken());
        await client.SendTimingMarkAsync();
        await client.SendTimingMarkAsync();
        stream.ByteWrites.Should().ContainSingle().Which.Should().Equal(new byte[] { 255, 253, 6 });
      }
      finally { Client.SkipProactiveOptionNegotiation = prior; }
    }

    [Fact]
    public async Task TimingMarkRoundTrip_PeerWill_CompletesWithoutReply()
    {
      var prior = Client.SkipProactiveOptionNegotiation;
      try
      {
        Client.SkipProactiveOptionNegotiation = true;
        using var stream = new ScriptedStream();
        using var client = new Client(stream, new CancellationToken());
        await client.SendTimingMarkAsync();
        stream.Enqueue(255, 251, 6);
        (await ReadClientOnceAsync(client)).Should().BeEmpty();
        stream.ByteWrites.Should().ContainSingle().Which.Should().Equal(new byte[] { 255, 253, 6 });
      }
      finally { Client.SkipProactiveOptionNegotiation = prior; }
    }

    [Fact]
    public async Task StatusSend_ReportsOutstandingDo()
    {
      var prior = Client.SkipProactiveOptionNegotiation;
      try
      {
        Client.SkipProactiveOptionNegotiation = true;
        using var stream = new ScriptedStream();
        using var client = new Client(stream, new CancellationToken());
        await client.SendTimingMarkAsync();
        stream.Enqueue(255, 250, 5, 1, 255, 240);
        (await ReadClientOnceAsync(client)).Should().BeEmpty();
        stream.ByteWrites.Should().HaveCount(2);
        stream.ByteWrites[1].Should().Equal(new byte[] { 255, 250, 5, 0, 253, 6, 240 });
      }
      finally { Client.SkipProactiveOptionNegotiation = prior; }
    }

    [Fact]
    public async Task StatusSend_ReportsOutstandingWill()
    {
      var prior = Client.SkipProactiveOptionNegotiation;
      try
      {
        Client.SkipProactiveOptionNegotiation = true;
        using var stream = new ScriptedStream();
        using var client = new Client(
          stream,
          TimeSpan.FromSeconds(30),
          new CancellationToken(),
          [(Commands.Will, Options.TimingMark)]);
        stream.Enqueue(255, 250, 5, 1, 255, 240);
        (await ReadClientOnceAsync(client)).Should().BeEmpty();
        stream.ByteWrites.Should().HaveCount(2);
        stream.ByteWrites[0].Should().Equal(new byte[] { 255, 251, 6 });
        stream.ByteWrites[1].Should().Equal(new byte[] { 255, 250, 5, 0, 251, 6, 240 });
      }
      finally { Client.SkipProactiveOptionNegotiation = prior; }
    }

    [Fact]
    public async Task StatusSend_SeOptionByte_Doubled()
    {
      var prior = Client.SkipProactiveOptionNegotiation;
      try
      {
        Client.SkipProactiveOptionNegotiation = true;
        using var stream = new ScriptedStream();
        using var client = new Client(stream, new CancellationToken());
        await client.RequestEnableAsync((Options)240);
        stream.Enqueue(255, 250, 5, 1, 255, 240);
        (await ReadClientOnceAsync(client)).Should().BeEmpty();
        stream.ByteWrites.Should().HaveCount(2);
        stream.ByteWrites[0].Should().Equal(new byte[] { 255, 253, 240 });
        stream.ByteWrites[1].Should().Equal(new byte[] { 255, 250, 5, 0, 253, 240, 240, 240 });
      }
      finally { Client.SkipProactiveOptionNegotiation = prior; }
    }
  }

  public class TerminalTypeSpeedTests
  {
    private static readonly int[] TypeSend = [255, 250, 24, 1, 255, 240];

    private static readonly int[] SpeedSend = [255, 250, 32, 1, 255, 240];

    private static byte[] TypeIsFrame(string type) =>
      [255, 250, 24, 0, .. Encoding.Latin1.GetBytes(type), 255, 240];

    private static byte[] SpeedIsFrame(string speed) =>
      [255, 250, 32, 0, .. Encoding.Latin1.GetBytes(speed), 255, 240];

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

    private static async Task<string> ReadClientOnceAsync(Client client)
    {
      return await client.ReadAsync(TimeSpan.FromMilliseconds(100));
    }

    private static async Task<IReadOnlyList<byte[]>> SendThroughClientAsync(Client client, ScriptedStream stream, int reads)
    {
      for (var i = 0; i < reads; i++)
      {
        stream.Enqueue(TypeSend);
        (await ReadClientOnceAsync(client)).Should().BeEmpty();
      }

      return stream.ByteWrites;
    }

    [Fact]
    public async Task TypeSend_WalksListAcrossReads()
    {
      var prior = Client.SkipProactiveOptionNegotiation;
      try
      {
        Client.SkipProactiveOptionNegotiation = true;
        using var stream = new ScriptedStream();
        using var client = new Client(stream, new CancellationToken());
        client.Settings.TerminalTypes.Add("xterm-256color");
        client.Settings.TerminalTypes.Add("xterm");
        client.Settings.TerminalTypes.Add("vt100");
        var writes = await SendThroughClientAsync(client, stream, 3);
        writes.Should().HaveCount(3);
        writes[0].Should().Equal(TypeIsFrame("xterm-256color"));
        writes[1].Should().Equal(TypeIsFrame("xterm"));
        writes[2].Should().Equal(TypeIsFrame("vt100"));
      }
      finally { Client.SkipProactiveOptionNegotiation = prior; }
    }

    [Fact]
    public async Task TypeSend_SameTwiceThenWraps()
    {
      var prior = Client.SkipProactiveOptionNegotiation;
      try
      {
        Client.SkipProactiveOptionNegotiation = true;
        using var stream = new ScriptedStream();
        using var client = new Client(stream, new CancellationToken());
        client.Settings.TerminalTypes.Add("aaa");
        client.Settings.TerminalTypes.Add("bbb");
        var writes = await SendThroughClientAsync(client, stream, 4);
        writes.Should().HaveCount(4);
        writes[0].Should().Equal(TypeIsFrame("aaa"));
        writes[1].Should().Equal(TypeIsFrame("bbb"));
        writes[2].Should().Equal(TypeIsFrame("bbb"));
        writes[3].Should().Equal(TypeIsFrame("aaa"));
      }
      finally { Client.SkipProactiveOptionNegotiation = prior; }
    }

    [Fact]
    public async Task TypeSend_TruncatesTo40Chars()
    {
      var prior = Client.SkipProactiveOptionNegotiation;
      try
      {
        Client.SkipProactiveOptionNegotiation = true;
        using var stream = new ScriptedStream();
        using var client = new Client(stream, new CancellationToken());
        client.Settings.TerminalTypes.Add(new string('a', 41));
        var writes = await SendThroughClientAsync(client, stream, 1);
        writes.Should().ContainSingle().Which.Should().Equal(TypeIsFrame(new string('a', 40)));
      }
      finally { Client.SkipProactiveOptionNegotiation = prior; }
    }

    [Fact]
    public async Task TypeSend_EmptyListFallsBackToSingle()
    {
      var prior = Client.SkipProactiveOptionNegotiation;
      try
      {
        Client.SkipProactiveOptionNegotiation = true;
        using var stream = new ScriptedStream();
        using var client = new Client(stream, new CancellationToken());
        client.Settings.TerminalType = "xterm";
        var writes = await SendThroughClientAsync(client, stream, 2);
        writes.Should().HaveCount(2);
        writes[0].Should().Equal(TypeIsFrame("xterm"));
        writes[1].Should().Equal(TypeIsFrame("xterm"));
      }
      finally { Client.SkipProactiveOptionNegotiation = prior; }
    }

    [Fact]
    public async Task TypeSend_EmptyStringFallsBackToUnknown()
    {
      var prior = Client.SkipProactiveOptionNegotiation;
      try
      {
        Client.SkipProactiveOptionNegotiation = true;
        using var stream = new ScriptedStream();
        using var client = new Client(stream, new CancellationToken());
        client.Settings.TerminalType = string.Empty;
        var writes = await SendThroughClientAsync(client, stream, 1);
        writes.Should().ContainSingle().Which.Should().Equal(TypeIsFrame("UNKNOWN"));
      }
      finally { Client.SkipProactiveOptionNegotiation = prior; }
    }

    [Fact]
    public async Task TypeSend_ListChangeResetsCycle()
    {
      var prior = Client.SkipProactiveOptionNegotiation;
      try
      {
        Client.SkipProactiveOptionNegotiation = true;
        using var stream = new ScriptedStream();
        using var client = new Client(stream, new CancellationToken());
        client.Settings.TerminalTypes.Add("aaa");
        client.Settings.TerminalTypes.Add("bbb");
        var first = await SendThroughClientAsync(client, stream, 1);
        first.Should().ContainSingle().Which.Should().Equal(TypeIsFrame("aaa"));
        client.Settings.TerminalTypes.Clear();
        client.Settings.TerminalTypes.Add("zzz");
        stream.Enqueue(TypeSend);
        (await ReadClientOnceAsync(client)).Should().BeEmpty();
        stream.ByteWrites.Should().HaveCount(2);
        stream.ByteWrites[1].Should().Equal(TypeIsFrame("zzz"));
      }
      finally { Client.SkipProactiveOptionNegotiation = prior; }
    }

    [Fact]
    public async Task TypeSend_DirectHandlerRepeatsString()
    {
      var (output, stream) = await ReadHandlerOnceAsync(
        static h => h.TerminalType = "xterm",
        [.. TypeSend, .. TypeSend]);
      output.Should().BeEmpty();
      stream.ByteWrites.Should().HaveCount(2);
      stream.ByteWrites[0].Should().Equal(TypeIsFrame("xterm"));
      stream.ByteWrites[1].Should().Equal(TypeIsFrame("xterm"));
    }

    [Fact]
    public async Task SpeedSend_ValidStaysUnchanged()
    {
      var (output, stream) = await ReadHandlerOnceAsync(
        static h => h.TerminalSpeed = "19200,19200", SpeedSend);
      output.Should().BeEmpty();
      stream.ByteWrites.Should().ContainSingle().Which.Should().Equal(SpeedIsFrame("19200,19200"));
    }

    [Fact]
    public async Task SpeedSend_StripsLeadingZeros()
    {
      var (output, stream) = await ReadHandlerOnceAsync(
        static h => h.TerminalSpeed = "019200,009600", SpeedSend);
      output.Should().BeEmpty();
      stream.ByteWrites.Should().ContainSingle().Which.Should().Equal(SpeedIsFrame("19200,9600"));
    }

    [Fact]
    public async Task SpeedSend_RoundsToNearest()
    {
      var (output, stream) = await ReadHandlerOnceAsync(
        static h => h.TerminalSpeed = "1000,1000", SpeedSend);
      output.Should().BeEmpty();
      stream.ByteWrites.Should().ContainSingle().Which.Should().Equal(SpeedIsFrame("1200,1200"));
    }

    [Fact]
    public async Task SpeedSend_TieRoundsUp()
    {
      var (output, stream) = await ReadHandlerOnceAsync(
        static h => h.TerminalSpeed = "142,142", SpeedSend);
      output.Should().BeEmpty();
      stream.ByteWrites.Should().ContainSingle().Which.Should().Equal(SpeedIsFrame("150,150"));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("9600")]
    [InlineData("9600,4800,2400")]
    [InlineData("")]
    [InlineData("9600,abc")]
    [InlineData(" 9600,9600")]
    public async Task SpeedSend_MalformedSendsNothing(string speed)
    {
      var (output, stream) = await ReadHandlerOnceAsync(h => h.TerminalSpeed = speed, SpeedSend);
      output.Should().BeEmpty();
      stream.ByteWrites.Should().BeEmpty();
    }
  }

  public class NawsRefreshTests
  {
    private static byte[] NawsFrame(int width, int height) => new byte[]
    {
      255, 250, 31, 0, (byte)(width >> 8), (byte)width, (byte)(height >> 8), (byte)height, 255, 240,
    };

    private static async Task<string> ReadClientOnceAsync(Client client)
    {
      return await client.ReadAsync(TimeSpan.FromMilliseconds(100));
    }

    [Fact]
    public async Task RefreshWindowSize_AfterNegotiation_SendsChangedSize()
    {
      var prior = Client.SkipProactiveOptionNegotiation;
      try
      {
        Client.SkipProactiveOptionNegotiation = true;
        using var stream = new ScriptedStream();
        using var client = new Client(stream, new CancellationToken());
        client.Settings.WindowWidth = 100;
        client.Settings.WindowHeight = 30;
        stream.Enqueue(255, 253, 31);
        (await ReadClientOnceAsync(client)).Should().BeEmpty();
        stream.ByteWrites.Should().HaveCount(2);
        stream.ByteWrites[0].Should().Equal(new byte[] { 255, 251, 31 });
        stream.ByteWrites[1].Should().Equal(NawsFrame(100, 30));
        client.Settings.WindowWidth = 120;
        client.Settings.WindowHeight = 40;
        await client.RefreshWindowSizeAsync();
        stream.ByteWrites.Should().HaveCount(3);
        stream.ByteWrites[2].Should().Equal(NawsFrame(120, 40));
      }
      finally { Client.SkipProactiveOptionNegotiation = prior; }
    }

    [Fact]
    public async Task RefreshWindowSize_Unchanged_DoesNotResend()
    {
      var prior = Client.SkipProactiveOptionNegotiation;
      try
      {
        Client.SkipProactiveOptionNegotiation = true;
        using var stream = new ScriptedStream();
        using var client = new Client(stream, new CancellationToken());
        client.Settings.WindowWidth = 100;
        client.Settings.WindowHeight = 30;
        stream.Enqueue(255, 253, 31);
        (await ReadClientOnceAsync(client)).Should().BeEmpty();
        stream.ByteWrites.Should().HaveCount(2);
        await client.RefreshWindowSizeAsync();
        stream.ByteWrites.Should().HaveCount(2);
      }
      finally { Client.SkipProactiveOptionNegotiation = prior; }
    }

    [Fact]
    public async Task RefreshWindowSize_WhenNeverNegotiated_SendsNothing()
    {
      var prior = Client.SkipProactiveOptionNegotiation;
      try
      {
        Client.SkipProactiveOptionNegotiation = true;
        using var stream = new ScriptedStream();
        using var client = new Client(stream, new CancellationToken());
        client.Settings.WindowWidth = 100;
        client.Settings.WindowHeight = 30;
        await client.RefreshWindowSizeAsync();
        stream.ByteWrites.Should().BeEmpty();
      }
      finally { Client.SkipProactiveOptionNegotiation = prior; }
    }

    [Fact]
    public async Task RefreshWindowSize_AfterDont_Suppressed()
    {
      var prior = Client.SkipProactiveOptionNegotiation;
      try
      {
        Client.SkipProactiveOptionNegotiation = true;
        using var stream = new ScriptedStream();
        using var client = new Client(stream, new CancellationToken());
        client.Settings.WindowWidth = 100;
        client.Settings.WindowHeight = 30;
        stream.Enqueue(255, 253, 31);
        (await ReadClientOnceAsync(client)).Should().BeEmpty();
        stream.Enqueue(255, 254, 31);
        (await ReadClientOnceAsync(client)).Should().BeEmpty();
        stream.ByteWrites.Should().HaveCount(3);
        stream.ByteWrites[2].Should().Equal(new byte[] { 255, 252, 31 });
        client.Settings.WindowWidth = 120;
        client.Settings.WindowHeight = 40;
        await client.RefreshWindowSizeAsync();
        stream.ByteWrites.Should().HaveCount(3);
      }
      finally { Client.SkipProactiveOptionNegotiation = prior; }
    }

    [Theory]
    [InlineData(100, 30, 100, 30)]
    [InlineData(70000, 30, 80, 30)]
    [InlineData(100, 90000, 100, 24)]
    [InlineData(65535, 65535, 65535, 65535)]
    public void GetEffectiveSize_ClampsDimensions(int width, int height, int expectedWidth, int expectedHeight)
    {
      NawsProtocol.GetEffectiveSize(width, height).Should().Be(((ushort)expectedWidth, (ushort)expectedHeight));
    }
  }

  public class LinemodeTests
  {
    private static async Task<(string Output, ScriptedStream Stream)> ReadWithStreamAsync(params int[] reads)
    {
      var stream = new ScriptedStream(reads);
      using var cts = new CancellationTokenSource();
      using var sut = new ByteStreamHandler(stream, cts, 1);
      var output = await sut.ReadAsync(TimeSpan.FromMilliseconds(50));
      return (output, stream);
    }

    private static async Task<string> ReadClientOnceAsync(Client client)
    {
      return await client.ReadAsync(TimeSpan.FromMilliseconds(100));
    }

    public static TheoryData<Commands, byte> NewControlCommands => new()
    {
      { Commands.EndOfFile, 236 },
      { Commands.Suspend, 237 },
      { Commands.Abort, 238 },
    };

    [Theory]
    [MemberData(nameof(NewControlCommands))]
    public async Task SendCommand_EofSuspendAbort_WritesIacFramedPair(Commands command, byte code)
    {
      using var stream = new ScriptedStream();
      using var sut = new Client(stream, new CancellationToken());
      await sut.SendCommand(command);
      stream.ByteWrites.Should().ContainSingle(w => w.SequenceEqual(new byte[] { 255, code }));
    }

    public static TheoryData<int, string> SignalMarkers => new()
    {
      { 236, "[EOF]" },
      { 237, "[SUSP]" },
      { 238, "[ABORT]" },
    };

    [Theory]
    [MemberData(nameof(SignalMarkers))]
    public async Task SignalReceived_SurfacesMarker(int code, string marker)
    {
      var (output, _) = await ReadWithStreamAsync(255, code);
      output.Should().Be(marker);
    }

    [Fact]
    public async Task DoLinemode_IsAgreed()
    {
      var (_, stream) = await ReadWithStreamAsync(255, 253, 34);
      stream.ByteWrites.Should().ContainSingle().Which.Should().Equal(new byte[] { 255, 251, 34 });
    }

    [Fact]
    public async Task ModeRequest_IsConfirmedWithAck()
    {
      var (output, stream) = await ReadWithStreamAsync(255, 250, 34, 1, 3, 255, 240);
      output.Should().BeEmpty();
      stream.ByteWrites.Should().ContainSingle().Which.Should()
        .Equal(new byte[] { 255, 250, 34, 1, 7, 255, 240 });
    }

    [Fact]
    public async Task ModeRepeatAcrossReads_RepliesOnce()
    {
      var prior = Client.SkipProactiveOptionNegotiation;
      try
      {
        Client.SkipProactiveOptionNegotiation = true;
        using var stream = new ScriptedStream();
        using var client = new Client(stream, new CancellationToken());
        stream.Enqueue(255, 250, 34, 1, 3, 255, 240);
        (await ReadClientOnceAsync(client)).Should().BeEmpty();
        stream.Enqueue(255, 250, 34, 1, 3, 255, 240);
        (await ReadClientOnceAsync(client)).Should().BeEmpty();
        stream.ByteWrites.Should().ContainSingle().Which.Should()
          .Equal(new byte[] { 255, 250, 34, 1, 7, 255, 240 });
      }
      finally { Client.SkipProactiveOptionNegotiation = prior; }
    }

    [Fact]
    public async Task ModeUnsupportedBits_AnsweredAsSubset()
    {
      // EDIT | TRAPSIG | SOFT_TAB | LIT_ECHO: the terminal-processing bits
      // are dropped, EDIT/TRAPSIG are never cleared, ACK is set.
      var (_, stream) = await ReadWithStreamAsync(255, 250, 34, 1, 27, 255, 240);
      stream.ByteWrites.Should().ContainSingle().Which.Should()
        .Equal(new byte[] { 255, 250, 34, 1, 7, 255, 240 });
    }

    [Fact]
    public async Task ModeAck_IsNeverAnswered()
    {
      using var stream = new ScriptedStream();
      using var cts = new CancellationTokenSource();
      using var sut = new ByteStreamHandler(stream, cts, 1);
      stream.Enqueue(255, 250, 34, 1, 3, 255, 240);
      (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().BeEmpty();
      stream.Enqueue(255, 250, 34, 1, 7, 255, 240);
      (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().BeEmpty();
      stream.ByteWrites.Should().ContainSingle().Which.Should()
        .Equal(new byte[] { 255, 250, 34, 1, 7, 255, 240 });
    }

    [Fact]
    public async Task ModeTruncated_Ignored()
    {
      var (output, stream) = await ReadWithStreamAsync(255, 250, 34, 1, 255, 240);
      output.Should().BeEmpty();
      stream.ByteWrites.Should().BeEmpty();
    }

    [Fact]
    public async Task ForwardMaskProposal_RefusedWithWont()
    {
      var (output, stream) = await ReadWithStreamAsync(255, 250, 34, 253, 2, 0, 0, 255, 240);
      output.Should().BeEmpty();
      stream.ByteWrites.Should().ContainSingle().Which.Should()
        .Equal(new byte[] { 255, 250, 34, 252, 2, 255, 240 });
    }

    public static TheoryData<int> ForwardMaskSilentVerbs => new()
    {
      254, // DONT: accepted (we never forward anyway).
      251, // WILL: unsolicited (we never sent DO), ignored.
      252, // WONT: unsolicited, ignored.
    };

    [Theory]
    [MemberData(nameof(ForwardMaskSilentVerbs))]
    public async Task ForwardMaskNonProposal_Silent(int verb)
    {
      var (output, stream) = await ReadWithStreamAsync(255, 250, 34, verb, 2, 255, 240);
      output.Should().BeEmpty();
      stream.ByteWrites.Should().BeEmpty();
    }

    [Fact]
    public async Task ForwardMaskTruncated_Ignored()
    {
      var (output, stream) = await ReadWithStreamAsync(255, 250, 34, 253, 255, 240);
      output.Should().BeEmpty();
      stream.ByteWrites.Should().BeEmpty();
    }

    [Fact]
    public async Task SlcServerValue_AgreedWithAck()
    {
      var (output, stream) = await ReadWithStreamAsync(255, 250, 34, 3, 3, 2, 9, 255, 240);
      output.Should().BeEmpty();
      stream.ByteWrites.Should().ContainSingle().Which.Should()
        .Equal(new byte[] { 255, 250, 34, 3, 3, 130, 9, 255, 240 });
    }

    [Fact]
    public async Task SlcRepeat_Ignored()
    {
      using var stream = new ScriptedStream();
      using var cts = new CancellationTokenSource();
      using var sut = new ByteStreamHandler(stream, cts, 1);
      stream.Enqueue(255, 250, 34, 3, 3, 2, 9, 255, 240);
      (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().BeEmpty();
      stream.Enqueue(255, 250, 34, 3, 3, 2, 9, 255, 240);
      (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().BeEmpty();
      stream.ByteWrites.Should().ContainSingle().Which.Should()
        .Equal(new byte[] { 255, 250, 34, 3, 3, 130, 9, 255, 240 });
    }

    [Fact]
    public async Task SlcAckedChange_SwitchesSilently()
    {
      using var stream = new ScriptedStream();
      using var cts = new CancellationTokenSource();
      using var sut = new ByteStreamHandler(stream, cts, 1);
      stream.Enqueue(255, 250, 34, 3, 3, 2, 9, 255, 240);
      (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().BeEmpty();
      // Same level, different value, ACK set: silent switch to 10.
      stream.Enqueue(255, 250, 34, 3, 3, 130, 10, 255, 240);
      (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().BeEmpty();
      // Proves the switch landed: 9 is now the change, agreed with ACK.
      stream.Enqueue(255, 250, 34, 3, 3, 2, 9, 255, 240);
      (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().BeEmpty();
      stream.ByteWrites.Should().HaveCount(2);
      stream.ByteWrites[1].Should().Equal(new byte[] { 255, 250, 34, 3, 3, 130, 9, 255, 240 });
    }

    [Fact]
    public async Task SlcCantChange_DisagreesWithoutAck()
    {
      using var stream = new ScriptedStream();
      using var cts = new CancellationTokenSource();
      using var sut = new ByteStreamHandler(stream, cts, 1);
      sut.Linemode.SetEntry(3, 1, 7);
      stream.Enqueue(255, 250, 34, 3, 3, 2, 9, 255, 240);
      (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().BeEmpty();
      stream.ByteWrites.Should().ContainSingle().Which.Should()
        .Equal(new byte[] { 255, 250, 34, 3, 3, 1, 7, 255, 240 });
    }

    [Fact]
    public async Task SlcUnknownFunction_RefusedAsDefault()
    {
      var (_, stream) = await ReadWithStreamAsync(255, 250, 34, 3, 31, 2, 65, 255, 240);
      stream.ByteWrites.Should().ContainSingle().Which.Should()
        .Equal(new byte[] { 255, 250, 34, 3, 31, 3, 0, 255, 240 });
    }

    [Fact]
    public async Task SlcTruncated_Ignored()
    {
      var (output, stream) = await ReadWithStreamAsync(255, 250, 34, 3, 3, 2, 255, 240);
      output.Should().BeEmpty();
      stream.ByteWrites.Should().BeEmpty();
    }

    [Fact]
    public async Task ImportRemoteSpecialCharacters_AfterNegotiation_SendsImport()
    {
      var prior = Client.SkipProactiveOptionNegotiation;
      try
      {
        Client.SkipProactiveOptionNegotiation = true;
        using var stream = new ScriptedStream();
        using var client = new Client(stream, new CancellationToken());
        stream.Enqueue(255, 253, 34);
        (await ReadClientOnceAsync(client)).Should().BeEmpty();
        await client.ImportRemoteSpecialCharactersAsync();
        stream.ByteWrites.Should().HaveCount(2);
        stream.ByteWrites[0].Should().Equal(new byte[] { 255, 251, 34 });
        stream.ByteWrites[1].Should().Equal(new byte[] { 255, 250, 34, 3, 0, 3, 0, 255, 240 });
      }
      finally { Client.SkipProactiveOptionNegotiation = prior; }
    }

    [Fact]
    public async Task ImportRemoteSpecialCharacters_WithoutNegotiation_Silent()
    {
      var prior = Client.SkipProactiveOptionNegotiation;
      try
      {
        Client.SkipProactiveOptionNegotiation = true;
        using var stream = new ScriptedStream();
        using var client = new Client(stream, new CancellationToken());
        await client.ImportRemoteSpecialCharactersAsync();
        stream.ByteWrites.Should().BeEmpty();
      }
      finally { Client.SkipProactiveOptionNegotiation = prior; }
    }

    [Fact]
    public async Task ExportSpecialCharacters_WithEntries_SendsTable()
    {
      var prior = Client.SkipProactiveOptionNegotiation;
      try
      {
        Client.SkipProactiveOptionNegotiation = true;
        using var stream = new ScriptedStream();
        using var client = new Client(stream, new CancellationToken());
        stream.Enqueue(255, 253, 34);
        (await ReadClientOnceAsync(client)).Should().BeEmpty();
        client.SetLinemodeEntry(3, 2, 9);
        client.SetLinemodeEntry(10, 2, 8);
        await client.ExportSpecialCharactersAsync();
        stream.ByteWrites.Should().HaveCount(2);
        stream.ByteWrites[1].Should().Equal(new byte[] { 255, 250, 34, 3, 3, 2, 9, 10, 2, 8, 255, 240 });
      }
      finally { Client.SkipProactiveOptionNegotiation = prior; }
    }

    [Fact]
    public async Task ExportSpecialCharacters_EmptyTable_Silent()
    {
      var prior = Client.SkipProactiveOptionNegotiation;
      try
      {
        Client.SkipProactiveOptionNegotiation = true;
        using var stream = new ScriptedStream();
        using var client = new Client(stream, new CancellationToken());
        stream.Enqueue(255, 253, 34);
        (await ReadClientOnceAsync(client)).Should().BeEmpty();
        await client.ExportSpecialCharactersAsync();
        stream.ByteWrites.Should().ContainSingle().Which.Should().Equal(new byte[] { 255, 251, 34 });
      }
      finally { Client.SkipProactiveOptionNegotiation = prior; }
    }
  }

  public class BinaryModeTests
  {
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

    private static async Task<string> ReadClientOnceAsync(Client client)
    {
      return await client.ReadAsync(TimeSpan.FromMilliseconds(100));
    }

    [Theory]
    [InlineData(100)]
    [InlineData(150)]
    [InlineData(200)]
    public async Task UndefinedCommand_IsSwallowedAsNop(int command)
    {
      // RFC 856 §5: IAC followed by an undefined command ≡ IAC NOP.
      var (output, stream) = await ReadHandlerOnceAsync(static _ => { }, 255, command);
      output.Should().BeEmpty();
      stream.ByteWrites.Should().BeEmpty();
    }

    [Fact]
    public async Task UndefinedCommand_DoesNotDisturbFollowingData()
    {
      var (output, _) = await ReadHandlerOnceAsync(static _ => { }, 255, 200, 72);
      output.Should().Be("H");
    }

    [Fact]
    public async Task HighBytes_PassThroughAfterBinaryAgreement()
    {
      // DO TransmitBinary is answered WILL; the following high bytes
      // (including a doubled IAC) arrive as Latin-1 chars.
      var (output, stream) = await ReadHandlerOnceAsync(
        static _ => { }, 255, 253, 0, 128, 200, 254, 255, 255);
      output.Should().Be("\u0080\u00c8\u00fe\u00ff");
      stream.ByteWrites.Should().ContainSingle().Which.Should().Equal(new byte[] { 255, 251, 0 });
    }

    [Fact]
    public async Task HighBytes_PassThroughWithoutAgreement_DocumentedDeviation()
    {
      // Strict 7-bit NVT would not carry these; this library passes them
      // through even when BINARY was never negotiated (see P8).
      var (output, stream) = await ReadHandlerOnceAsync(static _ => { }, 200, 255, 255);
      output.Should().Be("\u00c8\u00ff");
      stream.ByteWrites.Should().BeEmpty();
    }

    [Fact]
    public async Task BinaryAgreement_ClientLevel_DataFlowsAfterDoBinary()
    {
      var prior = Client.SkipProactiveOptionNegotiation;
      try
      {
        Client.SkipProactiveOptionNegotiation = true;
        using var stream = new ScriptedStream();
        using var client = new Client(stream, new CancellationToken());
        stream.Enqueue(255, 253, 0, 65);
        (await ReadClientOnceAsync(client)).Should().Be("A");
        stream.ByteWrites.Should().ContainSingle().Which.Should().Equal(new byte[] { 255, 251, 0 });
      }
      finally { Client.SkipProactiveOptionNegotiation = prior; }
    }

    [Fact]
    public void Converter_HighCharsMapToSameBytes()
    {
      ByteStringConverter.ConvertStringToByteArray("\u00c8\u00ff")
        .Should().Equal(new byte[] { 200, 255, 255 });
    }

    [Fact]
    public void Converter_Latin1RoundTrips128To255()
    {
      var chars = new char[128];
      for (var i = 0; i < chars.Length; i++)
      {
        chars[i] = (char)(128 + i);
      }

      var text = new string(chars);
      var expected = new byte[129];
      for (var i = 0; i < 127; i++)
      {
        expected[i] = (byte)(128 + i);
      }

      expected[127] = 255;
      expected[128] = 255;
      ByteStringConverter.ConvertStringToByteArray(text).Should().Equal(expected);
      ByteStringConverter.ToString(expected[0..127]).Should().Be(text[0..127]);
    }
  }

  public class EchoTests
  {
    private static int CountWrites(ScriptedStream stream, byte verb, byte option)
    {
      return stream.ByteWrites.Count(b =>
        b.Length == 3 && b[0] == 255 && b[1] == verb && b[2] == option);
    }

    [Fact]
    public async Task SpontaneousWillEcho_RepliesDoAndTracksPeer()
    {
      using var stream = new ScriptedStream(255, 251, 1);
      using var cts = new CancellationTokenSource();
      using var sut = new ByteStreamHandler(stream, cts, 1);
      (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().BeEmpty();
      CountWrites(stream, 253, 1).Should().Be(1);
      sut.Negotiation.IsEnabledByPeer(1).Should().BeTrue();
    }

    [Fact]
    public async Task WillEcho_SuppressesLocalEchoFlag()
    {
      using var stream = new ScriptedStream(255, 251, 1);
      using var cts = new CancellationTokenSource();
      using var sut = new ByteStreamHandler(stream, cts, 1);
      sut.IsWriteConsole = true;
      sut.LocalEchoEnabled.Should().BeTrue();
      await sut.ReadAsync(TimeSpan.FromMilliseconds(50));
      sut.PeerEchoing.Should().BeTrue();
      sut.LocalEchoEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task ConsoleWrite_SuppressedAfterWillEcho()
    {
      using var stream = new ScriptedStream();
      using var cts = new CancellationTokenSource();
      using var sut = new ByteStreamHandler(stream, cts, 1);
      sut.IsWriteConsole = true;
      using var sink = new StringWriter();
      var prior = Console.Out;
      try
      {
        Console.SetOut(sink);
        stream.Enqueue(72, 105); // "Hi"
        (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().Be("Hi");
        sink.ToString().Should().Be("Hi");
        stream.Enqueue(255, 251, 1);
        (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().BeEmpty();
        stream.Enqueue(89, 111); // "Yo"
        (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().Be("Yo");
        sink.ToString().Should().Be("Hi");
      }
      finally { Console.SetOut(prior); }
    }

    [Fact]
    public async Task DoEcho_WithOptIn_RepliesWillAndTracksUs()
    {
      using var stream = new ScriptedStream(255, 253, 1);
      using var cts = new CancellationTokenSource();
      using var sut = new ByteStreamHandler(stream, cts, 1);
      sut.AllowRemoteEcho = true;
      (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().BeEmpty();
      CountWrites(stream, 251, 1).Should().Be(1);
      sut.Negotiation.IsEnabledByUs(1).Should().BeTrue();
    }

    [Fact]
    public async Task DoEcho_AfterWillEcho_EvenWithOptIn_RepliesWont()
    {
      using var stream = new ScriptedStream();
      using var cts = new CancellationTokenSource();
      using var sut = new ByteStreamHandler(stream, cts, 1);
      sut.AllowRemoteEcho = true;
      stream.Enqueue(255, 251, 1);
      await sut.ReadAsync(TimeSpan.FromMilliseconds(50));
      stream.Enqueue(255, 253, 1);
      (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().BeEmpty();
      CountWrites(stream, 253, 1).Should().Be(1);
      CountWrites(stream, 252, 1).Should().Be(1);
      CountWrites(stream, 251, 1).Should().Be(0);
    }

    [Fact]
    public async Task WillEcho_AfterAgreedDoEcho_RepliesDont()
    {
      using var stream = new ScriptedStream();
      using var cts = new CancellationTokenSource();
      using var sut = new ByteStreamHandler(stream, cts, 1);
      sut.AllowRemoteEcho = true;
      stream.Enqueue(255, 253, 1);
      await sut.ReadAsync(TimeSpan.FromMilliseconds(50));
      stream.Enqueue(255, 251, 1);
      (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().BeEmpty();
      CountWrites(stream, 251, 1).Should().Be(1);
      CountWrites(stream, 254, 1).Should().Be(1);
      CountWrites(stream, 253, 1).Should().Be(0);
    }

    [Fact]
    public async Task OptIn_EchoesReceivedBytesBack()
    {
      using var stream = new ScriptedStream();
      using var cts = new CancellationTokenSource();
      using var sut = new ByteStreamHandler(stream, cts, 1);
      sut.AllowRemoteEcho = true;
      stream.Enqueue(255, 253, 1);
      await sut.ReadAsync(TimeSpan.FromMilliseconds(50));
      stream.Enqueue(65, 66);
      (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().Be("AB");
      stream.ByteWrites.Should().Contain(b => b.SequenceEqual(new byte[] { 65, 66 }));
    }

    [Fact]
    public async Task NoOptIn_DataAroundDoEcho_IsNotEchoedBack()
    {
      using var stream = new ScriptedStream(65, 255, 253, 1, 66);
      using var cts = new CancellationTokenSource();
      using var sut = new ByteStreamHandler(stream, cts, 1);
      (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().Be("AB");
      // Only the WONT reply; the snapshot predates the mid-read agreement.
      stream.ByteWrites.Should().HaveCount(1);
      CountWrites(stream, 252, 1).Should().Be(1);
    }

    [Fact]
    public async Task OptIn_EchoBack_EscapesIac()
    {
      using var stream = new ScriptedStream();
      using var cts = new CancellationTokenSource();
      using var sut = new ByteStreamHandler(stream, cts, 1);
      sut.AllowRemoteEcho = true;
      stream.Enqueue(255, 253, 1);
      await sut.ReadAsync(TimeSpan.FromMilliseconds(50));
      stream.Enqueue(255, 255);
      (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().Be("\u00ff");
      stream.ByteWrites.Should().Contain(b => b.SequenceEqual(new byte[] { 255, 255 }));
    }

    [Fact]
    public async Task Client_WillEcho_SuppressesAcrossReads()
    {
      var prior = Client.SkipProactiveOptionNegotiation;
      try
      {
        Client.SkipProactiveOptionNegotiation = true;
        using var stream = new ScriptedStream();
        using var client = new Client(stream, new CancellationToken());
        stream.Enqueue(255, 251, 1);
        (await client.ReadAsync(TimeSpan.FromMilliseconds(100))).Should().BeEmpty();
        CountWrites(stream, 253, 1).Should().Be(1);
        client.Negotiation.IsEnabledByPeer(1).Should().BeTrue();
        stream.Enqueue(72, 105);
        (await client.ReadAsync(TimeSpan.FromMilliseconds(100))).Should().Be("Hi");
      }
      finally { Client.SkipProactiveOptionNegotiation = prior; }
    }

    [Fact]
    public async Task Client_DoEcho_OptIn_EchoesBack()
    {
      var prior = Client.SkipProactiveOptionNegotiation;
      try
      {
        Client.SkipProactiveOptionNegotiation = true;
        using var stream = new ScriptedStream();
        using var client = new Client(stream, new CancellationToken());
        client.Settings.AllowRemoteEcho = true;
        stream.Enqueue(255, 253, 1);
        (await client.ReadAsync(TimeSpan.FromMilliseconds(100))).Should().BeEmpty();
        CountWrites(stream, 251, 1).Should().Be(1);
        stream.Enqueue(65);
        (await client.ReadAsync(TimeSpan.FromMilliseconds(100))).Should().Be("A");
        stream.ByteWrites.Should().Contain(b => b.SequenceEqual(new byte[] { 65 }));
      }
      finally { Client.SkipProactiveOptionNegotiation = prior; }
    }

    [Fact]
    public async Task StatusSnapshot_ReportsAgreedEcho()
    {
      using var stream = new ScriptedStream();
      using var cts = new CancellationTokenSource();
      using var sut = new ByteStreamHandler(stream, cts, 1);
      stream.Enqueue(255, 251, 1);
      await sut.ReadAsync(TimeSpan.FromMilliseconds(50));
      stream.Enqueue(255, 250, 5, 1, 255, 240);
      (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().BeEmpty();
      // STATUS IS uses the RFC 859 bare-SE terminator (no IAC before SE).
      stream.ByteWrites.Should().Contain(b => b.SequenceEqual(new byte[] { 255, 250, 5, 0, 253, 1, 240 }));
    }
  }
}
