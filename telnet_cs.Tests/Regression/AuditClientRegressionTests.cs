namespace telnet_cs.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Net;
    using System.Net.Sockets;
    using System.Text;
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Threading.Tasks;
    using FluentAssertions;
    using Xunit;
    using telnet_cs.Client;
    using telnet_cs.Transport;

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

            public void ExposeCancelPendingReads()
            {
                CancelPendingReads();
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
            using (GlobalStateGuard.TerminalType("vt100"))
            {
                using var stream = new ScriptedStream(255, 250, 24, 1, 255, 240);
                using var client = new Client(stream, new CancellationToken());
                client.Settings.TerminalType = "xterm";
                await client.ReadAsync(TimeSpan.FromMilliseconds(200));
                stream.ByteWrites.Should().HaveCount(2);
                stream.ByteWrites[1].Should().Equal(
                  new byte[] { 255, 250, 24, 0, 120, 116, 101, 114, 109, 255, 240 });
                Client.TerminalType.Should().Be("vt100");
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

        [Fact]
        public async Task ConnectAsyncConnectTimeout_ThrowsInvalidOperation()
        {
            // The reference connect_timeout shape: one timeout covers the
            // whole TCP+TLS connect, surfacing InvalidOperationException
            // (not SocketException — that is the fast-refusal path above).
            // 192.0.2.1 is TEST-NET-1 (RFC 5737): unroutable, so the
            // connect hangs until our timeout fires.
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Func<Task> act = () => Client.ConnectAsync("192.0.2.1", 2323, default, TimeSpan.FromSeconds(2));
            (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*within*");
            sw.Stop();
            sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30));
        }
    }
}
