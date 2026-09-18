// D2 line-limit pins (server side): the 64 KiB reference cap is now the
// TelnetServerOptions default, 0 disables, negatives die at Start, and a
// per-session override wins. Overlong input throws InvalidOperationException
// naming the limit, is consumed, and the next line reads clean (duplex).
namespace telnet_cs.Tests
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using FluentAssertions;
    using Xunit;
    using telnet_cs.Server;
    using telnet_cs.Transport;

    public class LineLimitTests
    {
        [Fact]
        public void DefaultOptions_Carry65536Limit()
        {
            new TelnetServerOptions().MaxTerminatedReadChars.Should().Be(65536);
        }

        [Fact]
        public async Task TerminatedReadAsync_OverlongLine_ThrowsNamingLimit()
        {
            using var session = new ServerSession(
                new ScriptedStream("AAAAAAAAAAA"),
                new TelnetServerOptions { MaxTerminatedReadChars = 10 },
                CancellationToken.None);
            await TerminatedReadLimitCases.OverlongLine_ThrowsNamingLimitAndSurvives(
                () => session.TerminatedReadAsync("\n", TimeSpan.FromSeconds(2)),
                () => session.IsConnected);
        }

        [Fact]
        public async Task TerminatedReadAsync_ZeroLimit_Disables()
        {
            string overlong = new string('A', 100) + "\n";
            using var session = new ServerSession(
                new ScriptedStream(overlong),
                new TelnetServerOptions { MaxTerminatedReadChars = 0 },
                CancellationToken.None);
            await TerminatedReadLimitCases.ZeroLimit_Disables(
                () => session.TerminatedReadAsync("\n", TimeSpan.FromSeconds(2)),
                overlong);
        }

        [Fact]
        public async Task TerminatedReadAsync_SessionOverride_BeatsOptions()
        {
            using var session = new ServerSession(
                new ScriptedStream("AAAAAAAAAAA"),
                new TelnetServerOptions { MaxTerminatedReadChars = 100 },
                CancellationToken.None);
            session.MaxTerminatedReadChars = 10;
            await TerminatedReadLimitCases.OverlongLine_ThrowsNamingLimitAndSurvives(
                () => session.TerminatedReadAsync("\n", TimeSpan.FromSeconds(2)),
                () => session.IsConnected);
        }

        [Fact]
        public async Task TerminatedReadAsync_SessionOverrideZero_DisablesOptionsLimit()
        {
            string overlong = new string('A', 100) + "\n";
            using var session = new ServerSession(
                new ScriptedStream(overlong),
                new TelnetServerOptions { MaxTerminatedReadChars = 10 },
                CancellationToken.None);
            session.MaxTerminatedReadChars = 0;
            await TerminatedReadLimitCases.ZeroLimit_Disables(
                () => session.TerminatedReadAsync("\n", TimeSpan.FromSeconds(2)),
                overlong);
        }

        [Fact]
        public void SessionOverride_Negative_ThrowsArgumentOutOfRange()
        {
            using var session = new ServerSession(
                new ScriptedStream(string.Empty),
                new TelnetServerOptions(),
                CancellationToken.None);
            TerminatedReadLimitCases.NegativeLimit_ThrowsArgumentOutOfRange(() => session.MaxTerminatedReadChars = -1);
        }

        [Fact]
        public void Start_NegativeLimit_ThrowsArgumentOutOfRange()
        {
            using var server = new TelnetServer(0, new TelnetServerOptions { MaxTerminatedReadChars = -1 });
            Assert.Throws<ArgumentOutOfRangeException>(() => server.Start());
        }

        [Fact]
        public async Task Duplex_ServerSideOverlong_ThrowsAndNextLineReadsClean()
        {
            var (clientStream, serverStream) = DuplexPipe.Create();
            using var session = new ServerSession(
                serverStream,
                new TelnetServerOptions { MaxTerminatedReadChars = 10 },
                CancellationToken.None);
            using var client = new telnet_cs.Client.Client(clientStream, CancellationToken.None);

            await client.WriteAsync("AAAAAAAAAAA", CancellationToken.None);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => session.TerminatedReadAsync("\n", TimeSpan.FromSeconds(5)));
            ex.Message.Should().Contain("10-character");
            session.IsConnected.Should().BeTrue("the session survives the overlong line");

            await client.WriteAsync("ok\n", CancellationToken.None);
            (await session.TerminatedReadAsync("\n", TimeSpan.FromSeconds(5))).Should().Be("ok\n");
        }
    }
}
