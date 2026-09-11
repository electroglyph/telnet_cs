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
            stream.ByteWrites.Should().ContainSingle().Which.Should().Equal(new byte[] { 255, 251, 3 });
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
        public async Task WaitForNegotiationAsync_Timeout_ReturnsFalse()
        {
            using var stream = new ScriptedStream();
            using var session = NewSession(stream);
            (await session.WaitForNegotiationAsync(_ => false, TimeSpan.FromMilliseconds(100))).Should().BeFalse();
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
            using var stream = new ScriptedStream();
            using var session = NewSession(stream);
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            (await session.WaitForNegotiationAsync(_ => false, TimeSpan.FromSeconds(5), cts.Token)).Should().BeFalse();
        }

        [Fact]
        public async Task Context_TracksReadsAndWrites()
        {
            using var stream = new ScriptedStream(104, 105);
            using var session = NewSession(stream);
            (await session.ReadAsync(TimeSpan.FromSeconds(2))).Should().Be("hi");
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
            using var stream = new ScriptedStream(105, 110);
            using var session = NewSession(stream);
            var tape = new StringWriter();
            session.Context.Typescript = tape;
            await session.WriteAsync("out");
            (await session.ReadAsync(TimeSpan.FromSeconds(2))).Should().Be("in");
            tape.ToString().Should().Be("outin");
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
            stream.StringWrites.Should().ContainSingle().Which.Should().Contain("Timeout.");
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
            (await session.ReadAsync(TimeSpan.FromSeconds(5))).Should().Be("hello");
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
            (await session.ReadAsync(TimeSpan.FromSeconds(5))).Should().Be("hi");
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
            (await session.ReadAsync(TimeSpan.FromSeconds(5))).Should().Be("hi");
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
            (await session.ReadAsync(TimeSpan.FromSeconds(5))).Should().Be("hi");
        }
    }
}
