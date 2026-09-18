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
            using var client = await Client.CreateAsync(new ScriptedStream("AAAAAAAAAAA"), TimeSpan.FromSeconds(30), CancellationToken.None);
            client.ApplyOptions(new TelnetClientOptions { MaxTerminatedReadChars = 10 });
            await TerminatedReadLimitCases.OverlongLine_ThrowsNamingLimitAndSurvives(
                () => client.TerminatedReadAsync("\n", TimeSpan.FromSeconds(2)),
                () => client.IsConnected);
        }

        [Fact]
        public async Task TerminatedReadAsync_ZeroLimit_Disables()
        {
            string overlong = new string('A', 100) + "\n";
            using var client = await Client.CreateAsync(new ScriptedStream(overlong), TimeSpan.FromSeconds(30), CancellationToken.None);
            client.ApplyOptions(new TelnetClientOptions { MaxTerminatedReadChars = 0 });
            await TerminatedReadLimitCases.ZeroLimit_Disables(
                () => client.TerminatedReadAsync("\n", TimeSpan.FromSeconds(2)),
                overlong);
        }

        [Fact]
        public async Task TerminatedReadAsync_InstanceOverride_BeatsOptions()
        {
            using var client = await Client.CreateAsync(new ScriptedStream("AAAAAAAAAAA"), TimeSpan.FromSeconds(30), CancellationToken.None);
            client.ApplyOptions(new TelnetClientOptions { MaxTerminatedReadChars = 100 });
            client.MaxTerminatedReadChars = 10;
            await TerminatedReadLimitCases.OverlongLine_ThrowsNamingLimitAndSurvives(
                () => client.TerminatedReadAsync("\n", TimeSpan.FromSeconds(2)),
                () => client.IsConnected);
        }

        [Fact]
        public async Task InstanceOverride_Negative_ThrowsArgumentOutOfRange()
        {
            using var client = await Client.CreateAsync(new ScriptedStream(string.Empty), TimeSpan.FromSeconds(30), CancellationToken.None);
            TerminatedReadLimitCases.NegativeLimit_ThrowsArgumentOutOfRange(() => client.MaxTerminatedReadChars = -1);
        }

        [Fact]
        public async Task Duplex_ClientSideOverlong_ThrowsAndNextLineReadsClean()
        {
            var (clientStream, serverStream) = DuplexPipe.Create();
            using var session = new ServerSession(serverStream, new TelnetServerOptions(), CancellationToken.None);
            using var client = await Client.CreateAsync(clientStream, TimeSpan.FromSeconds(30), CancellationToken.None);
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
