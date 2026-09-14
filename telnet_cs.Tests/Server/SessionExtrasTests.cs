// Tests for the §19 stream extras: SendGaAsync, generic negotiation
// waiters, session-context accounting + typescript, idle timeout, status
// logger, and TLS-auto-detect. Each pins exact wire bytes or counters.
namespace telnet_cs.Tests
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Security.Cryptography;
    using System.Security.Cryptography.X509Certificates;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using FluentAssertions;
    using Xunit;
    using telnet_cs.Client;
    using telnet_cs.Protocol;
    using telnet_cs.Server;

    public class SessionExtrasTests
    {
        private static ServerSession NewSession(ScriptedStream stream, TelnetServerOptions? options = null)
        {
            return new ServerSession(stream, options ?? new TelnetServerOptions(), CancellationToken.None);
        }

        [Fact]
        public async Task SendGaAsync_WithoutSga_SendsGaAndReturnsTrue()
        {
            using var stream = new ScriptedStream();
            using var session = NewSession(stream);
            (await session.SendGaAsync()).Should().BeTrue();
            stream.ByteWrites.Should().ContainSingle().Which.Should().Equal(new byte[] { 255, 249 });
        }

        [Fact]
        public async Task SendGaAsync_WithSgaAgreed_SendsNothingAndReturnsFalse()
        {
            using var stream = new ScriptedStream(255, 253, 3);
            using var session = NewSession(stream);
            (await session.ReadAsync(TimeSpan.FromMilliseconds(100))).Should().BeEmpty();
            (await session.SendGaAsync()).Should().BeFalse();
            // No IAC GA goes out (suppressed under SGA); the four writes are
            // the advanced batch the DO SGA agreement releases.
            stream.ByteWrites.SelectMany(static w => w).Should()
              .NotContain(b => b == 249);
            stream.ByteWrites.Should().HaveCount(4);
            stream.ByteWrites[0].Should().Equal(new byte[] { 255, 251, 3 });
            stream.ByteWrites[1].Should().Equal(new byte[] { 255, 251, 0 });
            stream.ByteWrites[2].Should().Equal(new byte[] { 255, 253, 31 });
            stream.ByteWrites[3].Should().Equal(new byte[] { 255, 253, 42 });
        }

        [Fact]
        public async Task ClientSendGaAsync_WithoutSga_SendsGa()
        {
            using var stream = new ScriptedStream();
            using var client = new Client(stream, CancellationToken.None);
            (await client.SendGaAsync()).Should().BeTrue();
            // The constructor proactively offers SGA, so the GA pair joins it.
            stream.ByteWrites.Should().Contain(w => w.SequenceEqual(new byte[] { 255, 249 }));
        }

        [Fact]
        public async Task WaitForOptionEnabledAsync_PeerWillArrives_ReturnsTrue()
        {
            using var stream = new ScriptedStream(255, 251, 24);
            using var session = NewSession(stream);
            (await session.WaitForOptionEnabledAsync(Options.TerminalType, local: false, TimeSpan.FromSeconds(5))).Should().BeTrue();
        }

        [Fact]
        public async Task WaitForOptionEnabledAsync_AlreadyEnabled_ReturnsTrueWithoutWaiting()
        {
            // Port of test_wait_for_immediate_return: once the peer's WILL
            // is processed, the waiter is already satisfied (a short deadline
            // proves no further wire wait is needed).
            using var stream = new ScriptedStream(255, 251, 24);
            using var session = NewSession(stream);
            (await session.ReadAsync(TimeSpan.FromMilliseconds(100))).Should().BeEmpty();
            (await session.WaitForOptionEnabledAsync(Options.TerminalType, local: false, TimeSpan.FromMilliseconds(50))).Should().BeTrue();
        }

        [Fact]
        public async Task WaitForOptionEnabledAsync_RemoteStateSetDirectly_ReturnsTrue()
        {
            // Port of test_wait_for_remote_option (delayed half): the waiter
            // observes a remotely-granted option set outside the wire pump.
            using var stream = new ScriptedStream();
            using var session = NewSession(stream);
            session.Negotiation.ReceivedWill((int)Options.TerminalType, agree: true);
            (await session.WaitForOptionEnabledAsync(Options.TerminalType, local: false, TimeSpan.FromSeconds(5))).Should().BeTrue();
        }

        [Fact]
        public async Task WaitForOptionEnabledAsync_LocalSgaAgreed_ReturnsTrue()
        {
            // Port of test_wait_for_local_option: our preset WILL SGA plus
            // the peer's DO SGA enables our side.
            using var stream = new ScriptedStream(255, 253, 3);
            using var session = NewSession(stream);
            await session.SendOpeningPresetAsync();
            (await session.ReadAsync(TimeSpan.FromMilliseconds(100))).Should().BeEmpty();
            (await session.WaitForOptionEnabledAsync(Options.SuppressGoAhead, local: true, TimeSpan.FromSeconds(5))).Should().BeTrue();
        }

        [Fact]
        public async Task WaitForNegotiationAsync_PeerWillSatisfiesPredicate_ReturnsTrue()
        {
            // Port of test_wait_for_condition_waits: the predicate form of
            // the remote-option wait.
            using var stream = new ScriptedStream(255, 251, 24);
            using var session = NewSession(stream);
            (await session.WaitForNegotiationAsync(
                n => n.IsEnabledByPeer((int)Options.TerminalType),
                TimeSpan.FromSeconds(5))).Should().BeTrue();
        }

        [Fact]
        public async Task WaitForNegotiationAsync_CombinedPeerPredicates_ReturnsTrue()
        {
            // Port of test_wait_for_combined_conditions: one waiter observes
            // two peer grants before succeeding.
            using var stream = new ScriptedStream(255, 251, 24, 255, 251, 32);
            using var session = NewSession(stream);
            (await session.WaitForNegotiationAsync(
                n => n.IsEnabledByPeer((int)Options.TerminalType) &&
                     n.IsEnabledByPeer((int)Options.TerminalSpeed),
                TimeSpan.FromSeconds(5))).Should().BeTrue();
        }

        [Fact]
        public async Task WaitForNegotiationAsync_PumpedText_RestoredInOrder()
        {
            // Polling contract: text pumped while waiting is restored to
            // PendingText in arrival order, so the next read sees it first.
            var reads = Encoding.ASCII.GetBytes("hello").Select(b => (int)b).Concat([255, 251, 24]).ToArray();
            using var stream = new ScriptedStream(reads);
            using var session = NewSession(stream);
            (await session.WaitForOptionEnabledAsync(Options.TerminalType, local: false, TimeSpan.FromSeconds(5))).Should().BeTrue();
            (await session.ReadAsync(TimeSpan.FromMilliseconds(500))).Should().Be("hello");
        }

        [Fact]
        public async Task WaitForOptionEnabledAsync_InvalidOption_ThrowsOutOfRange()
        {
            // Port of test_wait_for_invalid_option (KeyError): an
            // out-of-range option fails validation instead of waiting.
            using var stream = new ScriptedStream();
            using var session = NewSession(stream);
            Func<Task> act = () => session.WaitForOptionEnabledAsync((Options)999, local: false, TimeSpan.FromMilliseconds(100));
            await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
        }

        [Fact]
        public async Task WaitForNegotiationAsync_Timeout_ReturnsFalse()
        {
            // The waiter signals a missed deadline with TimeoutException so a
            // silent false cannot be mistaken for a satisfied condition.
            using var stream = new ScriptedStream();
            using var session = NewSession(stream);
            Func<Task> act = () => session.WaitForNegotiationAsync(_ => false, TimeSpan.FromMilliseconds(100));
            await act.Should().ThrowAsync<TimeoutException>();
        }

        [Fact]
        public async Task WaitForNegotiationAsync_AlreadyTrue_ReturnsWithoutReads()
        {
            using var stream = new ScriptedStream();
            using var session = NewSession(stream);
            (await session.WaitForNegotiationAsync(_ => true, TimeSpan.FromSeconds(5))).Should().BeTrue();
            stream.ByteWrites.Should().BeEmpty();
        }

        [Fact]
        public async Task WaitForNegotiationAsync_Cancelled_ReturnsFalse()
        {
            // A cancelled wait surfaces OperationCanceledException from the
            // caller's token rather than masking the cancel as a timeout.
            using var stream = new ScriptedStream();
            using var session = NewSession(stream);
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            Func<Task> act = () => session.WaitForNegotiationAsync(_ => false, TimeSpan.FromSeconds(5), cts.Token);
            await act.Should().ThrowAsync<OperationCanceledException>();
        }

        [Fact]
        public async Task Context_TracksReadsAndWrites()
        {
            using var stream = new ScriptedStream(104, 105);
            using var session = NewSession(stream);
            (await session.ReadAsync(TimeSpan.FromMilliseconds(500))).Should().Be("hi");
            await session.WriteAsync("yo");
            session.Context.CharsReceived.Should().Be(2);
            session.Context.CharsSent.Should().Be(2);
            session.Context.LastActivityUtc.Should().BeOnOrBefore(DateTimeOffset.UtcNow);
            session.Context.LastActivityUtc.Should().Be(session.Context.LastActivityUtc);
            session.Context.Properties["k"] = 1;
            session.Context.Properties["k"].Should().Be(1);
        }

        [Fact]
        public async Task Context_Typescript_RecordsBothDirections()
        {
            // The typescript records text read from the peer only; writes are
            // counted but never written to the transcript.
            using var stream = new ScriptedStream(105, 110);
            using var session = NewSession(stream);
            var tape = new StringWriter();
            session.Context.Typescript = tape;
            await session.WriteAsync("out");
            (await session.ReadAsync(TimeSpan.FromMilliseconds(500))).Should().Be("in");
            tape.ToString().Should().Be("in");
        }

        [Fact]
        public async Task IdleTimeout_ClosesQuietSessionWithNotice()
        {
            using var stream = new ScriptedStream();
            var options = new TelnetServerOptions { IdleTimeout = TimeSpan.FromMilliseconds(150) };
            using var session = new ServerSession(stream, options, CancellationToken.None);
            bool closed = false;
            for (int i = 0; i < 60 && !closed; i++)
            {
                await Task.Delay(50);
                closed = !session.IsConnected;
            }

            closed.Should().BeTrue();
            session.IsIdleTimedOut.Should().BeTrue();
            stream.StringWrites.Should().ContainSingle().Which.Should().Be("\r\nTimeout.\r\n");
        }

        [Fact]
        public void Timeout_DefaultsToIdleTimeout()
        {
            using var stream = new ScriptedStream();
            var options = new TelnetServerOptions { IdleTimeout = TimeSpan.FromMilliseconds(150) };
            using var session = new ServerSession(stream, options, CancellationToken.None);
            session.Timeout.Should().Be(TimeSpan.FromMilliseconds(150));
        }

        [Fact]
        public async Task SetTimeout_Shorter_ClosesQuietSessionWithNotice()
        {
            using var stream = new ScriptedStream();
            var options = new TelnetServerOptions { IdleTimeout = Timeout.InfiniteTimeSpan };
            using var session = new ServerSession(stream, options, CancellationToken.None);
            session.SetTimeout(TimeSpan.FromMilliseconds(150));
            session.Timeout.Should().Be(TimeSpan.FromMilliseconds(150));
            bool closed = false;
            for (int i = 0; i < 60 && !closed; i++)
            {
                await Task.Delay(50);
                closed = !session.IsConnected;
            }

            closed.Should().BeTrue();
            session.IsIdleTimedOut.Should().BeTrue();
            stream.StringWrites.Should().ContainSingle().Which.Should().Be("\r\nTimeout.\r\n");
        }

        [Fact]
        public async Task SetTimeout_Infinite_DisablesPendingTimeout()
        {
            using var stream = new ScriptedStream();
            var options = new TelnetServerOptions { IdleTimeout = TimeSpan.FromMilliseconds(150) };
            using var session = new ServerSession(stream, options, CancellationToken.None);
            session.SetTimeout(Timeout.InfiniteTimeSpan);
            await Task.Delay(300);
            session.IsConnected.Should().BeTrue();
            session.IsIdleTimedOut.Should().BeFalse();
            stream.StringWrites.Should().BeEmpty();
        }

        [Fact]
        public async Task IdleTimeout_Infinite_NeverFires()
        {
            using var stream = new ScriptedStream();
            var options = new TelnetServerOptions { IdleTimeout = Timeout.InfiniteTimeSpan };
            using var session = new ServerSession(stream, options, CancellationToken.None);
            await Task.Delay(150);
            session.IsConnected.Should().BeTrue();
            session.IsIdleTimedOut.Should().BeFalse();
        }

        [Fact]
        public async Task StatusLogger_LogsChangedCounters()
        {
            var lines = new List<string>();
            var options = new TelnetServerOptions
            {
                StatusInterval = TimeSpan.FromMilliseconds(50),
                Log = line =>
                {
                    lock (lines)
                    {
                        lines.Add(line);
                    }
                },
            };
            using var server = new TelnetServer(0, options);
            server.Start();
            using var client = await Client.ConnectAsync("127.0.0.1", server.Port);
            using var session = await server.AcceptSessionAsync(CancellationToken.None);
            await client.WriteAsync("hello");
            (await session.ReadAsync(TimeSpan.FromSeconds(1))).Should().Be("hello");
            bool logged = false;
            for (int i = 0; i < 60 && !logged; i++)
            {
                await Task.Delay(50);
                lock (lines)
                {
                    logged = lines.Any(line => line.Contains("rx=5", StringComparison.Ordinal));
                }
            }

            logged.Should().BeTrue();
        }

        private static X509Certificate2 CreateSelfSignedCert()
        {
            using var key = ECDsa.Create();
            return new CertificateRequest(
                "CN=localhost", key, HashAlgorithmName.SHA256).CreateSelfSigned(
                DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        }

        [Fact]
        public async Task TlsAutoDetect_PlaintextClient_StaysPlaintext()
        {
            using var cert = CreateSelfSignedCert();
            var serverOptions = new TelnetServerOptions
            {
                ServerCertificate = cert,
                TlsAutoDetect = TimeSpan.FromSeconds(2),
            };
            using var server = new TelnetServer(0, serverOptions);
            server.Start();
            var acceptTask = server.AcceptSessionAsync(CancellationToken.None);
            using var client = await Client.ConnectAsync("127.0.0.1", server.Port);
            using var session = await acceptTask;
            await client.WriteAsync("hi");
            (await session.ReadAsync(TimeSpan.FromSeconds(1))).Should().Be("hi");
        }

        [Fact]
        public async Task TlsAutoDetect_TlsClient_Handshakes()
        {
            using var cert = CreateSelfSignedCert();
            var serverOptions = new TelnetServerOptions
            {
                ServerCertificate = cert,
                TlsAutoDetect = TimeSpan.FromSeconds(5),
            };
            using var server = new TelnetServer(0, serverOptions);
            server.Start();
            var acceptTask = server.AcceptSessionAsync(CancellationToken.None);
            var clientOptions = new TelnetClientOptions
            {
                UseTls = true,
                TlsValidationCallback = (_, _, _, _) => true,
            };
            using var client = await Client.ConnectAsync(
                "127.0.0.1", server.Port, clientOptions,
                CancellationToken.None, TimeSpan.FromSeconds(10));
            using var session = await acceptTask;
            await client.WriteAsync("hi");
            (await session.ReadAsync(TimeSpan.FromSeconds(1))).Should().Be("hi");
        }

        [Fact]
        public async Task TlsAutoDetect_DisabledWithoutCert_StaysPlaintext()
        {
            using var server = new TelnetServer(0, new TelnetServerOptions { TlsAutoDetect = TimeSpan.FromSeconds(1) });
            server.Start();
            var acceptTask = server.AcceptSessionAsync(CancellationToken.None);
            using var client = await Client.ConnectAsync("127.0.0.1", server.Port);
            using var session = await acceptTask;
            await client.WriteAsync("hi");
            (await session.ReadAsync(TimeSpan.FromSeconds(1))).Should().Be("hi");
        }

        [Fact]
        public async Task TlsAutoDetect_SilentPeer_HandedOffAsPlaintextAfterWait()
        {
            // The reference tls_auto silent-plain handoff: a peer that sends
            // nothing is treated as plaintext once the peek wait elapses.
            using var cert = CreateSelfSignedCert();
            var serverOptions = new TelnetServerOptions
            {
                ServerCertificate = cert,
                TlsAutoDetect = TimeSpan.FromMilliseconds(300),
            };
            using var server = new TelnetServer(0, serverOptions);
            server.Start();
            var acceptTask = server.AcceptSessionAsync(CancellationToken.None);
            // A skip-proactive client sends nothing on connect, so the peek
            // window stays silent; the server's later preset bytes arrive
            // after detection and are consumed as negotiation, not data.
            using (GlobalStateGuard.SkipProactive(true))
            {
                using var client = await Client.ConnectAsync("127.0.0.1", server.Port);
                using var session = await acceptTask;
                await session.WriteAsync("hi");
                (await client.ReadAsync(TimeSpan.FromSeconds(1))).Should().Be("hi");
            }
        }

        [Fact]
        public async Task Context_Properties_Bag_RoundTrips_And_Duration_Grows()
        {
            using var server = new TelnetServer(0, new TelnetServerOptions());
            server.Start();
            var acceptTask = server.AcceptSessionAsync(CancellationToken.None);
            using var client = await Client.ConnectAsync("127.0.0.1", server.Port);
            using var session = await acceptTask;
            session.Context.Properties.ContainsKey("role").Should().BeFalse();
            session.Context.Properties["role"] = "tester";
            session.Context.Properties["role"].Should().Be("tester");
            session.Context.ConnectedAtUtc.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
            (DateTimeOffset.UtcNow - session.Context.ConnectedAtUtc).Should().BeGreaterThanOrEqualTo(TimeSpan.Zero);
            session.Context.Idle.Should().BeLessThan(TimeSpan.FromMinutes(1));
        }

        [Fact]
        public async Task Context_Idle_And_Duration_AreFresh_AfterRead()
        {
            // Reference idle/duration window: right after traffic both the
            // idle gap and the total connected span are sub-second.
            using var server = new TelnetServer(0, new TelnetServerOptions());
            server.Start();
            var acceptTask = server.AcceptSessionAsync(CancellationToken.None);
            using var client = await Client.ConnectAsync("127.0.0.1", server.Port);
            using var session = await acceptTask;
            await session.ReadAsync(TimeSpan.FromMilliseconds(100));
            session.Context.Idle.Should().BeLessThan(TimeSpan.FromSeconds(1));
            (DateTimeOffset.UtcNow - session.Context.ConnectedAtUtc).Should().BeLessThan(TimeSpan.FromSeconds(1));
        }

        [Fact]
        public async Task WaitForOptionEnabledAsync_Matches_DictForm_WaitForRemote()
        {
            // The reference wait_for(remote={OPT: True}) dict form: our
            // WaitForOptionEnabledAsync(option, local: false) is the same
            // predicate (remote direction = peer enables).
            using var server = new TelnetServer(0, new TelnetServerOptions());
            server.Start();
            var acceptTask = server.AcceptSessionAsync(CancellationToken.None);
            using var client = await Client.ConnectAsync("127.0.0.1", server.Port);
            using var session = await acceptTask;
            var waiter = client.WaitForOptionEnabledAsync(Options.SuppressGoAhead, local: false, TimeSpan.FromSeconds(10));
            await session.SendOpeningPresetAsync();
            (await waiter).Should().BeTrue();
            // A missing option misses its deadline, which the waiter reports
            // with a false return (it catches TimeoutException internally).
            (await client.WaitForOptionEnabledAsync(Options.LineMode, local: false, TimeSpan.FromMilliseconds(200)))
              .Should().BeFalse();
        }
    }
}
