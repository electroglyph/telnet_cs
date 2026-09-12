namespace telnet_cs.Tests
{
    using System;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using FluentAssertions;
    using Xunit;
    using telnet_cs.Server;

    public class AuditServerTests
    {
        private const int Iac = 255;
        private const int Sb = 250;
        private const int Se = 240;

        private static ServerSession NewSession(ScriptedStream stream)
        {
            return new ServerSession(stream, new TelnetServerOptions(), CancellationToken.None);
        }

        [Fact]
        public async Task TerminatedRead_TruncatesAtFirstTerminator()
        {
            using var stream = new ScriptedStream("hi\nrest");
            using var session = NewSession(stream);
            (await session.TerminatedReadAsync("\n", TimeSpan.FromSeconds(2))).Should().Be("hi\n");
        }

        [Fact]
        public async Task TerminatedRead_StashesRemainderForNextRead()
        {
            using var stream = new ScriptedStream("hi\nrest");
            using var session = NewSession(stream);
            (await session.TerminatedReadAsync("\n", TimeSpan.FromSeconds(2))).Should().Be("hi\n");
            (await session.TerminatedReadAsync("\n", TimeSpan.FromMilliseconds(200))).Should().Be("rest");
        }

        [Fact]
        public async Task WriteBytes_UpdatesSentAccounting()
        {
            using var stream = new ScriptedStream();
            using var session = NewSession(stream);
            await session.WriteAsync(new byte[] { 65, 66, 67 });
            session.Context.CharsSent.Should().BeGreaterThan(0);
        }

        [Fact]
        public async Task OldEnvironRequest_IgnoresNewVariantIs()
        {
            using var stream = new ScriptedStream(Iac, Sb, 39, 0, 0, 65, 1, 98, Iac, Se);
            using var session = NewSession(stream);
            (await session.RequestEnvironmentAsync(TimeSpan.FromMilliseconds(400))).Should().BeEmpty();
            session.ClientNewEnvironment.Should().BeEmpty();
        }

        [Fact]
        public async Task CharsetAccepted_EmptyName_IsNotLatched()
        {
            using var stream = new ScriptedStream(Iac, Sb, 42, 2, Iac, Se);
            using var session = NewSession(stream);
            (await session.RequestCharsetAsync(TimeSpan.FromSeconds(2))).Should().BeNull();
            session.ClientCharset.Should().BeNull();
        }

        [Fact]
        public async Task XDisplayRequest_WhileOutstanding_DoesNotSendTwice()
        {
            using var stream = new ScriptedStream();
            using var session = NewSession(stream);
            var first = session.RequestXDisplayAsync(TimeSpan.FromMilliseconds(500));
            await Task.Delay(100);
            var second = session.RequestXDisplayAsync(TimeSpan.FromMilliseconds(300));
            await Task.WhenAll(first, second);
            stream.ByteWrites.Should().HaveCount(1);
        }

        [Fact]
        public void SessionTimeout_FollowsLiveOptions()
        {
            using var stream = new ScriptedStream();
            var options = new TelnetServerOptions();
            using var session = new ServerSession(stream, options, CancellationToken.None);
            options.IdleTimeout = TimeSpan.FromMinutes(42);
            session.Timeout.Should().Be(TimeSpan.FromMinutes(42));
        }

        [Fact]
        public void ServerStop_ClearsBoundPort()
        {
            using var server = new TelnetServer(0);
            server.Start();
            server.Port.Should().BeGreaterThan(0);
            server.Stop();
            server.Port.Should().Be(0);
        }
    }
}
