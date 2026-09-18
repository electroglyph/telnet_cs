// D2 line-limit pins (client side): the TelnetClientOptions default is the
// historical 64 KiB, 0 disables, negatives die in ApplyOptions, and a
// per-instance override wins. Duplex proves the client throw site consumes
// the overlong line and the session survives.
namespace telnet_cs.Tests
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using FluentAssertions;
    using Xunit;
    using telnet_cs.Client;
    using telnet_cs.Server;
    using telnet_cs.Transport;

    public class ClientLineLimitTests
    {
        [Fact]
        public void DefaultOptions_Carry65536Limit()
        {
            new TelnetClientOptions().MaxTerminatedReadChars.Should().Be(65536);
        }

        [Fact]
        public async Task TerminatedReadAsync_OverlongLine_ThrowsNamingLimit()
        {
            using var client = new Client(new ScriptedStream("AAAAAAAAAAA"), CancellationToken.None);
            client.ApplyOptions(new TelnetClientOptions { MaxTerminatedReadChars = 10 });
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => client.TerminatedReadAsync("\n", TimeSpan.FromSeconds(2)));
            ex.Message.Should().Contain("10-character");
            client.IsConnected.Should().BeTrue();
        }

        [Fact]
        public async Task TerminatedReadAsync_ZeroLimit_Disables()
        {
            string overlong = new string('A', 100) + "\n";
            using var client = new Client(new ScriptedStream(overlong), CancellationToken.None);
            client.ApplyOptions(new TelnetClientOptions { MaxTerminatedReadChars = 0 });
            (await client.TerminatedReadAsync("\n", TimeSpan.FromSeconds(2))).Should().Be(overlong);
        }

        [Fact]
        public async Task TerminatedReadAsync_InstanceOverride_BeatsOptions()
        {
            using var client = new Client(new ScriptedStream("AAAAAAAAAAA"), CancellationToken.None);
            client.ApplyOptions(new TelnetClientOptions { MaxTerminatedReadChars = 100 });
            client.MaxTerminatedReadChars = 10;
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => client.TerminatedReadAsync("\n", TimeSpan.FromSeconds(2)));
            ex.Message.Should().Contain("10-character");
        }

        [Fact]
        public void InstanceOverride_Negative_ThrowsArgumentOutOfRange()
        {
            using var client = new Client(new ScriptedStream(string.Empty), CancellationToken.None);
            Assert.Throws<ArgumentOutOfRangeException>(() => client.MaxTerminatedReadChars = -1);
        }

        [Fact]
        public async Task Duplex_ClientSideOverlong_ThrowsAndNextLineReadsClean()
        {
            var (clientStream, serverStream) = DuplexPipe.Create();
            using var session = new ServerSession(serverStream, new TelnetServerOptions(), CancellationToken.None);
            using var client = new Client(clientStream, CancellationToken.None);
            client.MaxTerminatedReadChars = 10;

            await session.WriteAsync("AAAAAAAAAAA", CancellationToken.None);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => client.TerminatedReadAsync("\n", TimeSpan.FromSeconds(5)));
            ex.Message.Should().Contain("10-character");
            client.IsConnected.Should().BeTrue("the client survives the overlong line");

            await session.WriteAsync("ok\n", CancellationToken.None);
            (await client.TerminatedReadAsync("\n", TimeSpan.FromSeconds(5))).Should().Be("ok\n");
        }
    }
}
