namespace telnet_cs.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Net.Security;
    using System.Net.Sockets;
    using System.Security.Authentication;
    using System.Security.Cryptography.X509Certificates;
    using System.Text;
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Threading.Tasks;
    using FakeItEasy;
    using FluentAssertions;
    using Xunit;
    using telnet_cs.Client;
    using telnet_cs.Protocol;
    using telnet_cs.Transport;

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
            Action act = () => new Client(null!, new CancellationToken());
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
            using (GlobalStateGuard.SkipProactive(false))
            {
                var fake = ConnectedFake();
                using var _ = new Client(fake, TimeSpan.FromMilliseconds(10), default);
                A.CallTo(() => fake.WriteAsync(A<byte[]>.Ignored, 0, 3, A<CancellationToken>.Ignored))
                  .WhenArgumentsMatch(o => o[0] is byte[] b && b.SequenceEqual(Client.SuppressGoAheadBuffer))
                  .MustHaveHappened();
            }
            await Task.CompletedTask;
        }

        [Fact]
        public async Task CtorSkipNegotiationSendsNothing()
        {
            using (GlobalStateGuard.SkipProactive(true))
            {
                var fake = ConnectedFake();
                using var _ = new Client(fake, TimeSpan.FromMilliseconds(10), default);
                A.CallTo(() => fake.WriteAsync(A<byte[]>.Ignored, A<int>.Ignored, A<int>.Ignored, A<CancellationToken>.Ignored)).MustNotHaveHappened();
            }
            await Task.CompletedTask;
        }

        [Fact]
        public async Task CtorCustomOptionsSendExactTriplesInOrder()
        {
            using (GlobalStateGuard.SkipProactive(true))
            {
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
            await Task.CompletedTask;
        }

        [Fact]
        public async Task WriteLineAppendsRfc854FeedByDefault()
        {
            var fake = ConnectedFake();
            using var sut = new Client(fake, TimeSpan.FromMilliseconds(10), default);
            A.CallTo(() => fake.WriteAsync(A<byte[]>.Ignored, A<int>.Ignored, A<int>.Ignored, A<CancellationToken>.Ignored));
            await sut.WriteLineAsync("cmd");
            A.CallTo(() => fake.WriteAsync("cmd\r\n", A<CancellationToken>.Ignored)).MustHaveHappened();
        }

        [Fact]
        public async Task WriteLineExplicitLegacyFeedSendsBareLf()
        {
            var fake = ConnectedFake();
            using var sut = new Client(fake, TimeSpan.FromMilliseconds(10), default);
            A.CallTo(() => fake.WriteAsync(A<byte[]>.Ignored, A<int>.Ignored, A<int>.Ignored, A<CancellationToken>.Ignored));
            await sut.WriteLineAsync("cmd", Client.LegacyLineFeed);
            A.CallTo(() => fake.WriteAsync("cmd\n", A<CancellationToken>.Ignored)).MustHaveHappened();
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
            await Task.Delay(20); // let Register(CancelPendingReads) propagate
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
            // An empty terminator is meaningless (String.IndexOf("") is 0,
            // yet the located-check never matches ""), so the reader fails
            // fast like telnetlib3's readuntil, which raises ValueError on
            // an empty separator.
            using var stream = new DummyByteStream();
            using var sut = new Client(stream, new CancellationToken());
            Func<Task<string>> read = () => sut.TerminatedReadAsync(string.Empty, TimeSpan.FromMilliseconds(500), 1);
            await read.Should().ThrowAsync<ArgumentException>();
        }

        [Fact]
        public async Task TerminatedRead_PipelinedData_TruncatesAndStashesRemainder()
        {
            using var stream = new ScriptedStream("AB:CD:");
            using var sut = new Client(stream, new CancellationToken());
            (await sut.TerminatedReadAsync(":", TimeSpan.FromMilliseconds(500), 1)).Should().Be("AB:");
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(100))).Should().Be("CD:");
        }

        [Fact]
        public async Task TerminatedRead_MultipleTerminators_CutsAtEarliest()
        {
            using var stream = new ScriptedStream("A;B:C");
            using var sut = new Client(stream, new CancellationToken());
            var terminators = new List<string> { ":", ";" };
            (await sut.TerminatedReadAsync(terminators, TimeSpan.FromMilliseconds(500), 1)).Should().Be("A;");
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(100))).Should().Be("B:C");
        }

        [Fact]
        public async Task TerminatedRead_Regex_CutsAtMatchEnd()
        {
            using var stream = new ScriptedStream("AB12CD");
            using var sut = new Client(stream, new CancellationToken());
            (await sut.TerminatedReadAsync(new Regex(@"\d+"), TimeSpan.FromMilliseconds(500), 1)).Should().Be("AB12");
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(100))).Should().Be("CD");
        }

        [Fact]
        public async Task TerminatedRead_Regex_MultiPromptChain()
        {
            // Port of test_telnet_reader_readuntil_pattern_success: the
            // Router>/Router#/Router(config)# banner chain with re \S+[>#].
            using var stream = new ScriptedStream("Router> enable\nRouter#");
            using var sut = new Client(stream, new CancellationToken());
            var prompt = new Regex(@"\S+[>#]");
            (await sut.TerminatedReadAsync(prompt, TimeSpan.FromMilliseconds(500), 1)).Should().Be("Router>");
            (await sut.TerminatedReadAsync(prompt, TimeSpan.FromMilliseconds(500), 1)).Should().Be(" enable\nRouter#");
        }

        [Fact]
        public async Task TerminatedRead_Regex_CutsBeforeRemainder()
        {
            // Port of test_readuntil_pattern_success_and_eof_incomplete
            // (success half): "aaXYZbb" with pattern XYZ.
            using var stream = new ScriptedStream("aaXYZbb");
            using var sut = new Client(stream, new CancellationToken());
            (await sut.TerminatedReadAsync(new Regex("XYZ"), TimeSpan.FromMilliseconds(500), 1)).Should().Be("aaXYZ");
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(100))).Should().Be("bb");
        }

        [Fact]
        public async Task TerminatedRead_PipelinedNewline_LeavesRest()
        {
            // Port of test_readuntil_success_consumes_and_returns:
            // feed "abc\nrest", take "abc\n", buffer keeps "rest".
            using var stream = new ScriptedStream("abc\nrest");
            using var sut = new Client(stream, new CancellationToken());
            (await sut.TerminatedReadAsync("\n", TimeSpan.FromMilliseconds(500), 1)).Should().Be("abc\n");
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(100))).Should().Be("rest");
        }

        [Fact]
        public async Task ReadAsync_DataArrivingMidWait_IsReturned()
        {
            // Port of test_read_until_wait_path_then_data_arrives: a blocked
            // read resolves with bytes enqueued mid-wait.
            using var stream = new ScriptedStream();
            using var sut = new Client(stream, new CancellationToken());
            var pending = sut.ReadAsync(TimeSpan.FromSeconds(5));
            stream.Enqueue(120, 121, 122);
            (await pending).Should().Be("xyz");
        }

        [Fact]
        public async Task TerminatedRead_NullRegex_RejectsWithoutReading()
        {
            // Rejection half of test_telnet_reader_readuntil_pattern_invalid_arguments
            // and test_readuntil_pattern_invalid_types: a null pattern is
            // refused (ArgumentNullException:regex, cf. ValueError) before any
            // stream I/O happens.
            var fake = A.Fake<IByteStream>();
            A.CallTo(() => fake.Connected).Returns(true);
            using var sut = new Client(fake, TimeSpan.FromMilliseconds(1), default) { MillisecondReadDelay = 1 };
            Func<Task> act = () => sut.TerminatedReadAsync((Regex)null!, TimeSpan.FromMilliseconds(60), 1);
            await act.Should().ThrowAsync<ArgumentNullException>().WithParameterName("regex");
            A.CallTo(() => fake.ReadByte()).MustNotHaveHappened();
        }

        [Fact]
        public async Task TerminatedRead_Unterminated_StashesNothing()
        {
            using var stream = new ScriptedStream("AB");
            using var sut = new Client(stream, new CancellationToken());
            (await sut.TerminatedReadAsync(":", TimeSpan.FromMilliseconds(200), 1)).Should().Be("AB");
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(100))).Should().BeEmpty();
        }

        [Fact]
        public async Task TerminatedRead_TrailingSpaceTerminator_Matches()
        {
            // A terminator ending in a space must match itself: the read
            // below would stall to timeout under a trimmed-only check.
            using var stream = new ScriptedStream("Account: Password: > ");
            using var sut = new Client(stream, new CancellationToken());
            (await sut.TerminatedReadAsync("Account:", TimeSpan.FromMilliseconds(500))).Should().Be("Account:");
            (await sut.TerminatedReadAsync("Password:", TimeSpan.FromMilliseconds(500))).Should().Contain("Password:");
            (await sut.TerminatedReadAsync("> ", TimeSpan.FromMilliseconds(500))).Should().EndWith("> ");
        }

        [Fact]
        public async Task ReadAsync_SocketException_ReturnsEmpty()
        {
            // A reset connection is a dead peer like any other read-path
            // death: empty, not an error. The gate is wedged open so the
            // throwing ReadByte is actually reached.
            var fake = A.Fake<IByteStream>();
            A.CallTo(() => fake.Connected).Returns(true);
            A.CallTo(() => fake.Available).Returns(1);
            A.CallTo(() => fake.ReadByte()).Throws(new SocketException((int)SocketError.ConnectionReset));
            using var sut = new Client(fake, TimeSpan.FromMilliseconds(1), default) { MillisecondReadDelay = 1 };
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(500))).Should().BeEmpty();
        }

        [Fact]
        public void ApplyOptions_CopiesEveryMember()
        {
            // Anti-drop guard for the with-clone: every member set here must
            // arrive on Settings. (Future members flow structurally via the
            // compiler-generated clone; this test documents the contract.)
            var log = new List<string>();
            bool Validate(object _, X509Certificate? __, X509Chain? ___, SslPolicyErrors ____) => true;
            var certs = new X509CertificateCollection();
            var options = new TelnetClientOptions
            {
                TerminalType = "xterm",
                TerminalSpeed = "9600,9600",
                XDisplayLocation = "host:0",
                IsWriteConsole = true,
                AllowRemoteEcho = true,
                EnableBell = false,
                EnableMudOptions = true,
                TextEncoding = Encoding.Latin1,
                WindowWidth = 100,
                WindowHeight = 40,
                Log = log.Add,
                EnvironmentUser = "bob",
                EnvironmentDisplay = "host:0",
                UseTls = true,
                TlsHost = "example.com",
                TlsValidationCallback = Validate,
                TlsClientCertificates = certs,
                TlsProtocols = SslProtocols.Tls13,
            };
            options.TerminalTypes.Add("a");
            options.EnvironmentUserVars["K"] = "v";

            using var sut = new Client(ConnectedFake(), TimeSpan.FromMilliseconds(1), default);
            sut.ApplyOptions(options);

            sut.Settings.TerminalType.Should().Be("xterm");
            sut.Settings.TerminalTypes.Should().Equal("a");
            sut.Settings.TerminalSpeed.Should().Be("9600,9600");
            sut.Settings.XDisplayLocation.Should().Be("host:0");
            sut.Settings.IsWriteConsole.Should().BeTrue();
            sut.Settings.AllowRemoteEcho.Should().BeTrue();
            sut.Settings.EnableBell.Should().BeFalse();
            sut.Settings.EnableMudOptions.Should().BeTrue();
            sut.Settings.TextEncoding.Should().BeSameAs(Encoding.Latin1);
            sut.Settings.WindowWidth.Should().Be(100);
            sut.Settings.WindowHeight.Should().Be(40);
            sut.Settings.Log.Should().NotBeNull();
            sut.Settings.EnvironmentUser.Should().Be("bob");
            sut.Settings.EnvironmentDisplay.Should().Be("host:0");
            sut.Settings.EnvironmentUserVars.Should().Contain("K", "v");
            sut.Settings.UseTls.Should().BeTrue();
            sut.Settings.TlsHost.Should().Be("example.com");
            sut.Settings.TlsValidationCallback.Should().NotBeNull();
            // The method documents collections as re-seated, not shared, so the
            // mutable certificate collection is defensively copied with equal content.
            sut.Settings.TlsClientCertificates.Should().NotBeSameAs(certs);
            sut.Settings.TlsClientCertificates.Should().BeEquivalentTo(certs);
            sut.Settings.TlsProtocols.Should().Be(SslProtocols.Tls13);
        }

        [Fact]
        public void ApplyOptions_CollectionsAreCopiesNotAliases()
        {
            var options = new TelnetClientOptions();
            options.TerminalTypes.Add("a");
            options.EnvironmentUserVars["K"] = "v";
            using var sut = new Client(ConnectedFake(), TimeSpan.FromMilliseconds(1), default);
            sut.ApplyOptions(options);

            options.TerminalTypes.Add("b");
            options.EnvironmentUserVars["K2"] = "v2";
            sut.Settings.TerminalTypes.Should().Equal("a");
            sut.Settings.EnvironmentUserVars.Should().ContainSingle().Which.Should().Be(new KeyValuePair<string, string>("K", "v"));

            sut.Settings.TerminalTypes.Add("c");
            sut.Settings.EnvironmentUserVars["K3"] = "v3";
            options.TerminalTypes.Should().Equal("a", "b");
            options.EnvironmentUserVars.Should().HaveCount(2);
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
        public async Task TerminatedRead_ReturnsPromptlyWhenNoTerminator()
        {
            var fake = A.Fake<IByteStream>();
            A.CallTo(() => fake.Connected).Returns(true);
            using var sut = new Client(fake, TimeSpan.FromMilliseconds(1), default) { MillisecondReadDelay = 1 };
            (await sut.TerminatedReadAsync(":", TimeSpan.FromMilliseconds(60), 1)).Should().BeEmpty();
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
        public async Task WriteAsync_ByteArray_DoublesIacOnTheWire()
        {
            // Port of test_write_escapes_iac_and_send_iac_verbatim, data half:
            // a literal IAC in outbound user data is escaped by doubling.
            using (GlobalStateGuard.SkipProactive(true))
            {
                using var stream = new ScriptedStream();
                using var sut = new Client(stream, TimeSpan.FromMilliseconds(50), default);
                await sut.WriteAsync(new byte[] { 65, 255, 66 });
                stream.ByteWrites.Should().ContainSingle().Which.Should().Equal(new byte[] { 65, 255, 255, 66 });
            }
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

        [Fact]
        public async Task ConsecutiveWriteAsync_CallsConcatenateToAbcd()
        {
            // Port of test_writelines (bytes + unicode halves): sequential
            // string and byte writes land on the wire in order, ASCII
            // byte-identical (IAC doubling lives in ByteStringConverter).
            // (The scripted fake records string and byte writes on separate
            // lists, so both are pinned; a real stream serialises "abcd".)
            using (GlobalStateGuard.SkipProactive(true))
            {
                using var stream = new ScriptedStream();
                using var sut = new Client(stream, TimeSpan.FromMilliseconds(50), default);
                await sut.WriteAsync("a");
                await sut.WriteAsync("b");
                await sut.WriteAsync(new byte[] { (byte)'c', (byte)'d' });
                stream.StringWrites.Should().Equal("a", "b");
                stream.ByteWrites.Should().ContainSingle().Which.Should().Equal(new byte[] { (byte)'c', (byte)'d' });
            }
        }

        [Fact]
        public async Task ReadAsync_AfterStreamClose_ReturnsEmpty()
        {
            // Port of test_telnet_client_open_close_by_write (read half): a
            // closed stream reads empty and IsConnected follows the stream.
            using (GlobalStateGuard.SkipProactive(true))
            {
                using var stream = new ScriptedStream();
                using var sut = new Client(stream, TimeSpan.FromMilliseconds(50), default);
                stream.Close();
                (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().BeEmpty();
                sut.IsConnected.Should().BeFalse();
            }
        }
    }
}
